using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Harmony;
using UnityEngine;

namespace MWCFsmProfiler
{
    // Times the Unity callbacks of every MonoBehaviour in the game's and mods' assemblies, every coroutine step,
    // and every MSCLoader mod callback. Also tracks the physics and rendering windows of the frame, so the time
    // (and allocations) of callbacks that run inside them can be taken out of the engine phases.
    internal static class ScriptTimings
    {
        public const int KindUpdate = 0;
        public const int KindLateUpdate = 1;
        public const int KindFixedUpdate = 2;
        public const int KindOnGUI = 3;
        public const int KindCoroutine = 4;
        public const int FirstMessageKind = 5;

        // Unity messages beyond the per-frame ones. OnAudioFilterRead is left out on purpose: it runs on the audio
        // thread, and the probe is main-thread only.
        private static readonly string[] messages =
        {
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
            "OnControllerColliderHit", "OnJointBreak",
            "OnWillRenderObject", "OnBecameVisible", "OnBecameInvisible", "OnPreCull", "OnPreRender", "OnPostRender",
            "OnRenderObject", "OnRenderImage",
            "OnMouseEnter", "OnMouseOver", "OnMouseExit", "OnMouseDown", "OnMouseUp", "OnMouseDrag",
            "OnAnimatorMove", "OnAnimatorIK", "OnEnable", "OnDisable", "Start", "Awake", "OnDestroy"
        };

        public static readonly string[] KindNames = BuildKindNames();
        public static readonly Dictionary<Type, Stat>[] Stats = NewStats();
        public static readonly long[] KindTicks = new long[KindNames.Length]; // inclusive, per callback kind
        public static readonly Dictionary<string, Stat> ModStats = new Dictionary<string, Stat>();
        public static readonly Dictionary<MethodBase, string> modMethods = new Dictionary<MethodBase, string>();
        public static readonly Dictionary<MethodBase, Stat> modStatByMethod = new Dictionary<MethodBase, Stat>();
        private static readonly Dictionary<MethodBase, int> messageKind = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<Type, string> coroutineNames = new Dictionary<Type, string>();

        public static long LastFixedEnd;
        public static long LastLateEnd;
        public static int PatchedMethods;
        public static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
        public static int PatchedCoroutines;

        // Open from the last FixedUpdate / LateUpdate script until FramePhases marks the end of physics / rendering.
        // Top-level timed calls inside a window are taken back out of that engine phase.
        public static bool PhysicsWindow, RenderWindow;
        public static long InPhysicsTicks, InRenderTicks;
        public static long InPhysicsBytes, InRenderBytes;

        // UnityEngine.UI (uGUI: EventSystem, canvases, layout) is managed code and is timed like any script.
        private static readonly string[] includedAssemblies = { "UnityEngine.UI" };
        private static readonly string[] excludedPrefixes = { "UnityEngine", "mscorlib", "System", "Mono.", "0Harmony", "MWCFsmProfiler", "Boo.", "UnityScript.Lang", "Newtonsoft", "NAudio", "NVorbis", "Ionic", "INIFileParser" };

        private static string[] BuildKindNames()
        {
            List<string> names = new List<string> { "Update", "LateUpdate", "FixedUpdate", "OnGUI", "coroutine" };
            names.AddRange(messages);
            return names.ToArray();
        }

        private static Dictionary<Type, Stat>[] NewStats()
        {
            Dictionary<Type, Stat>[] stats = new Dictionary<Type, Stat>[FirstMessageKind + messages.Length];
            for (int i = 0; i < stats.Length; i++)
                stats[i] = new Dictionary<Type, Stat>();
            return stats;
        }

        public static void Reset()
        {
            foreach (Dictionary<Type, Stat> d in Stats)
                d.Clear();
            Array.Clear(KindTicks, 0, KindTicks.Length);
            ModStats.Clear();
            modStatByMethod.Clear();
            LastFixedEnd = LastLateEnd = 0L;
            PhysicsWindow = RenderWindow = false;
            InPhysicsTicks = InRenderTicks = 0L;
            InPhysicsBytes = InRenderBytes = 0L;
        }

