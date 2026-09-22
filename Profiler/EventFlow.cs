using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;

namespace MWCFsmProfiler
{
    internal class EventEntry
    {
        public string Name;
        public readonly Stat Stat = new Stat();
        public readonly Dictionary<Fsm, int> Senders = new Dictionary<Fsm, int>();
        public readonly Dictionary<Fsm, int> Receivers = new Dictionary<Fsm, int>();
        public int FromOutside; // sent by a script, a global transition or the engine, not by an FSM
        public int FromSelf;    // the FSM sent it to itself (FINISHED and the like)
    }

    // Times Fsm.ProcessEvent, the part of an event that looks up and runs a transition (the entered state's
    // OnEnter actions are timed on their own and nest inside), and records who sends each event to whom.
    // Keys are object references so recording does not build strings.
    internal static class EventFlow
    {
        public static readonly Dictionary<FsmEvent, EventEntry> Entries = new Dictionary<FsmEvent, EventEntry>(1024);

        public static void Reset()
        {
            Entries.Clear();
        }

        public static void Patch(HarmonyInstance harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Fsm), "ProcessEvent"),
                new HarmonyMethod(typeof(EventFlow).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                new HarmonyMethod(typeof(EventFlow).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)), null);
        }

        public static void Prefix(FsmEvent fsmEvent, out Sample __state)
        {
            __state = Probe.Begin();
            if (__state.Ticks == 0L)
                return;
            __state.Tag = fsmEvent;
            Probe.FrameEvents++;
            ActionTimings.ProcessedEvents++;
        }

        public static void Postfix(Fsm __instance, Sample __state)
        {
            if (__state.Ticks == 0L)
                return;

            FsmEvent fsmEvent = __state.Tag as FsmEvent;
            EventEntry entry = null;
            if (fsmEvent != null && !Entries.TryGetValue(fsmEvent, out entry))
            {
                entry = new EventEntry { Name = fsmEvent.Name };
                entry.Stat.NameId = Names.Get("Event " + fsmEvent.Name);
                Entries.Add(fsmEvent, entry);
            }

            Measured m = Probe.End(ref __state, Cat.Event, entry != null ? entry.Stat.NameId : -1);
            if (entry == null)
                return;
            entry.Stat.Add(ref m);
            Count(entry.Receivers, __instance);

            // Fsm.Event fills this static before the event is processed.
            Fsm sender = Fsm.EventData != null ? Fsm.EventData.SentByFsm : null;
            if (sender == null)
                entry.FromOutside++;
            else if (ReferenceEquals(sender, __instance))
                entry.FromSelf++;
            else
                Count(entry.Senders, sender);
        }

        private static void Count(Dictionary<Fsm, int> map, Fsm fsm)
        {
            int n;
            map.TryGetValue(fsm, out n);
            map[fsm] = n + 1;
        }

        public static string Describe(Fsm fsm)
        {
            if (fsm == null)
                return "?";
            if (ReferenceEquals(fsm.Owner, null))
                return fsm.Name;
            FsmEntry entry = FsmTimings.Get(fsm);
            return entry != null ? entry.Label : fsm.Name;
        }
    }
}
