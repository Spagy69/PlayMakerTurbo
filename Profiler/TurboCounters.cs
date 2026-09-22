using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace MWCFsmProfiler
{
    // Reads PlayMakerTurbo's public static counters and switches through reflection, so the profiler has no hard
    // dependency on PlayMakerTurbo and still works on an unpatched game.
    internal static class TurboCounters
    {
        private static readonly Type ticker = Type.GetType("PlayMakerTurbo.FsmTicker, PlayMakerTurbo", false);
        private static readonly Type core = Type.GetType("PlayMakerTurbo.Core, PlayMakerTurbo", false);
        private static readonly Dictionary<string, long> atStart = new Dictionary<string, long>();

        public static bool Installed => ticker != null && core != null;

        public static void Start()
        {
            atStart.Clear();
            if (!Installed)
                return;

            foreach (FieldInfo field in Counters())
                atStart[field.Name] = (long)field.GetValue(null);
            SetValidateActive(true);
        }

        public static string Stop(int frames)
        {
            if (!Installed)
                return "PlayMakerTurbo: not installed";

            SetValidateActive(false);
            StringBuilder sb = new StringBuilder("PlayMakerTurbo counters (per frame):");
            foreach (FieldInfo field in Counters())
            {
                long start;
                atStart.TryGetValue(field.Name, out start);
                long delta = (long)field.GetValue(null) - start;
                if (field.Name.EndsWith("Ticks"))
                    sb.Append($"\n  {field.Name,-22} {delta * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames,10:F3} ms/frame");
                else
                    sb.Append($"\n  {field.Name,-22} {(double)delta / frames,10:F1}   (total {delta})");
            }
            sb.Append("\n  settings: ").Append(SettingsLine());
            return sb.ToString();
        }

        public static string SettingsLine()
        {
            if (!Installed)
                return "not installed";
            StringBuilder sb = new StringBuilder();
            foreach (string name in OptionNames())
                sb.Append($"{name}={(GetOption(name) ? 1 : 0)} ");
            return sb.ToString().TrimEnd();
        }

        // The on/off switches of PlayMakerTurbo.Core (every public static bool except the profiler's own ValidateActive).
        public static List<string> OptionNames()
        {
            List<string> names = new List<string>();
            if (!Installed)
                return names;
            foreach (FieldInfo field in core.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(bool) && field.Name != "ValidateActive")
                    names.Add(field.Name);
            }
            return names;
        }

        public static bool GetOption(string name)
        {
            FieldInfo f = core != null ? core.GetField(name, BindingFlags.Public | BindingFlags.Static) : null;
            return f != null && (bool)f.GetValue(null);
        }

        public static void SetOption(string name, bool value)
        {
            FieldInfo f = core != null ? core.GetField(name, BindingFlags.Public | BindingFlags.Static) : null;
            if (f != null)
                f.SetValue(null, value);
        }

        private static IEnumerable<FieldInfo> Counters()
        {
            foreach (Type type in new[] { ticker, core })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType == typeof(long))
                        yield return field;
                }
            }
        }

        // While recording, PlayMakerTurbo computes Fsm.Active both ways and counts mismatches,
        // which decides whether the faster isActiveAndEnabled variant may be switched on.
        private static void SetValidateActive(bool value)
        {
            core.GetField("ValidateActive").SetValue(null, value);
        }
    }
}