        public static void Patch(HarmonyInstance harmony)
        {
            // Harmony 1.2 keys __state by the patch class, so prefix and postfixes must live in the same class.
            HarmonyMethod prefix = Hook("Prefix");
            HarmonyMethod[] postfixes = { Hook("UpdatePostfix"), Hook("LatePostfix"), Hook("FixedPostfix"), Hook("GuiPostfix") };
            HarmonyMethod messagePostfix = Hook("MessagePostfix");
            HarmonyMethod coroutinePostfix = Hook("CoroutinePostfix");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (Type type in MonoBehaviourTypes())
            {
                for (int kind = 0; kind < postfixes.Length; kind++)
                {
                    MethodInfo method = type.GetMethod(KindNames[kind], flags, null, Type.EmptyTypes, null);
                    if (Patchable(method))
                        TryPatch(harmony, method, prefix, postfixes[kind]);
                }

                // Messages are found by name: their parameter (Collider, Collision, RenderTexture...) is optional.
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    int index = Array.IndexOf(messages, method.Name);
                    if (index < 0 || !Patchable(method) || method.ContainsGenericParameters)
                        continue;
                    messageKind[method] = FirstMessageKind + index;
                    TryPatch(harmony, method, prefix, messagePostfix);
                }

                PatchCoroutines(harmony, type, prefix, coroutinePostfix);
            }
            PatchMods(harmony, prefix);
            PatchEngineManaged(harmony, prefix, messagePostfix);
        }

        // Managed parts of UnityEngine.dll that the engine calls every frame. SendMouseEvents raycasts from every
        // camera to deliver OnMouseEnter/Over/Exit, whether or not any script uses them.
        private static void PatchEngineManaged(HarmonyInstance harmony, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            Type send = typeof(MonoBehaviour).Assembly.GetType("UnityEngine.SendMouseEvents");
            MethodInfo method = send != null ? send.GetMethod("DoSendMouseEvents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            if (!Patchable(method))
                return;
            engineNames[method] = "UnityEngine.SendMouseEvents.DoSendMouseEvents";
            TryPatch(harmony, method, prefix, Hook("EnginePostfix"));
        }

        private static readonly Dictionary<MethodBase, string> engineNames = new Dictionary<MethodBase, string>();
        private static readonly Dictionary<MethodBase, Type> engineKeys = new Dictionary<MethodBase, Type>();

        // Static engine methods have no instance; their stat is keyed by the declaring type under the Update kind.
        public static void EnginePostfix(MethodBase __originalMethod, Sample __state)
        {
            if (__state.Ticks == 0L)
                return;
            Type key;
            if (!engineKeys.TryGetValue(__originalMethod, out key))
            {
                key = __originalMethod.DeclaringType;
                engineKeys[__originalMethod] = key;
            }
            string name;
            engineNames.TryGetValue(__originalMethod, out name);
            Add(key, KindUpdate, ref __state, name);
        }

        private static bool Patchable(MethodInfo method)
        {
            return method != null && !method.IsAbstract && method.GetMethodBody() != null;
        }

        // A coroutine is a compiler-generated IEnumerator nested in the MonoBehaviour (C#: <Name>c__Iterator0 or
        // <Name>d__0; UnityScript: $Name$12 with its own nested enumerator). Each MoveNext is one step of it.
        private static void PatchCoroutines(HarmonyInstance harmony, Type owner, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            foreach (Type nested in AllNested(owner))
            {
                if (nested.ContainsGenericParameters || !typeof(IEnumerator).IsAssignableFrom(nested) || coroutineNames.ContainsKey(nested))
                    continue;
                MethodInfo moveNext = nested.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (!Patchable(moveNext))
                    continue;
                coroutineNames[nested] = owner.Name + "." + CoroutineName(nested.Name);
                TryPatch(harmony, moveNext, prefix, postfix);
                PatchedCoroutines++;
            }
        }

        private static IEnumerable<Type> AllNested(Type type)
        {
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return nested;
                foreach (Type deeper in AllNested(nested))
                    yield return deeper;
            }
        }

