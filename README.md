# PlayMaker Turbo

> **Beta.** PlayMaker Turbo changes a core game file. It has been tested on one computer with one set of
> mods, and it can still crash the game, break some game logic or damage your save. Back up your save before
> you play with it, and keep the backup until you are sure everything works. See [Before you install](#before-you-install).

A speedup for PlayMaker in My Winter Car. The game runs on Unity 5.0.0f4 and most of its logic lives in
PlayMaker FSMs. Around 1900 of them tick every frame, which is why the game is limited by the CPU and barely
cares about the GPU.

Turbo patches `PlayMaker.dll` so the same work costs less. Everything enabled by default produces the same
result as stock PlayMaker: no objects are disabled, no logic is time sliced, no FSM is put to sleep. Two
options behave differently in rare edge cases; they are off by default and described below.

Tested on My Winter Car with MSCLoader 1.4.2 (build 410), PlayMaker 1.7.7.6, on a Ryzen 5 8645HS with an
RTX 4060 Laptop.

## Measured results

Numbers come from the `fpstest` console command (the Better FPS mod) and from my own profiler mod, taken at
the same spot in the world.

| | stock game | Turbo | Turbo with the mouse pick cache |
|---|---|---|---|
| `fpstest` Vanilla | 88.5 FPS | 108.3 FPS | 116.9 FPS |
| FSM time measured inside C# | 4.79 ms/frame | 3.51 ms | 2.40 ms |
| mouse pick raycasts | 203 per frame | 203 | 17 |
| managed allocations | 17.9 KB/frame | | 12.4 KB/frame |
| garbage collections | 1.4 per minute | | 0.8 per minute |

The profiler also counts what gets skipped. In a typical frame about 900 of 1880 `Update` calls, 95 percent
of `LateUpdate` calls and 98 percent of `FixedUpdate` calls are skipped because the FSM would provably do
nothing in them.

## Before you install

Copy your save folder somewhere outside the game. The game keeps it in

```
%USERPROFILE%\AppData\LocalLow\Amistech\My Winter Car
```

Turbo needs MSCLoader, because it uses the `0Harmony.dll` that MSCLoader puts into the game's `Managed` folder.

Things that can go wrong, and what to do about them:

- The game does not start or freezes while loading. Press Restore original game in the installer.
- Something in the world stops working: a door does not open, an item cannot be picked up, a car part does
  not react. Turn the options in `PlayMakerTurbo.ini` off one by one until it works again, then report which
  option it was in [Issues](https://github.com/Spagy69/PlayMakerTurbo/issues).
- The game updated and now crashes. Run the installer again, it patches the new `PlayMaker.dll`.

If a save got damaged, restoring the game files does not repair it. Only your backup does.

## Installing

Download the zip from [Releases](https://github.com/Spagy69/PlayMakerTurbo/releases). The repository itself
holds only source code; if you want to build it yourself, see [Building from source](#building-from-source).

Unpack the zip into one folder and keep `PlayMakerTurbo.dll` and the `Mono.Cecil*.dll` files next to
`PlayMakerTurbo Installer.exe`.

Close the game, run the installer, and press Install. The installer asks Steam where its libraries are and
finds the game on its own. If that fails, use the Browse button and point it at the folder that holds
`mywintercar.exe`; it also accepts the `mywintercar_Data` or `Managed` folder.

Install backs up `PlayMaker.dll` as `PlayMaker.dll.orig`, patches `PlayMaker.dll`, copies `PlayMakerTurbo.dll`
into `mywintercar_Data\Managed` and writes `PlayMakerTurbo.ini` there. An existing `.ini` is left alone, so
your settings survive an update.

To uninstall, press Restore original game. It puts the backup back and deletes `PlayMakerTurbo.dll`.

Run the installer again after every game update or Steam file verification. It notices that the game shipped
a different `PlayMaker.dll`, refreshes the backup from it and patches that.

The installer also works from a command line: pass the game path to install, add `--restore` to undo.

## Settings

Everything is switched in `mywintercar_Data\Managed\PlayMakerTurbo.ini`, one line per optimization, `1` for on
and `0` for off. The file is read when the game starts. If something misbehaves, turn entries off one by one
to find which one is responsible.

`IdleUpdateSkip` skips `Fsm.Update` for FSMs whose active state has finished its actions, with no state switch
pending and no delayed event waiting. In stock PlayMaker such a call walks an empty loop and returns.

`LateUpdateSkip` and `FixedUpdateSkip` do the same for the other two phases. An FSM is skipped when none of
its active actions override that callback.

`DelayedEventsEarlyOut` returns from `UpdateDelayedEvents` immediately when there are no delayed events. The
original clears two lists and copies an empty one into place on every call.

`GameObjectCache` caches the owner's GameObject on the Fsm. The original asks the engine for it on every
action in every frame, even though a component never changes its GameObject.

`SkipNonUpdatingActions` does not call actions that do not override `OnUpdate`, because the base method is
empty and `Init` would rewrite the same three fields. Actions that override `Init` are always called; the game
has two of those, `AnimateFsmAction` and `CurveFsmAction`.

`MousePickSingleCameraLookup` fetches `Camera.main` once in `DoMousePick` instead of twice. In Unity 5 each
call searches objects by tag.

`SetGameVolumeSkipUnchanged` writes `AudioListener.volume` only when the value differs from the current one.

`PropertyDelegates` replaces reflection in the `GetProperty` and `SetProperty` actions with a cached typed
delegate, for single member properties whose type selects the same branch of the original type chain. Mono's
`PropertyInfo.GetValue/SetValue` allocates an argument array and boxes the value on every call; the delegate
allocates nothing. Writes to Material, Texture and GameObject always take the original path, because when
their parameter is None the original falls through to the generic Object branch. Member paths with more than
one step, fields, enums and static properties also take the original path.

`CInputAxisInvertedBuilder` patches `cInput._SaveAxInverted`, which the `CInputSetAxisInverted` action calls
every frame. The original rebuilds a string one axis at a time and writes it to PlayerPrefs, which on Windows
is a registry write and costs around 40 microseconds. Turbo writes only when the axis states changed.

### The two options that are not bit identical

`ActiveFast` computes `Fsm.Active` through `isActiveAndEnabled`, one engine call instead of four. While
validating it, the profiler found 45 mismatches out of 1.5 million checks, most likely during the frames when
an object is being deactivated. That is why it stays off.

`MousePickFrameCache` shares the mouse pick raycast within a frame per layer mask. Stock PlayMaker caches it
too, but only for a single mask, so `Use` FSMs with different masks keep overwriting each other's result.
With the cache on, raycasts dropped from 203 to 17 per frame and `fpstest` gained about 8 FPS. The result
differs from the original only when the camera or a collider moves between two picks inside the same frame.
It ships off; turning it on is worth it.

## How it works

`PlayMaker.dll` gets the smallest possible surgery. All the logic lives in `PlayMakerTurbo.dll`, a normal C#
project compiled against the patched `PlayMaker.dll`. That keeps the code readable instead of hand written IL,
and it makes the switches possible without re-patching.

What the patch changes:

- `PlayMakerFSM.Update`, `PlayMakerFSM.LateUpdate` and `PlayMakerFixedUpdate.FixedUpdate` are renamed, so
  Unity stops calling them per component. `FsmTicker` calls them from one managed loop instead. Unity's own
  measurements in the "10000 Update() calls" article put the cost of a native to managed call above the work
  most idle FSMs do.
- `PlayMakerFSM.OnEnable` calls `FsmTicker.Ensure` first, so the ticker exists before any FSM needs it.
  `PlayMakerFixedUpdate` gets an `OnEnable` and `OnDisable` that register the proxy with the ticker.
- `Fsm.GameObject`, `Fsm.Active`, `FsmState.OnUpdate`, `ActionHelpers.DoMousePick` and `FsmProperty.GetValue`
  and `SetValue` forward into `PlayMakerTurbo.Core`. For `FsmProperty` the original bodies stay under the
  names `TurboOriginalGetValue` and `TurboOriginalSetValue` so the fast path can fall back to them.
- `Fsm.UpdateStateChanges` is rewritten to reset every state's loop counter only when some state was entered
  since the last reset. `FsmState.OnEnter` sets the flag, and it is the only place where the counter grows.
- `Fsm.UpdateDelayedEvents` gets an early return at the top.

Two serialization rules shape the patch. Unity serializes public fields, so fields that became public for the
ticker are marked `[NonSerialized]`, which keeps saved scenes and `Instantiate` copies identical. PlayMaker
stores actions with its own `ActionData` serializer, which reads the public fields of action types, so
`FsmStateAction` and `FsmProperty` received properties over private fields instead of public fields. Comparing
the decompiled original with the patched assembly shows the same list of public fields.

Patches to the game's own actions and to `cInput.dll` are applied at runtime with Harmony rather than by
editing those files. `Assembly-CSharp.dll` changes with every game update, and a runtime patch survives that.
When a patched method disappears after an update, the patch is skipped and a warning goes to the log.

## Building from source

You need:

- Windows with Visual Studio 2022 and the .NET desktop development workload, which brings MSBuild and the
  .NET SDK.
- The Unity Full v3.5 reference profile, which the runtime targets. Visual Studio installs it with the Game
  development with Unity workload, the same setup the MSCLoader mod template asks for. Check for the folder
  `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v3.5\Profile\Unity Full v3.5`.
- My Winter Car with MSCLoader installed. The runtime links against `UnityEngine.dll`, `PlayMaker.dll`,
  `Assembly-CSharp.dll`, `cInput.dll` and `0Harmony.dll` from the game's `Managed` folder. Those are game files
  and are not part of this repository.

Then run the build script from the repository folder:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

It finds the game through Steam. If your game is somewhere else, add `-GamePath "E:\Games\My Winter Car"`.
The finished release lands in `dist\`, and from there you install it like the downloaded zip.

The script reads the game files and never changes them. The order of its steps matters, because the runtime
has to be compiled against the patched `PlayMaker.dll`:

1. Build the installer (`Patcher/`, WinForms, .NET Framework 4.8, Mono.Cecil from NuGet).
2. Copy the original `PlayMaker.dll` into `obj\playmaker` and patch the copy with
   `"PlayMakerTurbo Installer.exe" <folder> --patch-only`. If Turbo is already installed in the game, the
   original is taken from `PlayMaker.dll.orig`.
3. Build the runtime (`Runtime/`, .NET 3.5) with MSBuild against that patched copy.
4. Copy the installer, the runtime, the Mono.Cecil files and the documents into `dist\`.

## The profiler

Most of the numbers above come from a separate MSCLoader mod, `MWCFsmProfiler`. F9 records everything: time
per FSM, per action type, per MonoBehaviour script and per mod, plus a frame breakdown of physics, rendering
and scripts, allocations per caller and GC counts. F10 records without any patching, which gives clean FPS,
the time spent in Turbo's own loops and the allocation rate. Reports land in
`Mods\Config\Mod Settings\MWCFsmProfiler`.

## What Turbo does not do

It does not disable distant objects or their FSMs the way MOP or NOP do. That changes how the game behaves and
causes the usual problems with items that save in the wrong state.

It does not remove the stutter from garbage collection. Unity 5 stops the whole game and walks the entire heap,
which at 450 MB takes around 200 ms. Turbo only lowers the allocation rate so collections happen less often.
In my game the largest single allocator was the Cheatbox++ mod at 10.9 KB per frame.

It does not speed up rendering, which the profiler puts at about 4.5 ms of an 11.6 ms frame. Shadow distance is
the setting that moves that number the most.

## How this was made

The code and this documentation were written by an AI, Claude (Anthropic), working in Claude Code. I directed
the work, decided what the patch may and may not change, ran the game and took every measurement in this
README. The AI analysed the decompiled PlayMaker, wrote the installer, the runtime and the profiler mod, and
fixed what my test runs turned up. Keep that in mind when you read the code, and treat it as beta for that
reason too.

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
your own disk, so do not share a patched `PlayMaker.dll` or a game folder with Turbo installed. Share the
installer instead. Modifying game files may conflict with the terms under which you got the game, so check
them yourself if that matters to you.

The installer does not connect to the internet and collects no data. It reads the Steam install path from the
Windows registry to find the game and writes only into the game's `Managed` folder.
