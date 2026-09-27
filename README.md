\# Rifle Scope



A stronger rifle scope for Sons of the Forest. Aiming down the rifle scope zooms

5x further than the vanilla scope and shows a full screen scope view with a

crosshair reticle, mil dots and a rangefinder instead of the red dot.



\## Multiplayer



Client side only. Nothing gets installed on a dedicated server, and other players

do not need the mod.



\## Installation



\### Easy install



Close the game, open PowerShell and paste:



```powershell

irm https://raw.githubusercontent.com/JustinOros/sotf-riflescope/main/Install.ps1 | iex

```



It finds your game, installs RedLoader if needed, and installs the latest

RifleScope. Run it again any time to update.



\### Manual install



\#### Step 1: Install RedLoader



\[RedLoader](https://github.com/ToniMacaroni/RedLoader/releases/latest) is the mod

loader for Sons of the Forest. The game cannot load any mod without it, so

install it first. You only have to do this once.



1\. Download `RedLoader.zip` from the

&#x20;  \[latest RedLoader release](https://github.com/ToniMacaroni/RedLoader/releases/latest)

2\. Extract it into your Sons of the Forest folder, the one containing

&#x20;  `SonsOfTheForest.exe`, usually

&#x20;  `C:\\Program Files (x86)\\Steam\\steamapps\\common\\Sons Of The Forest`

3\. Launch the game once and wait until you reach the main menu. The first launch

&#x20;  takes a few minutes while RedLoader processes the game files

4\. Check that `MODS` appears on the main menu, then quit



\#### Step 2: Install RifleScope



1\. Download `RifleScope.zip` from the

&#x20;  \[latest release](https://github.com/JustinOros/sotf-riflescope/releases/latest)

2\. Extract it into the `Mods` folder inside your game folder

3\. You should end up with:



```

Mods\\RifleScope.dll

Mods\\RifleScope\\manifest.json

```



\## Usage



Equip the rifle and aim. Once zoomed in, the rifle model is hidden and the scope

view is shown. Your shot goes where the crosshair is.



The red number at the bottom left of the scope is the range in meters to whatever

is under the crosshair.



The reticle has 10 mil dots in each direction. At 5x each dot is 2 mils, which

covers 20 cm at 100 m. At 10x and higher each dot is 1 mil. Use them to hold over

or to the side at long range.



Press F1 to open the console, then use these commands:



| Command | Action |

| --- | --- |

| `scope` | Show whether the mod is on or off |

| `scope off` | Turn the mod off and go back to the vanilla scope |

| `scope on` | Turn the mod back on |

| `scopezoom` | Show the current zoom |

| `scopezoom 5` | Set the zoom as a multiple of the vanilla scope, 1 to 50. The default is 5 |

| `scopetrim` | Show the current vertical trim |

| `scopetrim 50` | Set the vertical trim, -200 to 200. Positive moves hits down, negative moves hits up. The default is 50 |

| `scopewind` | Show the current windage |

| `scopewind 20` | Set the windage, -200 to 200. Positive moves hits right, negative moves hits left. The default is 0 |

| `scopedebug` | Toggle a debug readout above the range |



Trim and windage are angle adjustments, so a setting that is right at one range

stays right at every range.



The on or off setting, zoom, trim and windage are saved to `UserData\\RifleScope.txt` in your game

folder. Setting the zoom to 1 keeps vanilla zoom and the normal scope view,

without the red dot.



\## Building from source



Requires the .NET 8 SDK and RedLoader installed with its game assemblies

generated.



```powershell

.\\build.ps1 -Install

```



Use `-Package` to build `RifleScope.zip` for a release. Pass `-GameDir "path"` if

the game is not found automatically.



\## Troubleshooting



Check `\_RedLoader\\Latest.log` in your game folder. RifleScope logs a line when it

loads.

