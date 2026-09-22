using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MWCFsmProfiler
{
    // Same conditions for every benchmark block: the player cannot move or look around, game time and weather
    // stand still, traffic is gone. Everything is restored in reverse order when the benchmark ends.
    // It changes game state, so it is meant for a copy of a save that is not saved afterwards.
    internal static class Benchmark
    {
        // Global variables that hold the time of day (PlayMakerGlobals in resources.assets).
        private static readonly string[] timeGlobals = { "GlobalTime", "ClockHours", "ClockMinutes", "TimeRotationHour", "TimeRotationMinute", "GlobalHour", "GlobalDay" };
        private static readonly string[] trafficRoots = { "TRAFFIC", "NPC_CARS", "TRAIN" };

        private static readonly List<Action> restore = new List<Action>();
        public static readonly List<string> Log = new List<string>();
        public static bool Locked;

        // Returns null when locked, otherwise why it could not be.
        public static string Lock(bool freezeTraffic)
        {
            if (Locked)
                return null;
            Log.Clear();
            restore.Clear();

            string blocker = PlayerBlocker();
            if (blocker != null)
                return blocker;

            LockPlayer();
            FreezeTime();
            DisableFsm("MAP/WEATHER/Clouds", "Weather", "weather");
            if (freezeTraffic)
            {
                foreach (string root in trafficRoots)
                {
                    GameObject go = GameObject.Find(root);
                    if (go == null || !go.activeSelf)
                        continue;
                    go.SetActive(false);
                    restore.Add(() => go.SetActive(true));
                    Log.Add("traffic: deactivated " + root);
                }
            }
            Locked = true;
            return null;
        }

        public static void Unlock()
        {
            for (int i = restore.Count - 1; i >= 0; i--)
            {
                try
                {
                    restore[i]();
                }
                catch (Exception e)
                {
                    ModConsole.Error("FSM Profiler: restoring after the benchmark failed: " + e.Message);
                }
            }
            restore.Clear();
            Locked = false;
        }

        private static string PlayerBlocker()
        {
            FsmString vehicle = FsmVariables.GlobalVariables.FindFsmString("PlayerCurrentVehicle");
            if (vehicle != null && !string.IsNullOrEmpty(vehicle.Value))
                return "get out of the vehicle (" + vehicle.Value + ") first, the benchmark runs on foot";
            FsmBool menu = FsmVariables.GlobalVariables.FindFsmBool("PlayerInMenu");
            if (menu != null && menu.Value)
                return "close the menu first";
            FsmBool seated = FsmVariables.GlobalVariables.FindFsmBool("PlayerSeated");
            if (seated != null && seated.Value)
                return "stand up first";
            if (GameObject.Find("PLAYER") == null)
                return "no PLAYER object, load a save first";
            return null;
        }

        // CharacterMotor is UnityScript (Assembly-UnityScript-firstpass), so it is reached by name. MouseLook keeps
        // running with zero sensitivity rather than being disabled, so the frame keeps the same work.
        private static void LockPlayer()
        {
            GameObject player = GameObject.Find("PLAYER");
            foreach (MonoBehaviour b in player.GetComponents<MonoBehaviour>())
            {
                if (b == null || b.GetType().Name != "CharacterMotor")
                    continue;
                FieldInfo canControl = b.GetType().GetField("canControl", BindingFlags.Public | BindingFlags.Instance);
                if (canControl == null || canControl.FieldType != typeof(bool))
                {
                    Log.Add("player: CharacterMotor has no canControl field, movement NOT locked (do not touch the keyboard)");
                    continue;
                }
                object old = canControl.GetValue(b);
                canControl.SetValue(b, false);
                restore.Add(() => { if (b != null) canControl.SetValue(b, old); });
                Log.Add("player: CharacterMotor.canControl = false");
            }

            foreach (MouseLook look in UnityEngine.Object.FindObjectsOfType<MouseLook>())
            {
                MouseLook l = look;
                float x = l.sensitivityX, y = l.sensitivityY;
                l.sensitivityX = 0f;
                l.sensitivityY = 0f;
                restore.Add(() => { if (l != null) { l.sensitivityX = x; l.sensitivityY = y; } });
                Log.Add("view: MouseLook sensitivity 0 on " + FsmTimings.GetPath(l.transform));
            }
        }

        // Time.timeScale = 0 would stop physics and FSMs, and GlobalTimeScale = 0 would make "Calc rates" divide
        // by zero. Instead the FSMs that write the time globals are switched off; the ones that only read them
        // (sun rotation, clocks) keep running and simply see a time that does not move.
        private static void FreezeTime()
        {
            ClockAtLock = Clock();
            foreach (TimeWriter w in FindTimeWriters())
            {
                // Only an FSM whose current state writes the time is advancing it now. States that write it on an
                // event (day change, reset after sleeping) are not running and stay untouched.
                if (!w.ActiveNow || !w.Fsm.enabled)
                    continue;
                PlayMakerFSM fsm = w.Fsm;
                fsm.enabled = false;
                restore.Add(() => { if (fsm != null) fsm.enabled = true; });
                Log.Add(Short($"time: disabled FSM {fsm.FsmName} on {FsmTimings.GetPath(fsm.transform)} ({w.What})"));
            }
        }

        public static float ClockAtLock = -1f;

        // Game time in minutes from the clock globals, or -1 when the game has no such globals.
        public static float Clock()
        {
            FsmFloat h = FsmVariables.GlobalVariables.FindFsmFloat("ClockHours");
            FsmFloat m = FsmVariables.GlobalVariables.FindFsmFloat("ClockMinutes");
            if (h == null || m == null)
                return -1f;
            return h.Value * 60f + m.Value;
        }

        public static string ClockCheck()
        {
            float now = Clock();
            if (ClockAtLock < 0f || now < 0f)
                return "game time could not be checked (no ClockHours/ClockMinutes globals)";
            float moved = now - ClockAtLock;
            return Mathf.Abs(moved) < 0.01f
                ? "game time stood still during the benchmark"
                : $"WARNING: game time moved by {moved:F1} min during the benchmark; freezing did not fully work";
        }

        // The in-game console cannot show very long lines.
        private static string Short(string s)
        {
            return s.Length > 160 ? s.Substring(0, 157) + "..." : s;
        }

        private static void DisableFsm(string path, string fsmName, string what)
        {
            GameObject go = GameObject.Find(path);
            if (go == null)
            {
                Log.Add($"{what}: {path} not found, not frozen");
                return;
            }
            foreach (PlayMakerFSM fsm in go.GetComponents<PlayMakerFSM>())
            {
                if (fsm.FsmName != fsmName || !fsm.enabled)
                    continue;
                PlayMakerFSM f = fsm;
                f.enabled = false;
                restore.Add(() => { if (f != null) f.enabled = true; });
                Log.Add($"{what}: disabled FSM {fsmName} on {path}");
            }
        }

        internal class TimeWriter
        {
            public PlayMakerFSM Fsm;
            public string What;
            public bool ActiveNow; // the FSM's current state contains the write
        }

        // Finds actions that write a time global. PlayMaker marks no direction on fields, so writing is decided
        // per action type (see IsWrite); the scan button lists the result for checking.
        public static List<TimeWriter> FindTimeWriters()
        {
            HashSet<string> globals = new HashSet<string>(timeGlobals);
            List<TimeWriter> result = new List<TimeWriter>();
            foreach (PlayMakerFSM component in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                Fsm fsm = component.Fsm;
                if (fsm == null || fsm.States == null)
                    continue;
                List<string> hits = new List<string>();
                bool activeNow = false;
                foreach (FsmState state in fsm.States)
                {
                    if (state.Actions == null)
                        continue;
                    foreach (FsmStateAction action in state.Actions)
                    {
                        if (action == null)
                            continue;
                        foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (!IsWrite(action.GetType(), field))
                                continue;
                            NamedVariable v = field.GetValue(action) as NamedVariable;
                            if (v == null || !v.UseVariable || !globals.Contains(v.Name) || fsm.Variables.FindVariable(v.Name) != null)
                                continue;
                            hits.Add($"{state.Name}/{action.GetType().Name}.{field.Name} -> {v.Name}");
                            if (state == fsm.ActiveState)
                                activeNow = true;
                        }
                    }
                }
                if (hits.Count > 0)
                    result.Add(new TimeWriter { Fsm = component, What = string.Join(", ", hits.ToArray()), ActiveNow = activeNow });
            }
            return result;
        }

        // Which field of which action type is written:
        //  - storeResult / storeXxx always (FloatOperator, GetDistance, ...), except GetFsm* copies: they copy a
        //    value from another FSM, and once the source stands still they keep writing the same value;
        //  - xxxVariable / variable only for setters and arithmetic (SetFloatValue, FloatAdd, IntClamp, ...);
        //    in IntChanged, FloatCompare, ConvertIntToString and the like the same names are inputs;
        //  - ConvertXToY: only the field of type FsmY.
        private static readonly System.Text.RegularExpressions.Regex arithmetic =
            new System.Text.RegularExpressions.Regex("^(Float|Int)(Add|Subtract|Multiply|Divide|Clamp|Abs|Operator|Increment|Decrement|Wrap)");

        private static bool IsWrite(Type type, FieldInfo field)
        {
            string t = type.Name, f = field.Name;
            if (f.StartsWith("store"))
                return !t.StartsWith("GetFsm");
            if (t.StartsWith("Convert"))
            {
                int to = t.IndexOf("To", 7, StringComparison.Ordinal);
                return to > 0 && field.FieldType.Name == "Fsm" + t.Substring(to + 2);
            }
            if (f.EndsWith("Variable") || f == "variable")
                return t.StartsWith("Set") || arithmetic.IsMatch(t);
            return false;
        }

        public static string DescribeTimeWriters()
        {
            StringBuilder sb = new StringBuilder("FSMs that write a time-of-day global. [ACTIVE] = its current state writes it, so it is switched off during a benchmark:\n");
            foreach (TimeWriter w in FindTimeWriters())
                sb.AppendLine(Short($"  {(w.ActiveNow ? "[ACTIVE] " : "")}{w.Fsm.FsmName} | {FsmTimings.GetPath(w.Fsm.transform)} | {w.What}"));
            return sb.ToString();
        }

        // One line that must match between two sessions for their comparison to mean something. Turbo's state is
        // left out on purpose: it is what the two sessions are meant to differ in.
        public static string ConditionsLine()
        {
            StringBuilder sb = new StringBuilder();
            GameObject player = GameObject.Find("PLAYER");
            if (player != null)
            {
                Vector3 p = player.transform.position;
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "pos {0:F0},{1:F0},{2:F0}; ", p.x, p.y, p.z);
            }
            sb.Append("quality ").Append(QualitySettings.names[QualitySettings.GetQualityLevel()])
                .Append("; shadows ").Append(QualitySettings.shadowDistance)
                .Append("; ").Append(Screen.width).Append('x').Append(Screen.height).Append("; mods");
            foreach (Mod mod in LoadedMods())
                sb.Append(' ').Append(mod.ID);
            return sb.ToString();
        }

        // What the numbers depend on, so two sessions can be checked for comparable conditions.
        public static void WriteConditions(JsonWriter j)
        {
            GameObject player = GameObject.Find("PLAYER");
            j.BeginObject();
            if (player != null)
            {
                Vector3 p = player.transform.position;
                j.Prop("position", $"{p.x:F1} {p.y:F1} {p.z:F1}").Prop("rotationY", player.transform.eulerAngles.y, 1);
            }
            foreach (string g in new[] { "ClockHours", "ClockMinutes" })
            {
                FsmFloat f = FsmVariables.GlobalVariables.FindFsmFloat(g);
                if (f != null)
                    j.Prop(g, f.Value, 2);
            }
            FsmFloat rain = FsmVariables.GlobalVariables.FindFsmFloat("RainIntensity");
            if (rain != null)
                j.Prop("rainIntensity", rain.Value, 3);
            j.Prop("quality", QualitySettings.names[QualitySettings.GetQualityLevel()])
                .Prop("shadowDistance", QualitySettings.shadowDistance, 1).Prop("resolution", Screen.width + "x" + Screen.height)
                .Prop("vSync", QualitySettings.vSyncCount).Prop("unity", Application.unityVersion)
                .Prop("turboInstalled", TurboCounters.Installed).Prop("turboSettings", TurboCounters.SettingsLine());
            j.Name("mods").BeginArray();
            foreach (Mod mod in LoadedMods())
                j.Value(mod.ID + " " + mod.Version);
            j.EndArray();
            j.Name("frozen").BeginArray();
            foreach (string l in Log)
                j.Value(l);
            j.EndArray();
            j.EndObject();
        }

        private static IEnumerable<Mod> LoadedMods()
        {
            PropertyInfo p = typeof(ModLoader).GetProperty("LoadedMods", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return p != null ? (IEnumerable<Mod>)p.GetValue(null, null) : new Mod[0];
        }

        // Benchmark spot: the same place and view for every session.
        public static void SaveSpot(string file)
        {
            GameObject player = GameObject.Find("PLAYER");
            if (player == null)
                return;
            Vector3 p = player.transform.position;
            Vector3 r = player.transform.eulerAngles;
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            float pitch = cam != null ? cam.localEulerAngles.x : 0f;
            File.WriteAllText(file, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4}", p.x, p.y, p.z, r.y, pitch));
            ModConsole.Print("<color=orange>FSM Profiler:</color> benchmark spot saved.");
        }

        public static void TeleportToSpot(string file)
        {
            GameObject player = GameObject.Find("PLAYER");
            if (player == null || !File.Exists(file))
            {
                ModConsole.Print("FSM Profiler: no saved benchmark spot (or no PLAYER).");
                return;
            }
            string[] v = File.ReadAllText(file).Split(' ');
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            player.transform.position = new Vector3(float.Parse(v[0], inv), float.Parse(v[1], inv), float.Parse(v[2], inv));
            player.transform.eulerAngles = new Vector3(0f, float.Parse(v[3], inv), 0f);

            // The camera's MouseLook keeps the pitch in a private field and rewrites the camera from it every frame.
            float pitch = v.Length > 4 ? float.Parse(v[4], inv) : 0f;
            if (pitch > 180f)
                pitch -= 360f;
            FieldInfo rotationY = typeof(MouseLook).GetField("rotationY", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (MouseLook look in UnityEngine.Object.FindObjectsOfType<MouseLook>())
            {
                if (look.axes == MouseLook.RotationAxes.MouseY && rotationY != null)
                    rotationY.SetValue(look, -pitch);
            }
            ModConsole.Print("<color=orange>FSM Profiler:</color> moved to the benchmark spot.");
        }
    }
}
