# FSM Profiler

An MSCLoader mod that measures where the time and the managed allocations of a frame go in My Winter Car.
It is the tool every number in the PlayMaker Turbo README was measured with. It works with or without Turbo
installed; with Turbo it also reads Turbo's own counters and can switch Turbo's options for benchmarks.

It is an instrumenting profiler. It wraps game methods with Harmony patches that take a timestamp and a heap
reading before and after each call, the same way Unity's own profiler uses `BeginSample`/`EndSample`. That
makes it precise about managed code and blind inside the engine: rendering and physics show up only as whole
phases of the frame.

> **Beta**, like Turbo. The benchmark changes game state while it runs (see below), so use a copy of your save.

## Installing

Copy `MWCFsmProfiler.dll` into the game's `Mods` folder. The release zip has it in the `Profiler` folder.

The mod does nothing until the first recording. The timing patches go in when you press F9 or F11 for the first
time, which freezes the game for a few seconds, and they stay until you quit. A light recording (F10) made after
that is no longer free of them, and its report says so.

## Keys

All keys can be changed in MSCLoader's keybind settings.

| Key | What it does |
|---|---|
| F9 | Full recording. Press again to stop and save the report. |
| F10 | Light recording without timing patches: frame times, FPS, GC and Turbo's counters only. |
| F11 | Trace: every timed call of the next 300 frames (adjustable), for ui.perfetto.dev. |
| F12 | Benchmark, see below. F12 or Esc aborts it. |
| F8 | Live overlay with FPS, frame time percentiles, GC rate and, during F9, the five most expensive FSMs. |

The overlay allocates memory while it is on. Keep it off when you measure allocations.

## What a full recording measures

Every timed call is booked twice: its inclusive time (everything that ran inside it) and its self time (minus
timed calls nested inside). An FSM's self time is only PlayMaker's own bookkeeping, and its actions do the
real work, so FSM tables are sorted by inclusive time and everything else by self time.

A probe's cost lands in the code that called the timed method, so it has to be subtracted there. At the start
of each recording the profiler measures it per kind of probe. An empty patched method timed 100 000 times gives
the cost of Harmony's glue and the shared probe work (about 0.08 µs on a Ryzen 5 8645HS). The real bookkeeping
of the FSM, action, event and script probes is then run directly on a live FSM from the scene, and whatever it
costs beyond the minimal probe is added to that kind. Every call remembers the probe cost of the timed calls
nested inside it and subtracts exactly that from its self time. The probe cost of top-level calls happens
outside any timed call and gets its own line, "Profiler overhead", in the frame breakdown.

The report covers:

- FSMs, with the state that was active when each call started.
- Action instances: which action, in which state and at which index, on which object. For every action type
  the five instances that cost the most. This is how `CInputSetAxisInverted` was traced to the `Controls` FSM on
  `Systems/OptionsDB`.
- Events: how often each one is processed, who sends it and who receives it.
- MonoBehaviour callbacks: `Update`, `LateUpdate`, `FixedUpdate`, `OnGUI`, trigger and collision callbacks,
  render callbacks such as `OnRenderImage`, mouse callbacks, `Start`/`Awake`/`OnEnable`/`OnDisable`, every
  coroutine step, uGUI, and Unity's `SendMouseEvents`.
- MSCLoader mods, by callback.
- A frame breakdown into PlayMaker, actions, events, scripts, mods, physics, rendering and untimed time.
- Every frame on a timeline, so the report has median, p95, p99, 1% low FPS and the longest frames.
- Spikes: a frame longer than three times the recent median keeps every call it made, gets a guessed cause
  (garbage collection, one call, or engine work outside timed code) and is saved as a trace file.
- Allocations per call, per frame phase, and in the gaps between timed calls (explained below).
- Writes that changed nothing: `everyFrame` actions such as `SetFloatValue`, `FloatClamp`, `ActivateGameObject`
  or `CInputSetAxisInverted` that write the value the target already holds. These are the cheapest
  optimizations to find.
- Why the most expensive FSMs are not idle: the unfinished actions and delayed events of their current state.
- `GetProperty`/`SetProperty` by member, and whether Turbo's allocation free path covers it.
- Active scripts whose per-frame callbacks run but are not timed.
- A snapshot of the scene when the recording stops: awake and sleeping rigidbodies, colliders, visible
  renderers, shadow casters, lights with shadows, playing particle systems and audio sources, per root object.
- Render settings and, with Turbo installed, Turbo's counters and settings.

### Finding allocations nobody times

