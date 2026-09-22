using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MWCFsmProfiler
{
    internal class StateEntry
    {
        public string Name;
        public readonly Stat Stat = new Stat();
    }

    internal class FsmEntry
    {
        public Component Owner;
        public Fsm Fsm;
        public string Path;
        public string Root;
        public string FsmName;
        public readonly Stat[] Slots = { new Stat(), new Stat(), new Stat() }; // Update, LateUpdate, FixedUpdate
        public readonly Stat Total = new Stat();
        public readonly Dictionary<FsmState, StateEntry> States = new Dictionary<FsmState, StateEntry>();

        public string Label => FsmName + " | " + Path;
    }

    // Harmony 1.2 patches on Fsm.Update/LateUpdate/FixedUpdate. Patched on Fsm (not PlayMakerFSM) so the same
    // profiler works with and without PlayMakerTurbo. Sub-FSMs run by RunFSM share their parent's Owner and are
    // booked under it. Time is booked to the state that was active when the call started.
    internal static class FsmTimings
    {
        public const int SlotUpdate = 0;
        public const int SlotLateUpdate = 1;
        public const int SlotFixedUpdate = 2;
        public static readonly string[] SlotNames = { "Update", "LateUpdate", "FixedUpdate" };

        public static readonly Dictionary<int, FsmEntry> Entries = new Dictionary<int, FsmEntry>(4096);

        public static void Reset()
        {
            Entries.Clear();
        }

        public static void Prefix(Fsm __instance, out Sample __state)
        {
            __state = Probe.Begin();
            if (__state.Ticks != 0L)
                __state.Tag = __instance.ActiveState;
        }

        public static void UpdatePostfix(Fsm __instance, Sample __state) { Book(__instance, SlotUpdate, ref __state); }
        public static void LateUpdatePostfix(Fsm __instance, Sample __state) { Book(__instance, SlotLateUpdate, ref __state); }
        public static void FixedUpdatePostfix(Fsm __instance, Sample __state) { Book(__instance, SlotFixedUpdate, ref __state); }

        private static void Book(Fsm fsm, int slot, ref Sample s)
        {
            if (s.Ticks == 0L)
                return;

            FsmEntry entry = Get(fsm);
            StateEntry state = entry != null ? GetState(entry, s.Tag as FsmState) : null;
            Measured m = Probe.End(ref s, Cat.Fsm, state != null ? state.Stat.NameId : -1);
            if (entry == null)
                return;
            entry.Slots[slot].Add(ref m);
            entry.Total.Add(ref m);
            if (state != null)
                state.Stat.Add(ref m);
        }

        public static FsmEntry Get(Fsm fsm)
        {
            MonoBehaviour owner = fsm.Owner;
            if (ReferenceEquals(owner, null))
                return null;

            int id = owner.GetInstanceID();
            FsmEntry entry;
            if (!Entries.TryGetValue(id, out entry))
            {
                entry = CreateEntry(owner, fsm);
                Entries.Add(id, entry);
            }
            return entry;
        }

        private static StateEntry GetState(FsmEntry entry, FsmState state)
        {
            if (state == null)
                return null;
            StateEntry s;
            if (!entry.States.TryGetValue(state, out s))
            {
                s = new StateEntry { Name = state.Name };
                s.Stat.NameId = Names.Get("FSM " + entry.FsmName + " / " + state.Name + " @ " + entry.Path);
                entry.States.Add(state, s);
            }
            return s;
        }

        // Runs once per FSM, the first time it ticks while recording. The path is resolved here rather than
        // at report time because the object may be destroyed by then.
        private static FsmEntry CreateEntry(MonoBehaviour owner, Fsm fsm)
        {
            PlayMakerFSM component = owner as PlayMakerFSM;
            Transform t = owner.transform;
            FsmEntry entry = new FsmEntry
            {
                Owner = owner,
                Fsm = fsm,
                Path = GetPath(t),
                Root = t.root.name,
                FsmName = component != null ? component.FsmName : fsm.Name,
            };
            entry.Total.NameId = Names.Get("FSM " + entry.FsmName + " @ " + entry.Path);
            return entry;
        }

        public static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
