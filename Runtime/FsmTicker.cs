using System;
using System.Collections.Generic;
using System.Diagnostics;
using HutongGames.PlayMaker;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PlayMakerTurbo
{
    // Ticks every enabled PlayMakerFSM from one Update/LateUpdate/FixedUpdate instead of Unity calling each
    // component separately. Unity's per-component call (native -> managed, with validity checks) costs more
    // than the work most idle FSMs do, and MWC has ~2000 of them. FSMs whose tick would provably do nothing
    // are skipped; see the *IsNoOp methods for the exact conditions.
    //
    // With ActiveLists on, the ticker does not walk all ~2000 FSMs in every phase to find the ones to skip. An FSM
    // is queued when something can give it work: its component is enabled, it starts, one of its states activates
    // actions (entering a state, or the next action of a sequence), or it gets a delayed event. The patched
    // PlayMaker.dll calls Wake at exactly those points. The loops tick the queued FSMs and drop every one that the
    // skip rules say has nothing to do; nothing but a wake can give it work again. Walking and skipping the whole
    // list cost about 1.3 ms per frame, measured without the profiler.
    public class FsmTicker : MonoBehaviour
    {
        private static FsmTicker instance;
        private static PlayMakerFSM[] snapshot = new PlayMakerFSM[4096];
        private static readonly List<PlayMakerFixedUpdate> fixedProxies = new List<PlayMakerFixedUpdate>();
        private static readonly HashSet<PlayMakerFixedUpdate> removedThisStep = new HashSet<PlayMakerFixedUpdate>();
        private static PlayMakerFixedUpdate[] fixedSnapshot = new PlayMakerFixedUpdate[512];
        private static bool inFixedStep;

        private static readonly TickQueue updateQueue = new TickQueue(false);
        private static readonly TickQueue lateQueue = new TickQueue(true);
        private static int frameNo;
        private static long enableCounter;
        private const int SweepInterval = 300;
        private const int MaxMissedLogs = 20;

        // Read by MWCFsmProfiler through reflection.
        public static long UpdateCalls;
        public static long UpdateSkipped;
        public static long LateUpdateCalls;
        public static long LateUpdateSkipped;
        public static long FixedUpdateCalls;
        public static long FixedUpdateSkipped;
        public static long MissedWakes;     // FSMs the safety sweep found with work but not queued; should stay 0
        public static long QueuedUpdate;    // FSMs walked by the Update loop (with ActiveLists on)
        public static long QueuedLate;

        // Time spent inside the FSM calls themselves, so the profiler can tell the loop's own cost (walking every
        // FSM and deciding to skip it) from the work of the FSMs. Measured only while the profiler sets
        // MeasureInner: two timestamps per call are cheap, but not free.
        public static bool MeasureInner;
        public static long UpdateInnerTicks;
        public static long LateUpdateInnerTicks;
        public static long FixedUpdateInnerTicks;

        // Per-component state, stored in the field the patcher adds to PlayMakerFSM.
        internal sealed class Entry
        {
            public PlayMakerFSM Component;
            public bool Enabled;
            public bool InUpdate;
            public bool InLate;
            public int UpdateFrame = -1;
            public int LateFrame = -1;
            public long EnableSeq;   // order of the last enable = order in PlayMakerFSM.FsmList
            public long UpdateRunSeq; // EnableSeq when the entry was put into the sorted run of each queue
            public long LateRunSeq;
        }

        // FSMs to tick, visited in FsmList order like the original, which walked FsmList (and Unity calls the
        // components in the order they were enabled). The rule the original follows: an FSM is ticked when the walk
        // reaches it and it has work at that moment. An FSM woken by another one later in the same frame is ticked
        // this frame only if the walk has not passed it yet; otherwise it waits for the next frame. Ticking it again
        // would run its new state's OnUpdate in the same frame as its OnEnter (a click read twice, for example).
        //
        // The run holds the FSMs kept from the last pass, sorted by EnableSeq. New ones go to the tail and are merged
        // in by EnableSeq, so a pass sorts only the few FSMs woken since the last one.
        private sealed class TickQueue
        {
            private readonly bool late;
            private List<Entry> run = new List<Entry>(2048);
            private List<Entry> next = new List<Entry>(2048);
            private readonly List<Entry> tail = new List<Entry>(256);
            private readonly List<Entry> pending = new List<Entry>(256);
            private readonly List<Entry> carry = new List<Entry>(256);

            public TickQueue(bool late)
            {
                this.late = late;
            }

            public void Add(Entry e)
            {
                tail.Add(e);
            }

            public void Pass()
            {
                // FSMs enabled from here on joined FsmList after the original took its snapshot for this frame.
                long maxSeq = enableCounter;
                long cursor = long.MinValue;
                int i = 0;
                int absorbed = 0;
                next.Clear();
                carry.Clear();
                pending.Clear();

                while (true)
                {
                    while (absorbed < tail.Count)
                        Route(tail[absorbed++], cursor, maxSeq);

                    // An FSM disabled and enabled again since it was sorted in has moved to the end of FsmList.
                    while (i < run.Count && RunSeq(run[i]) != run[i].EnableSeq)
                        Route(run[i++], cursor, maxSeq);

                    Entry e;
                    if (i < run.Count && (pending.Count == 0 || run[i].EnableSeq <= pending[0].EnableSeq))
                    {
                        e = run[i++];
                    }
                    else if (pending.Count > 0)
                    {
                        e = pending[0];
                        pending.RemoveAt(0);
                    }
                    else
                    {
                        break;
                    }

                    cursor = e.EnableSeq;
                    if (late ? TickLate(e) : TickUpdate(e))
                    {
                        SetRunSeq(e, e.EnableSeq);
                        next.Add(e);
                    }
                    else
                    {
                        SetQueued(e, false);
                    }
                }

                tail.Clear();
                tail.AddRange(carry);
                List<Entry> swap = run;
                run = next;
                next = swap;
                next.Clear();
            }

            // A woken FSM the walk has not reached yet is ticked in this pass at its place; one it has passed, or
            // one enabled during the pass, waits for the next pass.
            private void Route(Entry e, long cursor, long maxSeq)
            {
                if (!e.Enabled)
                {
                    SetQueued(e, false);
                    return;
                }
                if (e.EnableSeq > cursor && e.EnableSeq <= maxSeq)
                {
                    int at = pending.Count;
                    while (at > 0 && pending[at - 1].EnableSeq > e.EnableSeq)
                        at--;
                    pending.Insert(at, e);
                }
                else
                {
                    carry.Add(e);
                }
            }

            private long RunSeq(Entry e)
            {
                return late ? e.LateRunSeq : e.UpdateRunSeq;
            }

            private void SetRunSeq(Entry e, long seq)
            {
                if (late)
                    e.LateRunSeq = seq;
                else
                    e.UpdateRunSeq = seq;
            }

            private void SetQueued(Entry e, bool queued)
            {
                if (late)
                    e.InLate = queued;
                else
                    e.InUpdate = queued;
            }
        }

        // Called by the patched PlayMakerFSM.OnEnable.
        public static void Ensure()
        {
            if (instance != null || PlayMakerFSM.ApplicationIsQuitting)
                return;

            Core.LoadSettings();
            ApplyActionPatches();
            GameObject go = new GameObject("PlayMakerTurbo");
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            instance = go.AddComponent<FsmTicker>();
        }

        // Separate method so that if a game update removed a patched action type, the resulting load error
        // (raised when this call is compiled) is caught here instead of breaking PlayMakerFSM.OnEnable.
        private static void ApplyActionPatches()
        {
            try
            {
                ActionPatches.Apply();
            }
            catch (Exception e)
            {
                Debug.LogWarning("PlayMakerTurbo: action patches skipped.\n" + e);
            }
        }

        // ---- wake points, called by the patched PlayMaker.dll ----

        // PlayMakerFSM.OnEnable, before its body: the component joins PlayMakerFSM.FsmList.
        public static void Enabled(PlayMakerFSM component)
        {
            Ensure();
            Entry e = GetEntry(component);
            e.Enabled = true;
            e.EnableSeq = ++enableCounter;
            Queue(e);
        }

        // PlayMakerFSM.OnDisable, before its body: the component leaves FsmList. The loops drop it lazily.
        public static void Disabled(PlayMakerFSM component)
        {
            GetEntry(component).Enabled = false;
        }

        // Fsm.Start and Fsm.DelayedEvent.
        public static void Wake(Fsm fsm)
        {
            // Only the FSM a component owns is ticked; sub-FSMs run inside their parent's RunFSM action.
            PlayMakerFSM component = fsm.Owner as PlayMakerFSM;
            if (ReferenceEquals(component, null) || !ReferenceEquals(component.fsm, fsm))
                return;
            Queue(GetEntry(component));
        }

        // FsmState.ActivateActions: entering a state, or a sequence state starting its next action.
        public static void WakeState(FsmState state)
        {
            Fsm fsm = state.fsm;
            if (fsm != null)
                Wake(fsm);
        }

        private static Entry GetEntry(PlayMakerFSM component)
        {
            Entry e = component.turboEntry as Entry;
            if (e == null)
            {
                e = new Entry { Component = component };
                component.turboEntry = e;
            }
            return e;
        }

        private static void Queue(Entry e)
        {
            if (!e.InUpdate)
            {
                e.InUpdate = true;
                updateQueue.Add(e);
            }
            if (!e.InLate)
            {
                e.InLate = true;
                lateQueue.Add(e);
            }
        }

        // ---- fixed update proxies ----

        // Called by the OnEnable/OnDisable the patcher adds to PlayMakerFixedUpdate.
        public static void RegisterFixed(PlayMakerFixedUpdate proxy)
        {
            Ensure();
            fixedProxies.Add(proxy);
        }

        public static void UnregisterFixed(PlayMakerFixedUpdate proxy)
        {
            fixedProxies.Remove(proxy);
            if (inFixedStep)
                removedThisStep.Add(proxy);
        }

        // ---- Update ----

        private void Update()
        {
            long start = Stopwatch.GetTimestamp();
            frameNo++;
            // The original calls this at the top of every Fsm.Update; it only flips a static flag.
            FsmTime.RealtimeBugFix();
            Core.UpdateDelta = Time.deltaTime;
            Core.UpdateDeltaValid = true;

            if (!Core.ActiveLists || Fsm.HitBreakpoint)
            {
                UpdateAll();
            }
            else
            {
                if (frameNo % SweepInterval == 0)
                    Sweep();
                UpdateQueued();
            }
            Core.UpdateDeltaValid = false;
            Core.TickerUpdateTicks += Stopwatch.GetTimestamp() - start;
        }

        private static void UpdateQueued()
        {
            updateQueue.Pass();
        }

        // Returns false when the FSM has nothing to do until it is woken again.
        private static bool TickUpdate(Entry e)
        {
            QueuedUpdate++;
            if (!e.Enabled)
                return false;
            PlayMakerFSM fsm = e.Component;
            Fsm inner = fsm.fsm;
            // Unity never calls Update before Start; Fsm.Start wakes it.
            if (!inner.Started)
                return false;

            if (IdleUpdateSkipAllowed(inner))
            {
                UpdateSkipped++;
                return false;
            }

            // No Unity null check: a component is disabled (OnDisable, which clears Enabled) before it is destroyed.
            if (e.UpdateFrame == frameNo)
                return true;
            e.UpdateFrame = frameNo;
            if (!TryLightTick(inner))
                CallUpdate(fsm);
            return true;
        }

        private static void UpdateAll()
        {
            int count = TakeSnapshot();
            for (int i = 0; i < count; i++)
            {
                PlayMakerFSM fsm = snapshot[i];
                Fsm inner = fsm.Fsm;
                // Unity never calls Update before Start.
                if (!inner.Started)
                    continue;

                if (IdleUpdateSkipAllowed(inner))
                {
                    UpdateSkipped++;
                    continue;
                }

                // Destroyed earlier this loop: Unity would not call Update on it either. Checked only here
                // because skipping is always correct and the Unity null check is the costlier test.
                if (fsm == null)
                    continue;
                if (!TryLightTick(inner))
                    CallUpdate(fsm);
            }
            Array.Clear(snapshot, 0, count);
        }

        private static bool TryLightTick(Fsm inner)
        {
            return Core.LightTicks && LightTick.Ready() && LightTick.TryTick(inner, Core.UpdateDelta);
        }

        private static void CallUpdate(PlayMakerFSM fsm)
        {
            UpdateCalls++;
            long inner0 = MeasureInner ? Stopwatch.GetTimestamp() : 0L;
            try
            {
                fsm.TurboUpdate();
            }
            catch (Exception e)
            {
                Debug.LogException(e, fsm);
            }
            if (inner0 != 0L)
                UpdateInnerTicks += Stopwatch.GetTimestamp() - inner0;
        }

        // ---- LateUpdate ----

        private void LateUpdate()
        {
            long start = Stopwatch.GetTimestamp();
            if (!Core.ActiveLists || Fsm.HitBreakpoint)
                LateUpdateAll();
            else
                LateUpdateQueued();
            FsmVariables.GlobalVariablesSynced = false;
            Core.TickerLateUpdateTicks += Stopwatch.GetTimestamp() - start;
        }

        private static void LateUpdateQueued()
        {
            lateQueue.Pass();
        }

        // Dropping on a no-op is safe because only a wake can make LateUpdate do something again: the active actions
        // only shrink until ActivateActions runs, and a pending state switch is resolved inside the FSM's own tick.
        private static bool TickLate(Entry e)
        {
            QueuedLate++;
            if (!e.Enabled)
                return false;
            PlayMakerFSM fsm = e.Component;
            Fsm inner = fsm.fsm;
            if (!inner.Started || inner.Finished)
                return false;

            if (Core.LateUpdateSkip && CallbackIsNoOp(inner, Core.FlagLateUpdate))
            {
                LateUpdateSkipped++;
                return false;
            }

            if (e.LateFrame == frameNo)
                return true;
            e.LateFrame = frameNo;
            CallLate(fsm);
            return true;
        }

        private static void LateUpdateAll()
        {
            int count = TakeSnapshot();
            for (int i = 0; i < count; i++)
            {
                PlayMakerFSM fsm = snapshot[i];
                Fsm inner = fsm.Fsm;
                if (!inner.Started || inner.Finished)
                    continue;

                if (Core.LateUpdateSkip && CallbackIsNoOp(inner, Core.FlagLateUpdate))
                {
                    LateUpdateSkipped++;
                    continue;
                }

                if (fsm == null)
                    continue;
                CallLate(fsm);
            }
            Array.Clear(snapshot, 0, count);
        }

        private static void CallLate(PlayMakerFSM fsm)
        {
            LateUpdateCalls++;
            long inner0 = MeasureInner ? Stopwatch.GetTimestamp() : 0L;
            try
            {
                fsm.TurboLateUpdate();
            }
            catch (Exception e)
            {
                Debug.LogException(e, fsm);
            }
            if (inner0 != 0L)
                LateUpdateInnerTicks += Stopwatch.GetTimestamp() - inner0;
        }

        // ---- safety sweep ----

        // Every few seconds, check every enabled FSM against the queues. An FSM that has work but is not queued
        // means a wake point is missing: it is queued now and reported, so the gap can be fixed.
        private static void Sweep()
        {
            int count = TakeSnapshot();
            for (int i = 0; i < count; i++)
            {
                PlayMakerFSM fsm = snapshot[i];
                Entry e = GetEntry(fsm);
                e.Enabled = true; // it is in FsmList, so it is enabled
                Fsm inner = fsm.Fsm;
                if (!inner.Started)
                    continue;
                bool missUpdate = !e.InUpdate && !IdleUpdateSkipAllowed(inner);
                bool missLate = !e.InLate && !inner.Finished && !(Core.LateUpdateSkip && CallbackIsNoOp(inner, Core.FlagLateUpdate));
                if (!missUpdate && !missLate)
                    continue;

                MissedWakes++;
                if (MissedWakes <= MaxMissedLogs)
                {
                    FsmState state = inner.ActiveState;
                    Debug.LogWarning($"PlayMakerTurbo: FSM '{inner.Name}' on '{fsm.name}' (state '{(state != null ? state.Name : "-")}') had {(missUpdate ? "Update" : "LateUpdate")} work but was not queued. Queued now. Please report this.");
                }
                Queue(e);
            }
            Array.Clear(snapshot, 0, count);
        }

        // ---- FixedUpdate ----

        private void FixedUpdate()
        {
            long start = Stopwatch.GetTimestamp();
            int count = fixedProxies.Count;
            if (fixedSnapshot.Length < count)
                fixedSnapshot = new PlayMakerFixedUpdate[count * 2];
            fixedProxies.CopyTo(fixedSnapshot);

            inFixedStep = true;
            for (int i = 0; i < count; i++)
            {
                PlayMakerFixedUpdate proxy = fixedSnapshot[i];
                if (proxy == null || removedThisStep.Contains(proxy))
                    continue;

                // One try per proxy, like Unity's per-component call: an exception stops the rest of that proxy's FSMs.
                try
                {
                    if (Core.FixedUpdateSkip)
                        TickProxy(proxy);
                    else
                        proxy.TurboFixedUpdate();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, proxy);
                }
            }
            inFixedStep = false;
            removedThisStep.Clear();
            Array.Clear(fixedSnapshot, 0, count);
            Core.TickerFixedUpdateTicks += Stopwatch.GetTimestamp() - start;
        }

        // Same loop as PlayMakerFixedUpdate.FixedUpdate, minus FSMs whose FixedUpdate would do nothing. The
        // original tests fsm.Active first, which asks the engine several times; the managed tests go first here
        // because an FSM that fails them is not called either way.
        private static void TickProxy(PlayMakerFixedUpdate proxy)
        {
            PlayMakerFSM[] fsms = proxy.playMakerFSMs;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                Fsm inner = fsm.Fsm;
                if (!inner.HandleFixedUpdate)
                    continue;
                if (CallbackIsNoOp(inner, Core.FlagFixedUpdate))
                {
                    FixedUpdateSkipped++;
                    continue;
                }
                if (!fsm.Active)
                    continue;
                FixedUpdateCalls++;
                long inner0 = MeasureInner ? Stopwatch.GetTimestamp() : 0L;
                inner.FixedUpdate();
                if (inner0 != 0L)
                    FixedUpdateInnerTicks += Stopwatch.GetTimestamp() - inner0;
            }
        }

        // ---- shared ----

        private static int TakeSnapshot()
        {
            // Copy, because FSMs enable/disable each other while ticking and that edits FsmList.
            List<PlayMakerFSM> list = PlayMakerFSM.FsmList;
            int count = list.Count;
            if (snapshot.Length < count)
                snapshot = new PlayMakerFSM[count * 2];
            list.CopyTo(snapshot);
            return count;
        }

        // Fsm.Update does nothing when the FSM is finished, or when the active state has finished all its
        // actions, nothing is switching, no delayed event is pending and no breakpoint froze PlayMaker.
        private static bool IdleUpdateSkipAllowed(Fsm fsm)
        {
            if (!Core.IdleUpdateSkip)
                return false;
            if (fsm.Finished)
                return true;
            if (!fsm.activeStateEntered || fsm.switchToState != null || fsm.delayedEvents.Count != 0 || Fsm.HitBreakpoint)
                return false;

            FsmState state = fsm.ActiveState;
            return state == null || state.finished;
        }

        // Fsm.LateUpdate / FixedUpdate only do real work when an active action overrides the callback,
        // a state switch is pending, or a state with no active actions still has to fire FINISHED.
        private static bool CallbackIsNoOp(Fsm fsm, int callbackFlag)
        {
            if (!fsm.activeStateEntered)
                return true;
            if (fsm.switchToState != null)
                return false;

            FsmState state = fsm.ActiveState;
            if (state == null)
                return true;

            List<FsmStateAction> actions = state.ActiveActions;
            if (actions.Count == 0)
                return state.finished;

            for (int i = 0; i < actions.Count; i++)
            {
                if ((Core.ActionFlags(actions[i]) & callbackFlag) != 0)
                    return false;
            }
            return true;
        }
    }
}