Mono's `GC.GetTotalMemory` goes up when the Boehm collector takes a new block of heap, so a single call's
number is coarse; summed over thousands of calls it ranks the allocators well. Heap growth outside any timed
call is booked to the gap it happened in, "after A, before B", with fixed markers at the frame boundary, after
all `Update` scripts, and at the end of physics and of rendering. At the start of a recording the main thread
also sleeps 250 ms, and whatever the heap grows by meanwhile was allocated by other threads.

That is how the report found, in order, a sort inside the profiler that boxed every float it compared, the
overlay itself, `Collision` objects that Unity creates before every `OnCollisionStay` call, and immediate mode
GUI overhead in `cInputGUI` and MSCLoader's `OnGUI`.

## Output

Every recording gets its own folder in `Mods\Config\Mod Settings\MWCFsmProfiler`:

- `report.txt` for reading in any editor.
- `report.json` with all the data, including the per-frame timeline.
- `report.html`, a single file that opens offline: sortable tables with filters, rows that expand from an FSM to
  its states and from an action type to its instances, a frame time chart, heap and allocation charts, and a
  button that loads a second `report.json` and shows the difference between the two.
- `spike_NN_XXms.json` for every kept spike, and `trace.json` after F11. Open them at
  [ui.perfetto.dev](https://ui.perfetto.dev) with "Open trace file" to see every call on a timeline. A trace
  of 300 frames has about a million calls and a file of about 150 MB.

## Benchmark (F12)

The benchmark measures Turbo's options against each other under the same conditions, with statistics that say
whether a difference is real. Stand on foot where you want to measure and press F12. The profiler then:

1. Locks the player (`CharacterMotor.canControl = false`) and the view (`MouseLook` sensitivity 0). Both keep
   running, so the frame does the same work.
2. Stops game time. It switches off the FSMs whose current state writes the time globals (`ClockHours`,
   `ClockMinutes`, `GlobalTime` and others); in testing those were `Color` and `TimeReset` on the sun. At the end
   it checks that the clock really stood still and writes the result into the report.
3. Switches off the `Weather` FSM and, unless you turn it off in the settings, deactivates `TRAFFIC`,
   `NPC_CARS` and `TRAIN`, the biggest source of noise.
4. Warms up for 5 seconds, then measures in 10 second blocks and throws away the first half second after each
   switch.
5. Puts everything back.

Use a copy of your save and do not save after a benchmark. Switching FSMs and traffic off and on again changes
game state.

There are three modes, chosen in the mod settings:

- A/B switches each checked option of `PlayMakerTurbo.Core` off and on in the order ABBA BAAB, which cancels
  slow drift such as GPU clocks or heap growth. Each option gets at least 8 off/on pairs and at most 20, and
  stops early once the 95% interval is narrower than ±0.05 ms.
- A/A switches nothing. It has to come out "in noise"; if it does not, the method is broken and the other
  results mean nothing.
- Baseline measures the current settings for 20 blocks and compares them with the previous baseline session.
  It is meant for comparing Turbo with the original game, which needs a restart in between, and has not been
  run end to end yet.

The unit of analysis is a block, not a frame, because neighbouring frames are not independent. The gain in
mean frame time comes from a paired t-test over the block pairs, p99 and median from a bootstrap with a fixed
seed, and frames with a garbage collection are left out and counted separately. The verdict is "provable
gain", "provable but negligible gain" (under 0.02 ms), "in noise", "worse" or "too little data".

The first runs gave:

| | gain per frame | 95% interval | verdict |
|---|---|---|---|
| A/A | 0.004 ms | −0.043 to 0.051 ms | in noise |
| `MousePickFrameCache` | 0.87 ms (9.2 %) | 0.85 to 0.90 ms | provable gain |
| `SetGameVolumeSkipUnchanged` | 0.03 ms | −0.01 to 0.08 ms | in noise |

## Limits

- Timing patches make the game slower, so FPS under F9 is lower than in normal play. Take FPS from F10 or from
  the benchmark, which runs without timing patches.
- Only the main thread is timed. `OnAudioFilterRead` runs on the audio thread and is not patched.
- Inside the engine the profiler sees only whole phases. For culling, shadows or PhysX you need a native
  sampling profiler.
- Allocation numbers per call are statistical, as explained above. Totals per frame are exact.
- The timeline holds 36 000 frames, about five minutes at 110 FPS; a longer recording keeps the tables but
  its summary covers only the first 36 000 frames. A trace holds about two million calls.
