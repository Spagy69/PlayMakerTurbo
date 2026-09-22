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
    public class FsmTicker : MonoBehaviour
    {
        private static FsmTicker instance;
        private static PlayMakerFSM[] snapshot = new PlayMakerFSM[4096];
        private static readonly List<PlayMakerFixedUpdate> fixedProxies = new List<PlayMakerFixedUpdate>();
        private static readonly HashSet<PlayMakerFixedUpdate> removedThisStep = new HashSet<PlayMakerFixedUpdate>();
        private static PlayMakerFixedUpdate[] fixedSnapshot = new PlayMakerFixedUpdate[512];
        private static bool inFixedStep;

        // Read by MWCFsmProfiler through reflection.
        public static long UpdateCalls;
        public static long UpdateSkipped;
        public static long LateUpdateCalls;
        public static long LateUpdateSkipped;
        public static long FixedUpdateCalls;
        public static long FixedUpdateSkipped;

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

        private void Update()
        {
            long start = Stopwatch.GetTimestamp();
            // The original calls this at the top of every Fsm.Update; it only flips a static flag.
            FsmTime.RealtimeBugFix();

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

                UpdateCalls++;
                try
                {
                    fsm.TurboUpdate();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, fsm);
                }
            }
            Array.Clear(snapshot, 0, count);
            Core.TickerUpdateTicks += Stopwatch.GetTimestamp() - start;
        }

        private void LateUpdate()
        {
            long start = Stopwatch.GetTimestamp();
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

                LateUpdateCalls++;
                try
                {
                    fsm.TurboLateUpdate();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, fsm);
                }
            }
            Array.Clear(snapshot, 0, count);
            FsmVariables.GlobalVariablesSynced = false;
            Core.TickerLateUpdateTicks += Stopwatch.GetTimestamp() - start;
        }

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

        // Same loop as PlayMakerFixedUpdate.FixedUpdate, minus FSMs whose FixedUpdate would do nothing.
        private static void TickProxy(PlayMakerFixedUpdate proxy)
        {
            PlayMakerFSM[] fsms = proxy.playMakerFSMs;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm.Active && fsm.Fsm.HandleFixedUpdate)
                {
                    Fsm inner = fsm.Fsm;
                    if (CallbackIsNoOp(inner, Core.FlagFixedUpdate))
                    {
                        FixedUpdateSkipped++;
                        continue;
                    }
                    FixedUpdateCalls++;
                    inner.FixedUpdate();
                }
            }
        }

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
