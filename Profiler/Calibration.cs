using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harmony;

namespace MWCFsmProfiler
{
    // Measures what one probe (Harmony prefix + postfix with a dictionary lookup) costs its caller, so the
    // report can subtract it. Two identical methods are timed in a loop, one patched and one not.
    internal static class Calibration
    {
        public static double OverheadTicks;
        private const int Iterations = 100000;
        private static readonly Dictionary<object, Stat> stats = new Dictionary<object, Stat>();
        private static readonly object key = new object();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Patched(int x) { return x + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Plain(int x) { return x + 1; }

        public static void Prefix(out Sample __state)
        {
            __state = Probe.Begin();
        }

        // Does the same work as the real postfixes: end the probe, look the stat up, add to it.
        public static void Postfix(Sample __state)
        {
            if (__state.Ticks == 0L)
                return;
            Measured m = Probe.End(ref __state, -1, -1);
            Stat stat;
            if (!stats.TryGetValue(key, out stat))
            {
                stat = new Stat();
                stats.Add(key, stat);
            }
            stat.Add(ref m);
        }

        public static void Patch(HarmonyInstance harmony)
        {
            harmony.Patch(typeof(Calibration).GetMethod("Patched"),
                new HarmonyMethod(typeof(Calibration).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                new HarmonyMethod(typeof(Calibration).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)), null);
        }

        // Runs with recording on (the probe does its full work) but before any frame is recorded.
        public static void Run()
        {
            bool recording = Probe.Recording;
            bool spans = SpanRecorder.Active;
            Probe.MarkMainThread();
            Probe.Recording = true;
            SpanRecorder.Active = false;

            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                int x = 0;
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < Iterations; i++)
                    x = Plain(x);
                long t1 = Stopwatch.GetTimestamp();
                for (int i = 0; i < Iterations; i++)
                    x = Patched(x);
                long t2 = Stopwatch.GetTimestamp();
                double perCall = ((t2 - t1) - (t1 - t0)) / (double)Iterations;
                if (perCall < best)
                    best = perCall;
            }
            OverheadTicks = best > 0 ? best : 0;

            Probe.Recording = recording;
            SpanRecorder.Active = spans;
            Probe.Reset();
        }

        public static double OverheadMicroseconds => OverheadTicks * 1000000.0 / Stopwatch.Frequency;
    }
}
