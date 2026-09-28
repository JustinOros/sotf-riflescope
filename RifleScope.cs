using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using RedLoader.Utils;
using Sons.FieldOfView;
using Sons.Items.Core;
using Sons.Weapon;
using SonsSdk;
using SonsSdk.Attributes;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RifleScope;

public class RifleScope : SonsMod
{
    private const float DefaultZoom = 5f;
    private const float MinZoom = 1f;
    private const float MaxZoom = 50f;
    private const float DefaultTrimCm = 60f;
    private const float MaxTrimCm = 200f;
    private const float MaxWindCm = 200f;
    private const float TrimReferenceRange = 50f;
    private const string SuppressorModName = "CompactPistolSuppressorMod";
    private const string SuppressedFireEvent = "event:/SotF Events/player sounds/Weapons/PistolTactical/PistolTacticalFire";
    private const string MuzzleName = "RifleScopeMuzzle";
    private const string RifleFireEvent = "event:/SotF Events/player sounds/Weapons/Rifle/rifle_fire";
    private const string UpgradeParameter = "upgrade";
    private const float SuppressedUpgradeValue = 0.2f;
    private const int MilDotCount = 10;
    private const float MilDotSize = 0.4f;
    private const float MinDotPx = 7f;
    private const float MinDotSpacingPx = 14f;
    private const float ThinLinePx = 2f;
    private const float PostPx = 8f;
    private const string RedDotShader = "Sons/RedDot HLSL";
    private const int MaskSize = 1024;
    private const float MaxRayDistance = 2000f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<IntPtr, float> OriginalOffsets = new();
    private static readonly Dictionary<int, Material> HiddenMaterials = new();
    private static readonly HashSet<int> HiddenIds = new();

    private static float _zoom = DefaultZoom;
    private static float _trimCm = DefaultTrimCm;
    private static float _windCm;
    private static bool _debug;
    private static bool _enabled = true;
    private static bool _suppressor;
    private static string _originalFireEvent;
    private static HarmonyLib.Harmony _audioHarmony;
    private static bool _loggedParamError;
    private static bool _loggedPistolParams;
    private static bool _reissuing;
    private static readonly List<(Renderer Renderer, int Slot, Material Original)> RedDotSlots = new();
    private static string _configPath;

    private static RifleAnimatorController _rifle;
    private static RifleAnimatorController _pendingRifle;
    private static WeaponMod _suppressorMod;
    private static float _hipFov;
    private static float _vanillaAimFov = 35f;
    private static float _targetAimFov;
    private static int _heldLayer = -1;
    private static Camera _hiddenCam;
    private static Transform _projectile;
    private static ProjectileTransformConstraint _steeredConstraint;
    private static Quaternion _originalShotOffset;
    private static bool _steered;

    private static GameObject _overlay;
    private static RectTransform _scopeRect;
    private static RectTransform _crosshairRect;
    private static RectTransform _lineH;
    private static RectTransform _lineV;
    private static readonly List<(RectTransform Rect, Vector2 Dir)> Posts = new();
    private static float _layoutH = -1f;
    private static readonly List<(RectTransform Rect, Vector2 Dir)> MilDots = new();
    private static RectTransform _leftBar;
    private static RectTransform _rightBar;
    private static TextMeshProUGUI _rangeText;
    private static string _lastRange;
    private static int _milsPerDot = 1;

    public RifleScope()
    {
        OnUpdateCallback = OnUpdate;
        HarmonyPatchAll = true;
    }

    protected override void OnSdkInitialized()
    {
        _configPath = Path.Combine(LoaderEnvironment.UserDataDirectory, "RifleScope.txt");
        Load();
        RLog.Msg($"RifleScope loaded. Zoom {Fmt(_zoom)}x the vanilla scope. Change it with: scopezoom <{Fmt(MinZoom)}-{Fmt(MaxZoom)}>");
    }

