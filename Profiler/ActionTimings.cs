using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;

namespace MWCFsmProfiler
{
    // One action in one state of one FSM.
    internal class ActionEntry
    {
        public Type Type;
        public string FsmName;
        public string Path;
        public string State;
        public int Index;
        public readonly Stat Frame = new Stat(); // OnUpdate/OnLateUpdate/OnFixedUpdate
        public readonly Stat Enter = new Stat(); // OnEnter/OnExit
        public long WasteChecked;
        public long WasteSame;

        public string Where => FsmName + " / " + State + " #" + Index + " @ " + Path;
    }

    // Times OnEnter/OnExit and the per-frame callbacks of every PlayMaker action type, per action instance,
    // so the report can say which FSM and state an expensive action type lives in.
    internal static class ActionTimings
    {
        public static readonly Dictionary<FsmStateAction, ActionEntry> Entries = new Dictionary<FsmStateAction, ActionEntry>(16384);
        public static long ProcessedEvents;
        public static long MousePicks;
        public static int PatchedMethods;
        public static bool TimeEnterExit = true;

        private static readonly string[] frameCallbacks = { "OnUpdate", "OnLateUpdate", "OnFixedUpdate" };
        private static readonly string[] enterCallbacks = { "OnEnter", "OnExit" };

        public static void Reset()
        {
            Entries.Clear();
            ProcessedEvents = 0;
            MousePicks = 0;
        }

        public static void Patch(HarmonyInstance harmony)
        {
            HarmonyMethod prefix = Hook("Prefix");
            HarmonyMethod framePostfix = Hook("FramePostfix");
            HarmonyMethod enterPostfix = Hook("EnterPostfix");
            foreach (Type type in ActionTypes())
            {
                PatchCallbacks(harmony, type, frameCallbacks, prefix, framePostfix);
                if (TimeEnterExit)
                    PatchCallbacks(harmony, type, enterCallbacks, prefix, enterPostfix);
            }

            harmony.Patch(AccessTools.Method(typeof(ActionHelpers), "DoMousePick"), Hook("CountMousePick"), null, null);
        }

        private static void PatchCallbacks(HarmonyInstance harmony, Type type, string[] names, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            foreach (string callback in names)
            {
                MethodInfo method = type.GetMethod(callback, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method == null || method.IsAbstract || method.GetMethodBody() == null)
                    continue;
                try
                {
                    harmony.Patch(method, prefix, postfix, null);
                    PatchedMethods++;
                }
                catch (Exception e)
                {
                    MSCLoader.ModConsole.Print($"FSM Profiler: could not time {type.Name}.{callback}: {(e.InnerException ?? e).Message}");
                }
            }
        }

        public static IEnumerable<Type> ActionTypes()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }

                foreach (Type type in types)
                {
                    if (type != null && !type.ContainsGenericParameters && type.IsSubclassOf(typeof(FsmStateAction)))
                        yield return type;
                }
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(ActionTimings).GetMethod(name, BindingFlags.Public | BindingFlags.Static));
        }

        public static void Prefix(out Sample __state)
        {
            __state = Probe.Begin();
        }

        public static void FramePostfix(FsmStateAction __instance, Sample __state)
        {
            if (__state.Ticks == 0L)
                return;
            ActionEntry entry = Get(__instance);
            Measured m = Probe.End(ref __state, Cat.Action, entry.Frame.NameId);
            entry.Frame.Add(ref m);
        }

        public static void EnterPostfix(FsmStateAction __instance, Sample __state)
        {
            if (__state.Ticks == 0L)
                return;
            ActionEntry entry = Get(__instance);
            Measured m = Probe.End(ref __state, Cat.Action, entry.Enter.NameId);
            entry.Enter.Add(ref m);
        }

        public static ActionEntry Get(FsmStateAction action)
        {
            ActionEntry entry;
            if (!Entries.TryGetValue(action, out entry))
            {
                entry = Create(action);
                Entries.Add(action, entry);
            }
            return entry;
        }

        private static ActionEntry Create(FsmStateAction action)
        {
            Fsm fsm = action.Fsm;
            FsmState state = action.State;
            ActionEntry entry = new ActionEntry
            {
                Type = action.GetType(),
                FsmName = fsm != null ? fsm.Name : "?",
                Path = "?",
                State = state != null ? state.Name : "?",
                Index = state != null && state.Actions != null ? Array.IndexOf(state.Actions, action) : -1,
            };
            if (fsm != null && !ReferenceEquals(fsm.Owner, null))
            {
                FsmEntry owner = FsmTimings.Get(fsm);
                if (owner != null)
                {
                    entry.FsmName = owner.FsmName;
                    entry.Path = owner.Path;
                }
            }
            entry.Frame.NameId = Names.Get(entry.Type.Name + " @ " + entry.Where);
            entry.Enter.NameId = Names.Get(entry.Type.Name + ".OnEnter/OnExit @ " + entry.Where);
            return entry;
        }

        public static void CountMousePick()
        {
            if (Probe.Recording)
            {
                MousePicks++;
                Probe.FramePicks++;
            }
        }
    }
}
