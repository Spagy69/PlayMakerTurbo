# PlayMaker Turbo

> **Beta.** PlayMaker Turbo changes a core game file. It has been tested on one computer with one set of mods,
> and it can still crash the game, break some game logic or damage your save. Back up your save before you play
> with it, and keep the backup until you are sure everything works. See [Before you install](#before-you-install).

A speedup for PlayMaker in My Winter Car. Most of the game's logic lives in about 2000 PlayMaker FSMs, and
around 1900 of them get an `Update` call every frame, which is why the game is limited by the CPU and barely
cares about the GPU. Turbo patches `PlayMaker.dll` so that the same work costs less.

With the default settings the game logic does the same as with stock PlayMaker. Turbo disables no objects,
spreads no logic over several frames and puts no FSM to sleep. Two options behave differently in rare edge
cases; they are off by default.

The release also has two MSCLoader mods:

- [PlayMaker Turbo Addon](Addon/README.md) fixes four slow spots in the game's own C# code, among them the
  freeze when you first get into a car. Its settings page also holds all of Turbo's switches.
- [FSM Profiler](Profiler/README.md) measures where the time of a frame goes. Every number on these pages comes
  from it.

Tested on My Winter Car with MSCLoader 1.4.2 (build 410) and PlayMaker 1.7.7.6, on a Ryzen 5 8645HS with an
RTX 4060 Laptop.

## Download

Get a zip from [Releases](https://github.com/Spagy69/PlayMakerTurbo/releases). Each release has five:

| Zip | Contents |
|---|---|
| `players` | The Turbo installer and the addon, with a short guide. Take this one if you just want the game faster. |
| `full` | Everything: Turbo, the addon, the profiler and all documentation |
| `turbo` | Only the Turbo installer |
| `addon` | Only the addon |
| `profiler` | Only the profiler |

The repository itself holds only source code, see [Building from source](docs/building.md).

## Before you install

Copy your save folder somewhere outside the game. The game keeps it in

```
%USERPROFILE%\AppData\LocalLow\Amistech\My Winter Car
```

Turbo needs MSCLoader, because it uses the `0Harmony.dll` that MSCLoader puts into the game's `Managed` folder.

Things that can go wrong, and what to do about them:

- The game does not start or freezes while loading. Press Restore original game in the installer.
- Something in the world stops working: a door does not open, an item cannot be picked up, a car part does not
  react. Turn the options off one by one until it works again, and report which one it was in
  [Issues](https://github.com/Spagy69/PlayMakerTurbo/issues).
- The game updated and now crashes. Run the installer again; it patches the new `PlayMaker.dll`.

Restoring the game files does not touch your save. If a save got damaged, copy your backup back in its place.

## Install

Unpack the zip into one folder and keep `PlayMakerTurbo.dll` and the `Mono.Cecil*.dll` files next to
`PlayMakerTurbo Installer.exe`. Close the game, run the installer and press Install.

The installer asks Steam where its libraries are and finds the game on its own. If that fails, press Browse and
point it at the folder that holds `mywintercar.exe`; the `mywintercar_Data` or `Managed` folder works too.

Install backs up `PlayMaker.dll` as `PlayMaker.dll.orig`, patches `PlayMaker.dll`, and copies `PlayMakerTurbo.dll`
and `PlayMakerTurbo.ini` into `mywintercar_Data\Managed`. An existing ini is kept, so your settings survive an
update.

Run the installer again after every game update or Steam file verification. It notices that the game shipped a
different `PlayMaker.dll`, takes that as the new backup and patches it.

To use the mods, copy `Addon\PlayMakerTurboAddon.dll` and `Profiler\MWCFsmProfiler.dll` into the game's `Mods`
folder.

To uninstall, press Restore original game. It puts the backup back and deletes `PlayMakerTurbo.dll`.

The installer also works from a command line: pass the game path to install, and add `--restore` to undo.

## Settings

Every optimization has a switch in `mywintercar_Data\Managed\PlayMakerTurbo.ini`, `1` for on and `0` for off,
read when the game starts. With the addon installed, the same switches are checkboxes on its settings page in
MSCLoader. [Settings](docs/settings.md) describes each of them.

`MousePickFrameCache` is off by default because in rare cases its result differs from the original, but it
gave about 8 FPS in testing and is worth turning on. Leave `ActiveFast` off.

## Measured results

These numbers were taken with Turbo 1.1, before `ActiveLists`, `FastEventRouting`, `LightTicks` and the addon
existed. A new benchmark with all of them will follow.

They come from the `fpstest` console command of the Better FPS mod and from the profiler, at the same spot in the
world.

| | Stock game | Turbo 1.1 | Turbo 1.1 with the mouse pick cache |
|---|---|---|---|
| `fpstest` Vanilla | 88.5 FPS | 108.3 FPS | 116.9 FPS |
| FSM time measured inside C# | 4.79 ms/frame | 3.51 ms | 2.40 ms |
| Mouse pick raycasts | 203 per frame | 203 | 17 |
| Managed allocations | 17.9 KB/frame | | 12.4 KB/frame |
| Garbage collections | 1.4 per minute | | 0.8 per minute |

The profiler's A/B benchmark switches one option off and on in 10 second blocks while the player, game time,
weather and traffic are frozen. It puts the mouse pick cache at 0.87 ms per frame (95% interval 0.85 to
0.90 ms), about 105 to 116 FPS at that spot.

Profiler measurements of the newer parts:

| Change | Before | After |
|---|---|---|
| `LateUpdate` loop with `ActiveLists` | 0.51 ms/frame | 0.09 ms/frame |
| PlayMaker's own time with `LightTicks` | 1.53 ms/frame | 0.70 ms/frame |
| Light switch on a car dashboard with `FastEventRouting` | 530 µs, 18 KB garbage | 80 µs, no garbage |
| First time in a car, addon force feedback fix | 185 ms freeze | 0.12 ms |
| `CarDynamics.FixedUpdate`, addon centre of mass fix | 48 µs per call | 1.3 µs per call |
| Suspension IK, addon parked IK fix | 0.57 ms/frame | 0.26 ms/frame |

## What Turbo does not do

It does not disable distant objects or their FSMs the way MOP or NOP do. That changes how the game behaves and
causes the usual problems with items that save in the wrong state.

It does not remove the stutter from garbage collection. Unity 5 stops the whole game and walks the entire heap,
which at about 400 MB takes around 140 ms. Turbo only lowers the allocation rate so collections happen less
often. Of what is left, about 2 KB per frame are `Collision` objects that Unity creates before every
`OnCollisionStay` call, which Unity 5 cannot reuse.

It does not speed up rendering, which the profiler puts at about 4.5 ms of an 11.6 ms frame. Shadow distance is
the setting that moves that number the most.

## Documentation

- [Player guide](docs/players.md): install, settings and uninstall in short, the README of the `players` zip.
- [Settings](docs/settings.md): every switch, its default and what it changes.
- [How it works](docs/how-it-works.md): the ticker, event routing, light ticks and everything the patch changes
  in `PlayMaker.dll`.
- [Building from source](docs/building.md): requirements, build and packaging scripts, repository layout.
- [Addon](Addon/README.md): the four fixes in the game's scripts and the settings page.
- [Profiler](Profiler/README.md): recordings, reports, traces and the A/B benchmark.

## How this was made

The code and this documentation were written by an AI, Claude (Anthropic), working in Claude Code. I directed
the work, decided what the patch may and may not change, ran the game and took every measurement on these
pages. The AI analysed the decompiled PlayMaker, wrote the installer, the runtime, the profiler and the addon,
and fixed what my test runs turned up. Keep that in mind when you read the code; it is one more reason this is
a beta.

## Licence

Copyright (c) 2026 Vít Machač. The code in this repository is released under the MIT licence, see
[LICENSE](LICENSE). The release also contains Mono.Cecil, which has its own MIT licence, see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

The software is provided as is, without warranty of any kind. The author is not liable for crashes, lost or
damaged saves, or any other damage that comes from using it. You install it at your own risk.

This is an unofficial fan project. It is not affiliated with, endorsed by or supported by Amistech Games,
Hutong Games or the MSCLoader developers. Do not report problems that appear with Turbo installed to them;
restore the original game first and check whether the problem is still there. My Winter Car, PlayMaker and
all other product names belong to their respective owners.

`PlayMaker.dll` is commercial code by Hutong Games and `Assembly-CSharp.dll` belongs to Amistech Games. This
repository contains none of their code, not even decompiled parts of it. The installer changes the copy on
your own disk, so do not share a patched `PlayMaker.dll` or a game folder with Turbo installed; share the
installer instead. Modifying game files may conflict with the terms under which you got the game, so check
them yourself if that matters to you.

The installer does not connect to the internet and collects no data. It reads the Steam install path from the
Windows registry to find the game and writes only into the game's `Managed` folder.