    private void OnUpdate()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            RLog.Error($"RifleScope error: {e.Message}");
            _rifle = null;
            SetZoomed(false, null);
        }
    }

    private static void Tick()
    {
        if (!_rifle || !_rifle.gameObject.activeInHierarchy)
        {
            if (_rifle || _hiddenCam)
            {
                _rifle = null;
                SetZoomed(false, null);
            }

            var pending = _pendingRifle;
            _pendingRifle = null;
            if (!pending || !pending.gameObject.activeInHierarchy)
                return;
            AdoptRifle(pending);
        }

        if (!_enabled)
            return;

        var manager = _rifle._cameraFovManager;
        if (!manager)
        {
            SetZoomed(false, null);
            return;
        }

        UpdateOffset(manager);

        var cam = manager._targetCamera;
        var zoomed = _rifle.IsAiming && cam && _zoom > 1.01f && cam.fieldOfView < _vanillaAimFov - 0.5f;
        SetZoomed(zoomed, cam);
    }

    internal static void OnWeaponTick(RangedWeaponController controller)
    {
        if (!controller || (_rifle && _rifle.Pointer == controller.Pointer))
            return;
        var rifle = controller.TryCast<RifleAnimatorController>();
        if (rifle && rifle.IsLocalPlayer())
            _pendingRifle = rifle;
    }

    private static void AdoptRifle(RifleAnimatorController rifle)
    {
        _rifle = rifle;
        _projectile = FindProjectileTransform(rifle);
        _originalFireEvent ??= rifle._gunShotAudioEvent;
        if (_enabled)
        {
            if (!_overlay)
                CreateOverlay();
            HideRedDot(rifle);
        }
        if (_suppressor)
            SetSuppressor(rifle, true);
    }

    private static WeaponMod FindSuppressorMod()
    {
        if (_suppressorMod)
            return _suppressorMod;
        foreach (var mod in Resources.FindObjectsOfTypeAll<WeaponMod>())
        {
            if (mod && mod.name == SuppressorModName)
            {
                _suppressorMod = mod;
                return mod;
            }
        }
        return null;
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == name)
                return t;
        }
        return null;
    }

    private static bool EnsureMuzzleSlot(RangedWeaponController rifle, WeaponMods mods)
    {
        var links = mods._modLocations;
        for (var i = 0; i < links.Count; i++)
        {
            if (links[i].Slot == WeaponMod.Slot.Muzzle)
                return true;
        }

        var anchor = FindChild(rifle.transform, "ProjectileVisualTransform");
        if (!anchor)
            return false;

        var muzzle = FindChild(anchor.parent, MuzzleName);
        if (!muzzle)
        {
            var go = new GameObject(MuzzleName);
            go.layer = anchor.gameObject.layer;
            muzzle = go.transform;
            muzzle.SetParent(anchor.parent, false);
            muzzle.localPosition = anchor.localPosition;
            muzzle.localRotation = Quaternion.identity;
            muzzle.localScale = Vector3.one;
        }

        var link = new WeaponMods.ModLink();
        link.Slot = WeaponMod.Slot.Muzzle;
        link.Location = muzzle;
        links.Add(link);
        return true;
    }

    private static Type FindGameType(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var asm in assemblies)
        {
            try
            {
                var t = asm.GetType(name, false) ?? asm.GetType("Il2Cpp." + name, false);
                if (t != null)
                    return t;
            }
            catch
            {
            }
        }

        foreach (var asm in assemblies)
        {
            var asmName = asm.GetName().Name ?? string.Empty;
            if (asm.IsDynamic || asmName.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("Il2Cppmscorlib", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
            {
                if (t != null && t.Name == name)
                    return t;
            }
        }
        return null;
    }

    private static void InstallAudioPatches()
    {
        if (_audioHarmony != null)
            return;
        _audioHarmony = new HarmonyLib.Harmony("RifleScope.SuppressorAudio");

        var handler = FindGameType("FMOD_AnimationEventHandler");
        var animPrefix = new HarmonyMethod(typeof(RifleScope).GetMethod(nameof(AnimEventPrefix), BindingFlags.NonPublic | BindingFlags.Static));
        var count = 0;
        if (handler != null)
        {
            foreach (var m in handler.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if (m.Name != "playFMODEvent" || ps.Length == 0 || ps[0].ParameterType != typeof(string))
                    continue;
                _audioHarmony.Patch(m, prefix: animPrefix);
                count++;
            }
        }

        var common = FindGameType("FMODCommon");
        var oneshotPrefix = new HarmonyMethod(typeof(RifleScope).GetMethod(nameof(OneshotPrefix), BindingFlags.NonPublic | BindingFlags.Static));
        if (common != null)
        {
            foreach (var m in common.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if (m.Name != "PlayOneshotInternal" || ps.Length < 4 || ps[0].ParameterType != typeof(string))
                    continue;
                _audioHarmony.Patch(m, prefix: oneshotPrefix);
                count++;
            }
        }

        RLog.Msg($"Suppressor audio: patched {count} methods (handler={(handler != null)}, common={(common != null)})");
    }

    private static bool RifleSuppressedNow() =>
        _suppressor && _rifle && _rifle.gameObject.activeInHierarchy;

    private static bool AnimEventPrefix(Component __instance, object[] __args)
    {
        try
        {
            if (!RifleSuppressedNow() || __args == null || __args.Length == 0 || __args[0] as string != RifleFireEvent)
                return true;
            if (!__instance || __instance.transform.root != _rifle.transform.root)
                return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool OneshotPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (_reissuing)
            return true;

        try
        {
            if (__args == null || __args.Length < 4 || __args[0] as string != SuppressedFireEvent)
                return true;

            var existing = __args[3] as Il2CppReferenceArray<Il2CppSystem.Object>;

            if (!RifleSuppressedNow())
            {
                if (!_loggedPistolParams && existing != null && existing.Length > 0)
                {
                    _loggedPistolParams = true;
                    RLog.Msg($"Suppressor audio: pistol params [{string.Join(", ", existing.Select(Describe))}]");
                }
                return true;
            }

            var oldCount = existing?.Length ?? 0;
            for (var i = 0; i < oldCount; i++)
            {
                if (existing[i] != null && existing[i].ToString() == UpgradeParameter)
                    return true;
            }

            var array = new Il2CppReferenceArray<Il2CppSystem.Object>(oldCount + 2);
            for (var i = 0; i < oldCount; i++)
                array[i] = existing[i];
            array[oldCount] = new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(UpgradeParameter));
            array[oldCount + 1] = BoxFloat(SuppressedUpgradeValue);

            var args = (object[])__args.Clone();
            args[3] = array;
            _reissuing = true;
            try
            {
                __originalMethod.Invoke(null, args);
            }
            finally
            {
                _reissuing = false;
            }
            return false;
        }
        catch (Exception e)
        {
            if (!_loggedParamError)
            {
                _loggedParamError = true;
                RLog.Error($"Suppressor audio: could not play suppressed shot: {(e.InnerException ?? e).Message}");
            }
            return true;
        }
    }

    private static Il2CppSystem.Object BoxFloat(float value)
    {
        object single = new Il2CppSystem.Single();
        typeof(Il2CppSystem.Single).GetField("m_value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(single, value);
        return ((Il2CppSystem.Single)single).BoxIl2CppObject();
    }

    private static string Describe(Il2CppSystem.Object o)
    {
        if (o == null)
            return "null";
        try
        {
            return $"{o.GetIl2CppType().Name}:{o.ToString()}";
        }
        catch
        {
            return "?";
        }
    }

    private static void SetSuppressor(RangedWeaponController rifle, bool on)
    {
        var weapon = rifle._rangedWeapon;
        var mods = weapon ? weapon._weaponMods : null;
        if (!mods)
        {
            Say("Suppressor: rifle has no WeaponMods");
            return;
        }

        var mod = FindSuppressorMod();
        if (!mod)
        {
            Say("Suppressor: pistol suppressor mod is not loaded");
            return;
        }

        if (on)
        {
            InstallAudioPatches();
            if (!EnsureMuzzleSlot(rifle, mods))
            {
                Say("Suppressor: could not find the rifle muzzle");
                return;
            }

            var applied = mods.HasMod(mod.ModItemId) || mods.ApplyMod(mod);
            rifle._gunShotAudioEvent = SuppressedFireEvent;
            RLog.Msg($"Suppressor on: applied={applied} slot={mod.AttachesToSlot} required={mod.RequiredSlot} item={mod.ModItemId}");
            return;
        }

        if (mods.HasMod(mod.ModItemId))
            mods.RemoveMod(mod);
        if (_originalFireEvent != null)
            rifle._gunShotAudioEvent = _originalFireEvent;
    }

    private static Transform FindProjectileTransform(RangedWeaponController rifle)
    {
        var constraint = rifle._projectileTransformConstraint;
        if (constraint)
            return constraint.transform;
        foreach (var t in rifle.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "ProjectileTransform")
                return t;
        }
        return null;
    }

    private static bool CenterPoint(Camera cam, out Vector3 point)
    {
        var origin = cam.transform.position;
        var dir = cam.transform.forward;
        var mask = _rifle ? _rifle._aimHitMask.value : 0;
        if (mask == 0)
            mask = Physics.DefaultRaycastLayers;

        if (Physics.Raycast(origin, dir, out var hit, MaxRayDistance, mask, QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            return true;
        }

        point = origin + dir * MaxRayDistance;
        return false;
    }

    private static float Range(Camera cam)
    {
        if (!cam || !CenterPoint(cam, out var point))
            return -1f;
        return Vector3.Distance(cam.transform.position, point);
    }

    private static void SteerShot(Camera cam)
    {
        var constraint = _rifle._projectileTransformConstraint;
        if (!constraint || !_projectile)
            return;

        var goal = constraint._target;
        if (!goal)
            return;

        if (!_steered || _steeredConstraint.Pointer != constraint.Pointer)
        {
            RestoreShot();
            _steeredConstraint = constraint;
            _originalShotOffset = constraint._offset;
            _steered = true;
        }

        CenterPoint(cam, out var point);
        var scale = Vector3.Distance(cam.transform.position, point) / TrimReferenceRange;
        point -= cam.transform.up * (_trimCm * 0.01f * scale);
        point += cam.transform.right * (_windCm * 0.01f * scale);
        var dir = point - _projectile.position;
        if (dir.sqrMagnitude < 0.0001f)
            return;

        var desired = Quaternion.LookRotation(dir, goal.up);
        constraint._offset = Quaternion.Inverse(goal.rotation) * desired;
    }

    private static string DebugLine(Camera cam)
    {
        if (!_projectile || !CenterPoint(cam, out var point))
            return $"trim {Fmt(_trimCm)} cm\nno target";

        var r = Vector3.Distance(_projectile.position, point);
        var barrel = _projectile.position + _projectile.forward * r;
        var up = Vector3.Dot(barrel - point, cam.transform.up) * 100f;
        var side = Vector3.Dot(barrel - point, cam.transform.right) * 100f;
        var eye = Vector3.Dot(_projectile.position - cam.transform.position, cam.transform.up) * 100f;
        return $"trim {Fmt(_trimCm)} wind {Fmt(_windCm)}  steer {(_steered ? "on" : "off")}\nbarrel up {up:+0.0;-0.0} cm side {side:+0.0;-0.0} cm\nbarrel above eye {eye:+0.0;-0.0} cm";
    }

    private static void RestoreShot()
    {
        if (_steered && _steeredConstraint)
            _steeredConstraint._offset = _originalShotOffset;
        _steered = false;
        _steeredConstraint = null;
    }

    private static void UpdateOffset(FovManager manager)
    {
        var settings = _rifle._fovChangeSettings;
        if (settings == null)
            return;

        if (!OriginalOffsets.TryGetValue(settings.Pointer, out var original))
        {
            original = settings._FieldOfViewTargetOffset_k__BackingField;
            OriginalOffsets[settings.Pointer] = original;
        }

        if (!_rifle.IsAiming && manager._activeSource == null)
            _hipFov = manager._currentTargetFieldOfView;
        if (_hipFov <= 1f)
            _hipFov = manager._defaultFov;

        _vanillaAimFov = Mathf.Clamp(_hipFov + original, 1f, 179f);

        if (_rifle.IsAiming)
            return;

        var target = 2f * Mathf.Atan(Mathf.Tan(_vanillaAimFov * 0.5f * Mathf.Deg2Rad) / _zoom) * Mathf.Rad2Deg;
        _targetAimFov = target;
        var offset = target - _hipFov;
        if (Mathf.Abs(settings._FieldOfViewTargetOffset_k__BackingField - offset) > 0.001f)
            settings._FieldOfViewTargetOffset_k__BackingField = offset;
    }

    private static void HideRedDot(RifleAnimatorController rifle)
    {
        foreach (var renderer in rifle.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            var changed = false;
            for (var i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                if (!mat || HiddenIds.Contains(mat.GetInstanceID()) || !mat.shader || mat.shader.name != RedDotShader)
                    continue;
                RedDotSlots.Add((renderer, i, mat));
                mats[i] = HiddenFor(mat);
                changed = true;
            }
            if (changed)
                renderer.sharedMaterials = mats;
        }
    }

    private static void RestoreRedDot()
    {
        foreach (var (renderer, slot, original) in RedDotSlots)
        {
            if (!renderer || !original)
                continue;
            var mats = renderer.sharedMaterials;
            if (slot >= mats.Length || !mats[slot] || !HiddenIds.Contains(mats[slot].GetInstanceID()))
                continue;
            mats[slot] = original;
            renderer.sharedMaterials = mats;
        }
        RedDotSlots.Clear();
    }

    private static void Disable()
    {
        SetZoomed(false, null);
        if (_rifle)
        {
            var settings = _rifle._fovChangeSettings;
            if (settings != null && OriginalOffsets.TryGetValue(settings.Pointer, out var original))
                settings._FieldOfViewTargetOffset_k__BackingField = original;
        }
        RestoreRedDot();
    }

    private static Material HiddenFor(Material original)
    {
        var id = original.GetInstanceID();
        if (HiddenMaterials.TryGetValue(id, out var cached) && cached)
            return cached;

        var hidden = new Material(original)
        {
            name = original.name + "_Hidden",
            hideFlags = HideFlags.HideAndDontSave
        };
        hidden.SetColor("_Color", Color.clear);
        if (hidden.HasProperty("_EmissionColor"))
            hidden.SetColor("_EmissionColor", Color.clear);

        HiddenMaterials[id] = hidden;
        HiddenIds.Add(hidden.GetInstanceID());
        return hidden;
    }

    private static void SetZoomed(bool zoomed, Camera cam)
    {
        if (zoomed)
        {
            if (_heldLayer < 0)
            {
                _heldLayer = LayerMask.NameToLayer("Held");
                if (_heldLayer < 0)
                    _heldLayer = 15;
            }

            if (_hiddenCam && _hiddenCam != cam)
                ShowHeld();
            if (!_hiddenCam)
            {
                cam.cullingMask &= ~(1 << _heldLayer);
                _hiddenCam = cam;
            }

            SteerShot(cam);
            ShowOverlay(true, Range(cam), _debug ? DebugLine(cam) : null, _targetAimFov > 0f ? _targetAimFov : cam.fieldOfView);
            return;
        }

        RestoreShot();
        if (_hiddenCam)
            ShowHeld();
        _hiddenCam = null;
        ShowOverlay(false, -1f, null, 0f);
    }

    private static void ShowHeld()
    {
        if (_hiddenCam)
            _hiddenCam.cullingMask |= 1 << _heldLayer;
        _hiddenCam = null;
    }

    private static void ShowOverlay(bool show, float range, string debug, float fov)
    {
        if (!show)
        {
            if (_overlay && _overlay.activeSelf)
                _overlay.SetActive(false);
            return;
        }

        if (!_overlay)
            CreateOverlay();
        if (!_overlay.activeSelf)
            _overlay.SetActive(true);

        var h = (float)Screen.height;
        if (!Mathf.Approximately(h, _layoutH))
        {
            _layoutH = h;
            LayoutReticle(h);
        }
        PlaceMilDots(h, fov);

        if (_rangeText)
        {
            var label = range < 0f ? "--- m" : $"{Mathf.RoundToInt(range)} m";
            if (debug != null)
                label = debug + "\n" + label;
            if (label != _lastRange)
            {
                _lastRange = label;
                _rangeText.text = label;
            }
        }
    }

    private static void LayoutReticle(float h)
    {
        var radius = h * 0.495f;
        var postStart = radius * 0.55f;
        var postLength = h - postStart;

        _scopeRect.sizeDelta = new Vector2(h, h);
        _crosshairRect.sizeDelta = new Vector2(h * 2f, h * 2f);
        _crosshairRect.anchoredPosition = Vector2.zero;

        _lineH.sizeDelta = new Vector2(h * 2f, ThinLinePx);
        _lineV.sizeDelta = new Vector2(ThinLinePx, h * 2f);

        foreach (var (rect, dir) in Posts)
        {
            if (!rect)
                continue;
            var center = postStart + postLength * 0.5f;
            rect.anchoredPosition = dir * center;
            rect.sizeDelta = dir.x != 0f ? new Vector2(postLength, PostPx) : new Vector2(PostPx, postLength);
        }

        if (_rangeText)
        {
            var rt = _rangeText.rectTransform;
            rt.anchoredPosition = new Vector2(-0.34f * h, -0.31f * h);
            rt.sizeDelta = new Vector2(0.3f * h, 0.2f * h);
            _rangeText.fontSize = 0.035f * h;
        }

        _leftBar.offsetMax = new Vector2(-h * 0.5f + 1f, 0f);
        _rightBar.offsetMin = new Vector2(h * 0.5f - 1f, 0f);
    }

    private static void CreateOverlay()
    {
        _overlay = new GameObject("RifleScopeOverlay");
        UnityEngine.Object.DontDestroyOnLoad(_overlay);
        _overlay.hideFlags = HideFlags.HideAndDontSave;

        var canvas = _overlay.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -100;

        var crosshairGo = new GameObject("Crosshair");
        crosshairGo.hideFlags = HideFlags.HideAndDontSave;
        crosshairGo.transform.SetParent(_overlay.transform, false);
        _crosshairRect = crosshairGo.AddComponent<RectTransform>();
        Center(_crosshairRect);

        _lineH = CreateImage("LineH", Texture2D.whiteTexture, Color.black, _crosshairRect).rectTransform;
        Center(_lineH);
        _lineV = CreateImage("LineV", Texture2D.whiteTexture, Color.black, _crosshairRect).rectTransform;
        Center(_lineV);

        Posts.Clear();
        foreach (var dir in new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right })
        {
            var post = CreateImage($"Post{dir}", Texture2D.whiteTexture, Color.black, _crosshairRect).rectTransform;
            Center(post);
            Posts.Add((post, dir));
        }

        CreateMilDots();

        var scope = CreateImage("Scope", BuildMaskTexture(), Color.white, _overlay.transform);
        _scopeRect = scope.rectTransform;
        Center(_scopeRect);

        _rangeText = CreateRangeText(scope.transform);

        _leftBar = CreateImage("Left", Texture2D.whiteTexture, Color.black, _overlay.transform).rectTransform;
        _leftBar.anchorMin = new Vector2(0f, 0f);
        _leftBar.anchorMax = new Vector2(0.5f, 1f);
        _leftBar.offsetMin = Vector2.zero;

        _rightBar = CreateImage("Right", Texture2D.whiteTexture, Color.black, _overlay.transform).rectTransform;
        _rightBar.anchorMin = new Vector2(0.5f, 0f);
        _rightBar.anchorMax = new Vector2(1f, 1f);
        _rightBar.offsetMax = Vector2.zero;

        _layoutH = -1f;
        _overlay.SetActive(false);
    }

    private static void Center(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
    }

    private static void CreateMilDots()
    {
        MilDots.Clear();
        var tex = BuildDotTexture();
        foreach (var dir in new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right })
        {
            for (var k = 1; k <= MilDotCount; k++)
            {
                var rt = CreateImage($"MilDot{dir}{k}", tex, Color.black, _crosshairRect).rectTransform;
                Center(rt);
                MilDots.Add((rt, dir * k));
            }
        }
    }

    private static void PlaceMilDots(float h, float fov)
    {
        if (fov <= 0f)
            return;

        var pxPerMil = h * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 0.001f;
        _milsPerDot = 1;
        foreach (var step in new[] { 1, 2, 5, 10 })
        {
            _milsPerDot = step;
            if (pxPerMil * step >= MinDotSpacingPx)
                break;
        }

        var spacing = pxPerMil * _milsPerDot;
        var size = Math.Max(MinDotPx, MilDotSize * spacing);
        var limit = h * 0.495f * 0.95f;

        foreach (var (rect, dir) in MilDots)
        {
            if (!rect)
                continue;
            var pos = dir * spacing;
            var visible = pos.magnitude <= limit;
            if (rect.gameObject.activeSelf != visible)
                rect.gameObject.SetActive(visible);
            if (!visible)
                continue;
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(size, size);
        }
    }

    private static Texture2D BuildDotTexture()
    {
        const int size = 64;
        var pixels = new Color32[size * size];
        var c = (size - 1) * 0.5f;
        var radius = size * 0.5f - 1f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - c;
                var dy = y - c;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                var alpha = Math.Clamp(radius - d + 0.5f, 0f, 1f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f + 0.5f));
            }
        }
        return MakeTexture("RifleScopeMilDot", size, pixels);
    }

    private static TextMeshProUGUI CreateRangeText(Transform parent)
    {
        try
        {
            var go = new GameObject("Range");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            var font = FindFont();
            if (font)
                text.font = font;
            text.color = new Color(0.9f, 0.08f, 0.08f, 1f);
            text.alignment = TextAlignmentOptions.BottomLeft;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            text.text = string.Empty;

            var rt = text.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = Vector2.zero;
            return text;
        }
        catch (Exception e)
        {
            RLog.Error($"RifleScope could not create the range readout: {e.Message}");
            return null;
        }
    }

    private static TMP_FontAsset FindFont()
    {
        try
        {
            var font = TMP_Settings.defaultFontAsset;
            if (font)
                return font;
        }
        catch
        {
        }

        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font)
                return font;
        }
        return null;
    }

    private static RawImage CreateImage(string name, Texture texture, Color color, Transform parent)
    {
        var go = new GameObject(name);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<RawImage>();
        image.texture = texture;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static Texture2D BuildMaskTexture()
    {
        var size = MaskSize;
        var pixels = new Color32[size * size];
        var c = (size - 1) * 0.5f;
        var radius = size * 0.495f;
        var fadeStart = radius * 0.88f;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - c;
                var dy = y - c;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                var alpha = Math.Clamp((d - radius) * 0.5f + 0.5f, 0f, 1f);
                var t = Math.Clamp((d - fadeStart) / (radius - fadeStart), 0f, 1f);
                var fade = t * t * (3f - 2f * t) * 0.55f;
                if (fade > alpha)
                    alpha = fade;
                pixels[y * size + x] = new Color32(0, 0, 0, (byte)(alpha * 255f + 0.5f));
            }
        }

        return MakeTexture("RifleScopeMask", size, pixels);
    }

    private static Texture2D MakeTexture(string name, int size, Color32[] pixels)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return tex;
    }

    [DebugCommand("scope")]
    private static void ScopeCommand(string args)
    {
        args = (args ?? string.Empty).Trim().ToLowerInvariant();

        if (args == "on" || args == "off")
        {
            _enabled = args == "on";
            Save();
            if (_enabled)
                _rifle = null;
            else
                Disable();
            Say($"RifleScope {(_enabled ? "on" : "off")}");
            return;
        }

        Say($"RifleScope is {(_enabled ? "on" : "off")}. Usage: scope on or scope off");
    }

    [DebugCommand("suppressor")]
    private static void SuppressorCommand(string args)
    {
        args = (args ?? string.Empty).Trim().ToLowerInvariant();

        if (args != "on" && args != "off")
        {
            Say($"Rifle suppressor is {(_suppressor ? "on" : "off")}. Usage: suppressor on or suppressor off");
            return;
        }

        _suppressor = args == "on";
        Save();
        if (_rifle)
            SetSuppressor(_rifle, _suppressor);
        Say($"Rifle suppressor {(_suppressor ? "on" : "off")}");
    }

    [DebugCommand("scopezoom")]
    private static void ScopeZoomCommand(string args)
    {
        args = (args ?? string.Empty).Trim();

        if (args.Length == 0)
        {
            Say($"Scope zoom is {Fmt(_zoom)}x the vanilla scope. Usage: scopezoom <{Fmt(MinZoom)}-{Fmt(MaxZoom)}>");
            return;
        }

        if (!float.TryParse(args, NumberStyles.Float, Inv, out var value) || value < MinZoom || value > MaxZoom)
        {
            Say($"Usage: scopezoom <{Fmt(MinZoom)}-{Fmt(MaxZoom)}>");
            return;
        }

        _zoom = value;
        Save();
        Say($"Scope zoom set to {Fmt(_zoom)}x the vanilla scope. Applies the next time you aim.");
    }

    [DebugCommand("scopetrim")]
    private static void ScopeTrimCommand(string args)
    {
        args = (args ?? string.Empty).Trim();

        if (args.Length == 0)
        {
            Say($"Scope trim is {Fmt(_trimCm)}. Positive moves hits down, negative moves hits up. Usage: scopetrim <-{Fmt(MaxTrimCm)} to {Fmt(MaxTrimCm)}>");
            return;
        }

        if (!float.TryParse(args, NumberStyles.Float, Inv, out var value) || value < -MaxTrimCm || value > MaxTrimCm)
        {
            Say($"Usage: scopetrim <-{Fmt(MaxTrimCm)} to {Fmt(MaxTrimCm)}>");
            return;
        }

        _trimCm = value;
        Save();
        Say($"Scope trim set to {Fmt(_trimCm)}.");
    }

    [DebugCommand("scopewind")]
    private static void ScopeWindCommand(string args)
    {
        args = (args ?? string.Empty).Trim();

        if (args.Length == 0)
        {
            Say($"Scope windage is {Fmt(_windCm)}. Positive moves hits right, negative moves hits left. Usage: scopewind <-{Fmt(MaxWindCm)} to {Fmt(MaxWindCm)}>");
            return;
        }

        if (!float.TryParse(args, NumberStyles.Float, Inv, out var value) || value < -MaxWindCm || value > MaxWindCm)
        {
            Say($"Usage: scopewind <-{Fmt(MaxWindCm)} to {Fmt(MaxWindCm)}>");
            return;
        }

        _windCm = value;
        Save();
        Say($"Scope windage set to {Fmt(_windCm)}.");
    }

    [DebugCommand("scopedebug")]
    private static void ScopeDebugCommand(string args)
    {
        _debug = !_debug;
        Say($"Scope debug readout {(_debug ? "on" : "off")}");
    }

    private static void Say(string text)
    {
        RLog.Msg(text);
        try
        {
            SonsTools.ShowMessage(text, 5f);
        }
        catch
        {
        }
    }

    private static string Fmt(float v) => v.ToString("0.##", Inv);

    private static void Load()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;
            var parts = File.ReadAllText(_configPath).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, Inv, out var zoom) && zoom >= MinZoom && zoom <= MaxZoom)
                _zoom = zoom;
            if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, Inv, out var trim) && trim >= -MaxTrimCm && trim <= MaxTrimCm)
                _trimCm = trim;
            if (parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, Inv, out var wind) && wind >= -MaxWindCm && wind <= MaxWindCm)
                _windCm = wind;
            if (parts.Length > 3)
                _enabled = parts[3] != "0";
            if (parts.Length > 4)
                _suppressor = parts[4] == "1";
        }
        catch (Exception e)
        {
            RLog.Error($"RifleScope could not read config: {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(_configPath, $"{_zoom.ToString(Inv)} {_trimCm.ToString(Inv)} {_windCm.ToString(Inv)} {(_enabled ? "1" : "0")} {(_suppressor ? "1" : "0")}");
        }
        catch (Exception e)
        {
            RLog.Error($"RifleScope could not write config: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(RangedWeaponController), nameof(RangedWeaponController.CheckFireInput))]
internal static class CheckFireInputPatch
{
    private static void Postfix(RangedWeaponController __instance)
    {
        try
        {
            global::RifleScope.RifleScope.OnWeaponTick(__instance);
        }
        catch
        {
        }
    }
}