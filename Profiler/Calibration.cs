using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MWCFsmProfiler
{
    // Measures what one probe costs the code around it, per kind of probe, so the report can subtract it.
    //
    // 1. The Harmony glue plus the shared probe work: two identical methods timed in a loop, one patched with a
    //    minimal prefix/postfix and one not.
    // 2. The bookkeeping each real postfix adds on top (finding the FSM, its state, the action entry, the script
    //    type...): the real prefix/postfix pair is called directly on a live object from the scene, and the
    //    minimal pair is called the same way; the difference is the extra cost of that kind of probe.
    //
    // A probe's cost lands in its caller (the probe takes its timestamps as the last thing before and the first
    // thing after the timed call), so Probe subtracts it from the parent's self time.
    internal static class Calibration
    {
        public static double OverheadTicks;                           // minimal probe, Harmony glue included
        public static readonly double[] CategoryTicks = new double[Cat.Count];
        private const int Iterations = 100000;
        private const int DirectIterations = 20000;
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

        // The minimal postfix: end the probe, look a stat up, add to it.
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

        private delegate void Loop(int n);

        // Runs with recording on, so the probe does its full work (heap readings and span recording included),
        // but before any frame is recorded. Leaves entries behind in the timing tables; the caller resets them.
        public static void Run()
        {
            bool recording = Probe.Recording;
            Probe.MarkMainThread();
            Probe.Recording = true;

            OverheadTicks = Best(() =>
            {
                int x = 0;
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < Iterations; i++)
                    x = Plain(x);
                long t1 = Stopwatch.GetTimestamp();
                for (int i = 0; i < Iterations; i++)
                    x = Patched(x);
                long t2 = Stopwatch.GetTimestamp();
                return ((t2 - t1) - (t1 - t0)) / (double)Iterations;
            });

            double minimal = Direct(n =>
            {
                for (int i = 0; i < n; i++)
                {
                    Sample s;
                    Prefix(out s);
                    Postfix(s);
                }
            });

            PlayMakerFSM component = null;
            foreach (PlayMakerFSM f in Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (f.enabled && f.Fsm != null && f.Fsm.ActiveState != null && f.Fsm.ActiveState.Actions != null && f.Fsm.ActiveState.Actions.Length > 0)
                {
                    component = f;
                    break;
                }
            }

            for (int c = 0; c < Cat.Count; c++)
                CategoryTicks[c] = OverheadTicks;

            if (component != null)
            {
                Fsm fsm = component.Fsm;
                FsmStateAction action = fsm.ActiveState.Actions[0];
                FsmEvent fsmEvent = FsmEvent.Finished;

                CategoryTicks[Cat.Fsm] = OverheadTicks + Extra(minimal, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        Sample s;
                        FsmTimings.Prefix(fsm, out s);
                        FsmTimings.UpdatePostfix(fsm, s);
                    }
                });
                CategoryTicks[Cat.Action] = OverheadTicks + Extra(minimal, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        Sample s;
                        ActionTimings.Prefix(out s);
                        ActionTimings.FramePostfix(action, s);
                    }
                });
                CategoryTicks[Cat.Event] = OverheadTicks + Extra(minimal, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        Sample s;
                        EventFlow.Prefix(fsmEvent, out s);
                        EventFlow.Postfix(fsm, s);
                    }
                });
                CategoryTicks[Cat.Script] = OverheadTicks + Extra(minimal, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        Sample s;
                        ScriptTimings.Prefix(out s);
                        ScriptTimings.UpdatePostfix(component, s);
                    }
                });
                // Mod callbacks look their stat up by method, which costs about what a script lookup does.
                CategoryTicks[Cat.Mod] = CategoryTicks[Cat.Script];
            }

            Probe.Recording = recording;
            Probe.Reset();
        }

        private static double Extra(double minimal, Loop loop)
        {
            double extra = Direct(loop) - minimal;
            return extra > 0 ? extra : 0;
        }

        // Ticks per iteration of a directly called prefix/postfix pair, best of three rounds after a warm-up.
        private static double Direct(Loop loop)
        {
            loop(100);
            return Best(() =>
            {
                long t0 = Stopwatch.GetTimestamp();
                loop(DirectIterations);
                return (Stopwatch.GetTimestamp() - t0) / (double)DirectIterations;
            });
        }

        private static double Best(System.Func<double> round)
        {
            double best = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                double v = round();
                if (v < best)
                    best = v;
            }
            return best > 0 ? best : 0;
        }

        public static double OverheadMicroseconds => OverheadTicks * 1000000.0 / Stopwatch.Frequency;

        public static double Microseconds(int category)
        {
            return CategoryTicks[category] * 1000000.0 / Stopwatch.Frequency;
        }
    }
}
