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

Measured with the profiler's benchmark (F12) on version 1.2.0-beta, at one spot in the house, 1920x1080, quality
Golden Eye with 200 m shadows, no mods except MSCLoader, the profiler and, in the Turbo sessions, the addon. The
benchmark freezes the player, game time, weather and traffic, warms up for 5 seconds and then measures twenty
10 second blocks. Each column is a separate start of the game from the same save.

| | Original game | Turbo + addon, default settings | Turbo + addon, mouse pick cache on |
|---|---|---|---|
| Average frame | 13.86 ms (72 FPS) | 8.00 ms (125 FPS) | 7.45 ms (134 FPS) |
| p99 frame | 28.4 ms | 11.2 ms | 10.8 ms |
| 1% low | 33 FPS | 85 FPS | 89 FPS |
| Managed allocations | 11.1 KB/frame | 3.2 KB/frame | 3.0 KB/frame |

Against the original, the default settings save 5.87 ms per frame (95% interval 5.64 to 6.10 ms) and the
settings with the mouse pick cache 6.41 ms (6.19 to 6.63 ms). How much you gain elsewhere depends on your
computer and on how much is going on around you.

Within one session, the A/B benchmark switches one option off and on in blocks and compares neighbouring blocks,
so slow drift cancels out. With everything else on:

| Option | Gain per frame | 95% interval |
|---|---|---|
| `LateUpdateSkip` | 1.05 ms | 0.84 to 1.27 |
| `MousePickFrameCache` | 0.77 ms | 0.73 to 0.82 |
| `IdleUpdateSkip` | 0.43 ms | 0.39 to 0.46 |
| `ActiveLists` | 0.40 ms | 0.34 to 0.47 |
| `PropertyDelegates` | 0.18 ms | 0.03 to 0.33 |
| `GameObjectCache` | 0.11 ms | 0.06 to 0.16 |
| `CInputAxisInvertedBuilder` | 0.09 ms | 0.07 to 0.11 |
| `DelayedEventsEarlyOut` | 0.07 ms | 0.02 to 0.11 |
| `FixedUpdateSkip` | 0.06 ms | 0.04 to 0.09 |
| `MousePickSingleCameraLookup` | 0.04 ms | 0.00 to 0.08 |
| `SetGameVolumeSkipUnchanged` | in noise | -0.06 to 0.03 |
| `FastEventRouting` | in noise | -0.04 to 0.04 |
| `SkipNonUpdatingActions` | about zero | -0.09 to 0.00, and -0.04 to 0.11 in a second run |
| `LightTicks` | about zero | -0.10 to 0.00, and 0.04 to 0.14 in a second run with the cache off |

A method check that switches nothing (A/A) came out at 0.001 ms (-0.037 to 0.039 ms).

The gains do not add up to the total, for two reasons. Each option was measured with all the others on, and
the largest single part of Turbo has no switch: the ticker, which calls the FSMs from one managed loop instead
of letting Unity call about 2000 components from native code in every update phase.

`FastEventRouting` speeds up single events, which the frozen benchmark hardly sends. The profiler measured it,
and the addon's fixes, on the events and calls themselves:

| Change | Before | After |
|---|---|---|
| Light switch on a car dashboard with `FastEventRouting` | 530 µs, 18 KB garbage | 80 µs, no garbage |
| First time in a car, addon force feedback fix | 185 ms freeze | 0.12 ms |
| `CarDynamics.FixedUpdate`, addon centre of mass fix | 48 µs per call | 1.3 µs per call |
| Suspension IK, addon parked IK fix | 0.57 ms/frame | 0.26 ms/frame |

The profiler writes the same tables on any computer, see [Profiler](Profiler/README.md#benchmark-f12).
## What Turbo does not do

It does not disable distant objects or their FSMs the way MOP or NOP do. That changes how the game behaves and
causes the usual problems with items that save in the wrong state.

It does not remove the stutter from garbage collection. Unity 5 stops the whole game and walks the entire heap,
which at about 400 MB takes around 140 ms. Turbo only lowers the allocation rate so collections happen less
often. Of what is left, about 2 KB per frame are `Collision` objects that Unity creates before every
`OnCollisionStay` call, which Unity 5 cannot reuse.

It does not speed up rendering, which the profiler puts at about 2.7 ms of a 9.4 ms frame with my graphics mods. Shadow distance is
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
