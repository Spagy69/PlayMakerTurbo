# How PlayMaker Turbo works

My Winter Car runs on Unity 5.0.0f4, and most of its logic lives in PlayMaker FSMs, about 2000 of them in a
normal save. In the original game around 1900 get an `Update` call every frame, most of them only to find out
that they have nothing to do. That is why the game is limited by the CPU and barely cares about the GPU.

Turbo makes the same work cheaper. It does not disable objects, spread logic over several frames or put FSMs to
sleep, which is what mods like MOP do and what changes how the game behaves. With the default settings the game
logic runs the same steps in the same order as with stock PlayMaker.

## Two assemblies

`PlayMaker.dll` gets as small a change as possible. The installer renames a few methods and turns a few method
bodies into one-line calls into `PlayMakerTurbo.dll`. All of the logic lives there, in a normal C# project
compiled against the patched `PlayMaker.dll`.

Written that way, the code can be read and debugged like any other C#, which hand written IL could not. It also
lets every optimization have a switch in `PlayMakerTurbo.ini` without patching the file again.

## The ticker

Unity calls `Update`, `LateUpdate` and `FixedUpdate` on every `PlayMakerFSM` component separately, and each of
those is a call from native code into managed code. Unity's "10000 Update() calls" article puts the cost of such
a call above the work most idle FSMs do.

The patch renames `PlayMakerFSM.Update`, `PlayMakerFSM.LateUpdate` and `PlayMakerFixedUpdate.FixedUpdate`, so
Unity no longer finds them. One `FsmTicker` component calls them instead, from a single managed loop per phase.
`PlayMakerFSM.OnEnable` creates the ticker first thing, so it exists before any FSM needs it, and
`PlayMakerFixedUpdate` gets an `OnEnable` and `OnDisable` that register it with the ticker.