        private static string CoroutineName(string typeName)
        {
            int open = typeName.IndexOf('<'), close = typeName.IndexOf('>');
            if (open >= 0 && close > open)
                return typeName.Substring(open + 1, close - open - 1);
            string[] parts = typeName.Split(new[] { '$' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : typeName;
        }

        private static void TryPatch(HarmonyInstance harmony, MethodBase method, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                harmony.Patch(method, prefix, postfix, null);
                PatchedMethods++;
                Patched.Add(method);
            }
            catch (Exception e)
            {
                MSCLoader.ModConsole.Print($"FSM Profiler: could not time {method.DeclaringType.Name}.{method.Name}: {(e.InnerException ?? e).Message}");
            }
        }

        private static IEnumerable<Type> MonoBehaviourTypes()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (Array.IndexOf(includedAssemblies, name) < 0 && Array.Exists(excludedPrefixes, p => name.StartsWith(p, StringComparison.Ordinal)))
                    continue;

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
                    if (type != null && !type.ContainsGenericParameters && type.IsSubclassOf(typeof(MonoBehaviour)))
                        yield return type;
                }
            }
        }

        // MSCLoader keeps each mod's callbacks as Action fields (A_Update, ...) and calls them from its own
        // MonoBehaviours, so the mod methods themselves are patched and attributed to the mod's name.
        private static void PatchMods(HarmonyInstance harmony, HarmonyMethod prefix)
        {
            PropertyInfo loadedMods = typeof(MSCLoader.ModLoader).GetProperty("LoadedMods", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (loadedMods == null)
                return;

            HarmonyMethod postfix = Hook("ModPostfix");
            foreach (MSCLoader.Mod mod in (IEnumerable<MSCLoader.Mod>)loadedMods.GetValue(null, null))
            {
                if (mod.ID == "MWCFsmProfiler")
                    continue;
                foreach (string callback in new[] { "A_Update", "A_FixedUpdate", "A_OnGUI" })
                {
                    FieldInfo field = typeof(MSCLoader.Mod).GetField(callback, BindingFlags.NonPublic | BindingFlags.Instance);
                    Delegate action = field == null ? null : (Delegate)field.GetValue(mod);
                    if (action == null || modMethods.ContainsKey(action.Method) || action.Method.GetMethodBody() == null)
                        continue;
                    modMethods[action.Method] = mod.Name + " (" + callback.Substring(2) + ")";
                    TryPatch(harmony, action.Method, prefix, postfix);
                }
            }
        }

        // Active scripts whose per-frame callbacks run but are not timed (patch failed, assembly excluded, or a
        // shape the patcher does not recognise). Their time and allocations show up as gaps between timed calls.
        public static List<string> UntimedActiveScripts()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (b == null || !b.enabled)
                    continue;
                for (int kind = 0; kind < 3; kind++)
                {
                    MethodInfo found = null;
                    for (Type t = b.GetType(); t != null && t != typeof(MonoBehaviour) && found == null; t = t.BaseType)
                        found = t.GetMethod(KindNames[kind], flags, null, Type.EmptyTypes, null);
                    if (found == null || Patched.Contains(found))
                        continue;
                    string key = found.DeclaringType.FullName + "." + found.Name + "  [" + found.DeclaringType.Assembly.GetName().Name + "]";
                    int n;
                    counts.TryGetValue(key, out n);
                    counts[key] = n + 1;
                }
            }
            List<string> result = new List<string>();
            foreach (KeyValuePair<string, int> c in counts)
                result.Add(c.Value.ToString().PadLeft(5) + "  " + c.Key);
            result.Sort((a, b) => string.CompareOrdinal(b, a));
            return result;
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(ScriptTimings).GetMethod(name, BindingFlags.Public | BindingFlags.Static));
        }

        public static void Prefix(out Sample __state)
        {
            __state = Probe.Begin();
        }

        public static void UpdatePostfix(object __instance, Sample __state) { Add(__instance.GetType(), KindUpdate, ref __state, null); }

        public static void LatePostfix(object __instance, Sample __state)
        {
            Add(__instance.GetType(), KindLateUpdate, ref __state, null);
            LastLateEnd = Stopwatch.GetTimestamp();
            OpenRenderWindow();
        }

        public static void FixedPostfix(object __instance, Sample __state)
        {
            Add(__instance.GetType(), KindFixedUpdate, ref __state, null);
            LastFixedEnd = Stopwatch.GetTimestamp();
            OpenPhysicsWindow();
        }

        public static void GuiPostfix(object __instance, Sample __state) { Add(__instance.GetType(), KindOnGUI, ref __state, null); }

        public static void MessagePostfix(object __instance, MethodBase __originalMethod, Sample __state)
        {
            int kind;
            if (__state.Ticks != 0L && messageKind.TryGetValue(__originalMethod, out kind))
                Add(__instance.GetType(), kind, ref __state, null);
        }

        public static void CoroutinePostfix(object __instance, Sample __state)
        {
            if (__state.Ticks == 0L)
                return;
            Type type = __instance.GetType();
            string name;
            coroutineNames.TryGetValue(type, out name);
            Add(type, KindCoroutine, ref __state, name);
        }

        private static void Add(Type type, int kind, ref Sample start, string name)
        {
            if (start.Ticks == 0L)
                return;

            Stat stat;
            if (!Stats[kind].TryGetValue(type, out stat))
            {
                stat = new Stat { NameId = Names.Get(name != null ? (kind == KindCoroutine ? "coroutine " + name : name) : type.Name + "." + KindNames[kind]) };
                Stats[kind].Add(type, stat);
            }
            Measured m = Probe.End(ref start, Cat.Script, stat.NameId);
            stat.Add(ref m);
            KindTicks[kind] += m.Elapsed;

            // Only top-level calls: nested ones are already inside their caller's time.
            if (start.Depth == 0)
            {
                if (PhysicsWindow && kind != KindFixedUpdate)
                {
                    InPhysicsTicks += m.Elapsed;
                    InPhysicsBytes += m.Bytes;
                }
                if (RenderWindow && kind != KindLateUpdate)
                {
                    InRenderTicks += m.Elapsed;
                    InRenderBytes += m.Bytes;
                }
            }
        }

        // Reopened after every FixedUpdate / LateUpdate script, so the window starts at the last one: the engine
        // runs physics (and rendering) only after all scripts of that phase.
        private static void OpenPhysicsWindow()
        {
            PhysicsWindow = true;
            InPhysicsTicks = 0L;
            InPhysicsBytes = 0L;
            if (Probe.Recording)
                Timeline.PhysicsMemoryStart = GC.GetTotalMemory(false);
        }

        private static void OpenRenderWindow()
        {
            RenderWindow = true;
            InRenderTicks = 0L;
            InRenderBytes = 0L;
            if (Probe.Recording)
                Timeline.RenderMemoryStart = GC.GetTotalMemory(false);
        }

        // Mod callbacks can be lambdas or methods of helper classes, so they are keyed by method, not by instance.
        public static void ModPostfix(MethodBase __originalMethod, Sample __state)
        {
            if (__state.Ticks == 0L)
                return;

            Stat stat;
            if (!modStatByMethod.TryGetValue(__originalMethod, out stat))
            {
                string name;
                if (!modMethods.TryGetValue(__originalMethod, out name))
                    name = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
                if (!ModStats.TryGetValue(name, out stat))
                {
                    stat = new Stat { NameId = Names.Get("Mod " + name) };
                    ModStats.Add(name, stat);
                }
                modStatByMethod.Add(__originalMethod, stat);
            }
            Measured m = Probe.End(ref __state, Cat.Mod, stat.NameId);
            stat.Add(ref m);
        }
    }
}
