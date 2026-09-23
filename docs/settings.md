# Settings

Turbo reads its switches from `mywintercar_Data\Managed\PlayMakerTurbo.ini` once, when the game starts. Each
line is one optimization, `1` for on and `0` for off. A switch that is missing from the file keeps its default,
so an ini from an older version works with a newer one.

With the [addon](../Addon/README.md) installed, every switch below is also a checkbox on its settings page in
MSCLoader (Mods, PlayMaker Turbo Addon, Settings). A click writes the one changed line back into the ini and
leaves the comments alone. Either way, a change applies after a restart.

If something in the game misbehaves, turn the switches off one at a time until it works again, and report the
one that caused it in [Issues](https://github.com/Spagy69/PlayMakerTurbo/issues).

## Overview

| Switch | Default | Same result as the original | Measured gain per frame | Checkbox in the addon |
|---|---|---|---|---|
| [`IdleUpdateSkip`](#idleupdateskip) | on | yes | 0.43 ms | Skip Update of FSMs that finished and wait for nothing |
| [`LateUpdateSkip`](#lateupdateskip-and-fixedupdateskip) | on | yes | 1.05 ms | Skip LateUpdate when no active action uses it |
| [`FixedUpdateSkip`](#lateupdateskip-and-fixedupdateskip) | on | yes | 0.06 ms | Skip FixedUpdate when no active action uses it |
| [`DelayedEventsEarlyOut`](#delayedeventsearlyout) | on | yes | 0.07 ms | Return from delayed events right away when there are none |
| [`GameObjectCache`](#gameobjectcache) | on | yes | 0.11 ms | Cache the FSM owner's GameObject |
| [`SkipNonUpdatingActions`](#skipnonupdatingactions) | on | yes | about zero | Do not call actions that have no OnUpdate |
| [`MousePickSingleCameraLookup`](#mousepicksinglecameralookup) | on | yes | 0.04 ms | Fetch Camera.main once per mouse pick instead of twice |
| [`SetGameVolumeSkipUnchanged`](#setgamevolumeskipunchanged) | on | yes | in noise | Set the game volume only when it changes |
| [`PropertyDelegates`](#propertydelegates) | on | yes | 0.18 ms | GetProperty / SetProperty without reflection (no garbage) |
| [`CInputAxisInvertedBuilder`](#cinputaxisinvertedbuilder) | on | yes | 0.09 ms | cInput stores inverted axis settings only when they change |
| [`ActiveLists`](#activelists) | on | yes | 0.40 ms | Tick only woken FSMs instead of walking all of them |
| [`FastEventRouting`](#fasteventrouting) | on | yes | per event, see below | Send events to one GameObject without walking all FSMs |
| [`LightTicks`](#lightticks) | on | yes | about zero | Light tick for FSMs that only wait (Wait, mouse over) |
| [`MousePickFrameCache`](#mousepickframecache) | off | almost | 0.77 ms | Share mouse pick raycast in a frame (almost identical) |
| [`ActiveFast`](#activefast) | off | no | not measured | Fsm.Active via isActiveAndEnabled (NOT identical) |
| [`ValidateEventRouting`](#validateeventrouting) | off | yes, debugging only | costs time | none |

"Same result" means the game logic does the same thing in the same order as with stock PlayMaker. The gains come
from the profiler's A/B benchmark with all other switches on, see
[Measured results](../README.md#measured-results). What each switch changes inside PlayMaker is explained in
[How it works](how-it-works.md).

## The ticker

These switches decide which FSMs get an `Update`, `LateUpdate` or `FixedUpdate` call in a frame. They need the
ticker, which Turbo always installs; with all of them off the ticker calls every FSM, like the original.

### IdleUpdateSkip

Skips `Fsm.Update` for an FSM whose active state has finished its actions, with no state switch pending and no
delayed event waiting. In stock PlayMaker such a call walks an empty loop and returns.

### LateUpdateSkip and FixedUpdateSkip

Skip `Fsm.LateUpdate` and `Fsm.FixedUpdate` for an FSM when none of its active actions override that callback.

### ActiveLists

Keeps a queue of FSMs for `Update` and one for `LateUpdate`, and walks only those instead of all of the roughly
2000 FSMs in every phase. An FSM joins a queue when something can give it work and leaves it when the skip rules
above say it has nothing to do. The queues keep the order of the full walk, see
[Waking FSMs](how-it-works.md#waking-fsms-activelists).

Measured without the profiler, the `LateUpdate` loop went from 0.51 to 0.09 ms per frame, walking 110 FSMs
instead of 2080. The `Update` loop stayed at about 0.6 ms, because more than 1000 FSMs do real work there every
frame. The benchmark puts the whole switch at 0.40 ms per frame. With `ActiveLists=0` the ticker walks every FSM
and applies the skip rules to each.

Every 300 frames the ticker checks all FSMs against the queues. An FSM that has work but is not queued gets
queued and reported in the log as a missed wake. Testing turned up none.

### LightTicks

Gives FSMs that only wait a cheaper tick. That covers a state whose active actions are only `Wait` (not in real
time) or `MousePickEvent` without a `mouseOff` event: about 300 `Use` FSMs that check every frame whether you look
at an item, and many others that sit in a lone `Wait`. The ticker does just what their `Update` would change and
hands over to the normal `Fsm.Update` as soon as anything more would happen. See
[Light ticks](how-it-works.md#light-ticks-lightticks).

About 380 FSMs per frame take this path. In the profiler's timing that looked like a large saving, PlayMaker's
own time falling from 1.53 to 0.70 ms per frame, but the profiler's probes also make every `Fsm.Update` more
expensive. The benchmark, which runs without probes, puts light ticks at about zero: slightly worse with
`MousePickFrameCache` on and 0.09 ms per frame better with it off. If a game update changes `Wait` or `MousePickEvent`, light ticks switch themselves off and say so in the
log.

## PlayMaker internals

### DelayedEventsEarlyOut

Returns from `Fsm.UpdateDelayedEvents` right away when there are no delayed events. The original clears two
lists and copies an empty one into place on every call.

### GameObjectCache

Caches the owner's GameObject on the Fsm. The original asks the engine for it for every action in every frame,
although a component never changes its GameObject.

### SkipNonUpdatingActions

Does not call actions that do not override `OnUpdate`. The base method is empty, and `Init` would write the
same three fields again. Actions that override `Init` are always called; the game has two, `AnimateFsmAction`
and `CurveFsmAction`.

Two benchmark runs put it at about zero, once slightly worse and once in noise. Checking whether an action has an
`OnUpdate` seems to cost about as much as calling the empty method.

### FastEventRouting

Changes how an event reaches the FSMs of one GameObject, which is what `SendEvent` and `SendEventByName` do when
their target is a GameObject or an FSM on it. The original walks all of `PlayMakerFSM.FsmList`; Turbo reads only
the target's own components. The same FSMs get the event in the same order, see
[Event routing](how-it-works.md#event-routing-fasteventrouting).

Pressing the light switch on a car dashboard took 530 µs and made 18 KB of garbage with the original, and 80 µs
with no garbage with Turbo. `BroadcastEvent`, which sends to every FSM in the game, still walks the whole list
but reuses a buffer instead of copying the list each time.

### ValidateEventRouting

A debugging switch that is not in the default file. With `ValidateEventRouting=1`, every event sent to a
GameObject is also routed the original way, and any difference in the receivers goes to the log (at most 30
lines). A test session with car part assembly found none. It costs the original's time on top of Turbo's, so
leave it off for playing.

## Actions and game scripts

### MousePickSingleCameraLookup

Fetches `Camera.main` once in `ActionHelpers.DoMousePick` instead of twice. In Unity 5 each call searches the
scene's objects by tag.

### SetGameVolumeSkipUnchanged

Writes `AudioListener.volume` in the `SetGameVolume` action only when the value differs from the current one.

### PropertyDelegates

Replaces reflection in the `GetProperty` and `SetProperty` actions with a cached typed delegate. Mono's
`PropertyInfo.GetValue/SetValue` and `FieldInfo.GetValue/SetValue` box the value on every call; the delegate
allocates nothing.

The car scripts (`Wheel`, `Drivetrain`, `AxisCarController`) expose public fields, and while driving the game
reads and writes them about 140 times a frame, around 3 KB of garbage per frame on the original path. With the
switch on, the game's allocations while driving fell from 10.6 to 5.6 KB per frame in the profiler.

The fast path covers a single property or field whose type selects the same branch of the original type chain.
These always take the original path:

- writes to Material, Texture and GameObject, because when their parameter is None the original falls through
  to the generic Object branch;
- member paths with more than one step;
- readonly fields, enums and static members.

### CInputAxisInvertedBuilder

Patches `cInput._SaveAxInverted`, which the `CInputSetAxisInverted` action calls every frame. The original
rebuilds a string one axis at a time and writes it to PlayerPrefs, which on Windows is a registry write of
about 40 µs. Turbo writes only when the axis states changed.

## Switches that are off by default

### MousePickFrameCache

Shares the mouse pick raycast within a frame, one result per layer mask. Stock PlayMaker caches it too, but for
a single mask only, so `Use` FSMs with different masks keep overwriting each other's result.

With the cache on, raycasts dropped from 203 to 17 per frame. The benchmark puts it at 0.77 ms per frame, and at
the test spot the game went from 125 to 134 FPS. The result differs from the original only when the camera or a
collider moves between two picks inside one frame. Car part assembly, which picks while you move a part in
front of the camera, worked normally with it on. It ships off because it is not bit identical, but it is worth
turning on.

### ActiveFast

Computes `Fsm.Active` through `isActiveAndEnabled`, one engine call instead of four. While validating it, the
profiler found 45 mismatches in 1.5 million checks, most likely in the frames in which an object is being
deactivated. Leave it off.