With the loop in managed code, the ticker can skip a call when it can prove the FSM would do nothing in it
([IdleUpdateSkip, LateUpdateSkip and FixedUpdateSkip](settings.md#the-ticker)). In a typical frame of Turbo 1.1
that skipped about 900 of 1880 `Update` calls, 95 percent of `LateUpdate` calls and 98 percent of `FixedUpdate`
calls.

### Waking FSMs (ActiveLists)

Skipping a call still means looking at the FSM. With `ActiveLists` the ticker keeps a queue for `Update` and one
for `LateUpdate` and looks only at the FSMs in them.

An FSM joins the queues when something can give it work:

- its component is enabled,
- it starts,
- one of its states activates actions, which happens when a state is entered and when a sequence state moves
  on to its next action,
- it gets a delayed event.

The patched `PlayMakerFSM.OnEnable`, `Fsm.Start`, `FsmState.ActivateActions` and both `Fsm.DelayedEvent`
overloads call the ticker at exactly those points. An FSM that the skip rules say has nothing to do leaves the
queue until it is woken again.

The order matters. Unity calls the components in the order they were enabled, which is also the order of
`PlayMakerFSM.FsmList`, and the queues are walked in that order too. When one FSM wakes another in the middle of
the walk, the woken FSM is ticked in the same frame only if the walk has not reached it yet. Otherwise it waits
for the next frame, which is what the full walk does.

An early version ticked such an FSM again in the same frame. Its new state then ran `OnUpdate` in the frame of
its `OnEnter`, the game read one mouse click twice, and car parts snapped back on right after you took them off.

Every 300 frames the ticker still checks all FSMs against the queues, and logs any FSM that has work but was
not queued as a missed wake.

### Light ticks (LightTicks)

About 380 FSMs per frame sit in a state that only waits. Most are `Use` FSMs whose one action is
`MousePickEvent`, which asks every frame whether you are looking at the item; others wait in a lone `Wait`. The
action costs about 1 µs, and the `Fsm.Update` around it (execution stack, owner check, action loop, finished
check, state change loop) costs two or three times that.

For such an FSM the ticker does, at the FSM's own place in the walk, only what that `Update` would change:

- it adds `deltaTime` to the state time and to each `Wait` timer, with the same float operations;
- it runs each pick and writes `RaycastHitInfo`, in the order of the actions.

First it checks whether the `Update` would do anything more: a `Wait` that would run out in this frame, a
`Wait` in real time, a `mouseOff` event, a pending event or state switch, an action of any other type. Any of
those, or the mouse being over the object, sends the FSM through the normal `Fsm.Update` instead.

The light tick reads the private `Wait.timer` field. If a game update renames it, light ticks switch themselves
off and write that to the log.

## Event routing (FastEventRouting)

`SendEvent` and `SendEventByName` with a GameObject as the target end up in
`Fsm.BroadcastEventToGameObject` or `Fsm.SendEventToFsmOnGameObject`. The original walks all of
`PlayMakerFSM.FsmList`, about 2000 FSMs, asks the engine twice for each one, and builds a new list of receivers.
It does all of that again for every child when the event also goes to children.

Turbo reads the target GameObject's own `PlayMakerFSM` components instead, keeps the enabled ones and sorts them
by the order in which they were enabled, which is their order in `FsmList`. The same FSMs get the event in the
same order. `ValidateEventRouting=1` checks that on every event against the original walk.

`Fsm.BroadcastEvent`, which sends to every FSM in the game, keeps its walk and only reuses one buffer instead of
copying `FsmList` into a new list on every call.

The patched methods forward into `PlayMakerTurbo.EventRouting` and keep their original bodies as
`TurboOriginal...` for when the switch is off. Calls from inside `PlayMaker.dll`, such as the one in
`Fsm.Event`, are pointed at the forwarding methods too. Other assemblies find a method by its name, but inside
one assembly a call refers to the method itself, so without this step the game's own actions would still reach
the renamed originals.

## Everything the patch changes

- `PlayMakerFSM.Update`, `PlayMakerFSM.LateUpdate` and `PlayMakerFixedUpdate.FixedUpdate` are renamed and called
  by the ticker. `PlayMakerFixedUpdate` gets `OnEnable` and `OnDisable`.
- `PlayMakerFSM.OnEnable` and `OnDisable`, `Fsm.Start`, both `Fsm.DelayedEvent` overloads and
  `FsmState.ActivateActions` call the ticker first.
- `PlayMakerFSM` gets a non serialized `turboEntry` field for the ticker's bookkeeping. Its serialized `fsm`
  field becomes public, so the ticker can read it without the property, which also rewrites the owner.
- `Fsm.GameObject`, `Fsm.Active`, `FsmState.OnUpdate`, `ActionHelpers.DoMousePick` and `FsmProperty.GetValue` and
  `SetValue` forward into `PlayMakerTurbo.Core`. The original `FsmProperty` bodies stay as
  `TurboOriginalGetValue` and `TurboOriginalSetValue` so the fast path can fall back to them.
- `Fsm.UpdateStateChanges` resets every state's loop counter only when some state was entered since the last
  reset. `FsmState.OnEnter` sets that flag, and it is the only place where the counter grows.
- `Fsm.UpdateDelayedEvents` gets an early return at the top.
- `Fsm.BroadcastEventToGameObject`, `Fsm.SendEventToFsmOnGameObject` and `Fsm.BroadcastEvent` forward into
  `PlayMakerTurbo.EventRouting`, and calls to them inside `PlayMaker.dll` are redirected.

### Serialization

Two serialization rules shape the patch. Unity serializes public fields, so every field that became public for
the ticker is marked `[NonSerialized]`, and saved scenes and `Instantiate` copies stay the same. PlayMaker
stores actions with its own `ActionData` serializer, which reads the public fields of action types, so
`FsmStateAction` and `FsmProperty` got properties over private fields where Turbo needed access. Comparing the
decompiled original with the patched assembly shows the same list of public fields.

## Game scripts at runtime

Turbo's patches to the game's own actions and to `cInput.dll` (`SetGameVolumeSkipUnchanged`,
`CInputAxisInvertedBuilder`) are applied with Harmony when the game starts, and those files stay as they are.
`Assembly-CSharp.dll` changes with every game update, and a runtime patch survives that. When a patched method
disappears after an update, the patch is skipped and a warning goes to the log.

The [addon](../Addon/README.md) works the same way for its fixes in the game's C# code.
