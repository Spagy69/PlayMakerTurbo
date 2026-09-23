# PlayMaker Turbo Addon

> **Beta**, like the rest of this repository. Back up your save before you play with it. See the main
> [README](../README.md#before-you-install).

An MSCLoader mod with fixes for My Winter Car's own scripts. PlayMaker Turbo only touches `PlayMaker.dll`; the
slow spots the profiler found in the game's C# code (`Assembly-CSharp.dll` and `cInput.dll`) are handled here,
at runtime, with Harmony. The addon works with or without Turbo installed. Its settings page can also switch
Turbo's options, if Turbo is installed.

## Installing

Copy `PlayMakerTurboAddon.dll` from the `Addon` folder of the release into the game's `Mods` folder. It needs
MSCLoader and nothing else. To remove it, delete the file.

## What it fixes

Every fix has its own checkbox in the mod's settings and is on by default. A fix that is off is not patched at
all, so the game runs its original method.

### Freeze when you first get into a car

Every car has a `ForceFeedback` component. The first time you sit in a car, its `Start` asks the native plugin
`UnityForceFeedback.dll` to create DirectInput and go through every game controller in the system. That took
183 to 199 ms in every recording, a visible freeze, and it happens again for each car you drive. The plugin
keeps one device for the whole game and frees it only when the game quits, so the later calls set up the same
thing again.

The addon makes the same three plugin calls once, while the game is loading, and after that a car only marks
itself as running, which is the state the original leaves it in. `ForceFeedback.Start` dropped from 185 ms to
0.12 ms.

The original binds DirectInput to whichever window is in front. If you switch to another program while the game
loads, the addon leaves the setup to the first car, as before, and says so in the console. One difference
remains: a force feedback wheel plugged in while the game runs is picked up only after a restart, where the
original would find it when you enter the next car.

### Garbage from the closed key binding menu

`cInputGUI` draws cInput's key binding window in `OnGUI`. While the window is closed it draws nothing, but Unity
still prepares IMGUI for it on every GUI event, about 0.5 KB of garbage per frame. The more garbage, the sooner
the next 140 ms garbage collection pause.

The addon switches the component off while the window is closed and back on when it opens. cInput raises
`cGUI.OnGUIToggled` on every open and close, so nothing checks it every frame. The component is switched off
only after its `Start` has run, because `Start` is what tells cInput that the menu exists.

### Suspension IK of parked cars

`SimpleIKSolver` bends the joints of the suspension linkages so they meet their target. A normal save has 43 of
them, each around 13 µs, about 0.57 ms per frame. When the target cannot be reached exactly, a solver runs all
20 of its iterations every frame, also on a car nobody touches.

Parked cars in this game never fall asleep: the wheel script pushes on them every physics step, so their
transforms move by a few micrometres all the time. The addon therefore skips a solve when the last solve that
ran had settled and nothing it reads (the target position and each joint's position, rotation and local
rotation) has moved by more than 0.1 mm, or 0.0001 per quaternion component (about 0.01 degrees), since then.
The comparison is always with the last solve that ran, so small moves cannot add up: the joints stay within that
tolerance of where the solver would put them. In a recording with the fix, about half of the solves were
skipped and the IK took 0.26 ms per frame.

This is the only fix that is not bit identical to the original. A solver with a collider under its joints would
always run, because moving a collider can wake a car's rigidbody; no solver in the game has one.

### Mass recomputed on every physics step

`CarDynamics.FixedUpdate` sets the car's `Rigidbody.centerOfMass` on every physics step, always to the same
value. Each set makes Unity recompute the mass distribution over all of the car's colliders. Measured in the
game, one set costs 60 to 120 µs on the cars with 22 to 41 colliders and 1 to 12 µs on the ones with a few.
The game sets the inertia tensor itself, so the set changed nothing: mass, inertia tensor, its rotation and the
centre of mass stayed the same, also right after a collider was added to the car.

The addon replaces that one call in `FixedUpdate` with a set that happens only when the value differs from what
the body has. The first set on each body always happens, because a body whose centre of mass was never set
follows its colliders. `CarDynamics.FixedUpdate` took 48 µs per call before and 1.3 µs with the fix, which is
about 0.24 ms per frame at five calls per frame.

## Settings

Open Mods, then PlayMaker Turbo Addon, then Settings.

Addon fixes has the four checkboxes above. They are read once when the game loads, because a Harmony patch
cannot be taken back while the game runs, so a change applies after a restart.

PlayMaker Turbo shows every switch from `PlayMakerTurbo.ini` with a short description;
[Settings](../docs/settings.md) explains each one. The ini stays the source of truth, because Turbo reads it when the game starts and people also edit it by hand. The checkboxes are set
from the file when the settings load, and a click writes the one changed line back, leaving the comments alone.
Turbo picks the change up at the next start. Reset all settings to default in MSCLoader does not reset these,
because they are read from the ini again right after. If Turbo is not installed, this part only says so.

## Console

`turboaddon` prints what each fix is doing: whether force feedback was set up while loading, whether the key
binding GUI is switched off, how many IK solvers have settled, and whether the centre of mass fix is on.

## If something breaks

Turn the fix off in the settings, restart the game and check whether the problem goes away. Report it in
[Issues](https://github.com/Spagy69/PlayMakerTurbo/issues) with the fix's name. Each patched method is looked up
by name when the game loads; if a game update removed or renamed it, that fix stays off and the console shows
an error.

## Building

`build.ps1` in the repository root builds the addon along with everything else and puts it into `dist\Addon`;
see [Building from source](../docs/building.md).
The project references `UnityEngine.dll`, `MSCLoader.dll`, `Assembly-CSharp.dll`, `cInput.dll` and
`0Harmony.dll` from the game's `Managed` folder. If the environment variable `MWCMODSFOLDER` points at the
game's `Mods` folder, every build also copies the DLL there.

## Licence

MIT, copyright (c) 2026 Vít Machač, like the rest of the repository. The code was written by an AI; see
[How this was made](../README.md#how-this-was-made).
