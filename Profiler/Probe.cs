using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MWCFsmProfiler
{
    // What a timed call is booked under, so a frame can be split without counting nested time twice.
    internal static class Cat
    {
        public const int Fsm = 0;     // PlayMaker's own work in Fsm.Update/LateUpdate/FixedUpdate (self time)
        public const int Action = 1;  // action OnEnter/OnUpdate/OnLateUpdate/OnFixedUpdate/OnExit
        public const int Event = 2;   // Fsm.ProcessEvent: transition lookup and state switching
        public const int Script = 3;  // MonoBehaviour Update/LateUpdate/FixedUpdate/OnGUI
        public const int Mod = 4;     // MSCLoader mod callbacks
        public const int Count = 5;
        public static readonly string[] Names = { "PlayMaker core", "Actions", "Events", "Scripts", "Mods" };
    }

    // Start of a timed call. Tag carries whatever the postfix needs from before the call (e.g. the FSM's state).
    internal struct Sample
    {
        public long Ticks;
        public long Memory;
        public int Depth;
        public long Gap;   // heap growth since the previous top-level timed call ended (top-level calls only)
        public object Tag;
    }

    // Result of a timed call, added to one or more Stats.
    internal struct Measured
    {
        public long Elapsed;
        public long Self;
        public long Bytes;
        public long SelfBytes;
        public int Descendants;
        public int Children;
    }

    // Totals of one timed thing (an FSM, a state, an action instance, a script type, a mod callback...).
    internal class Stat
    {
        public long Ticks;       // inclusive: everything that ran inside
        public long Self;        // exclusive: minus timed calls nested inside
        public long Calls;
        public long Bytes;       // inclusive managed heap growth
        public long SelfBytes;
        public long Descendants; // timed calls nested inside (any depth), for overhead correction
        public long Children;    // timed calls directly inside
        public long MaxTicks;    // slowest single call
        public int NameId = -1;
        public long OverlayMark; // Self at the overlay's last refresh

        public void Add(ref Measured m)
        {
            Ticks += m.Elapsed;
            Self += m.Self;
            Calls++;
            Bytes += m.Bytes;
            SelfBytes += m.SelfBytes;
            Descendants += m.Descendants;
            Children += m.Children;
            if (m.Elapsed > MaxTicks)
                MaxTicks = m.Elapsed;
        }

        // Probe overhead lands almost entirely in the caller: the probe takes its timestamps as the last
        // thing before and the first thing after the timed call.
        public long CorrectedSelf => Math.Max(0L, Self - (long)(Calibration.OverheadTicks * Children));
        public long CorrectedTicks => Math.Max(0L, Ticks - (long)(Calibration.OverheadTicks * Descendants));
    }

    // Interned display names, so spans and stats carry an int instead of a string.
    internal static class Names
    {
        public static readonly List<string> List = new List<string>(8192);
        private static readonly Dictionary<string, int> ids = new Dictionary<string, int>(8192);

        public static int Get(string name)
        {
            int id;
            if (!ids.TryGetValue(name, out id))
            {
                id = List.Count;
                List.Add(name);
                ids.Add(name, id);
            }
            return id;
        }
    }

    // The hot path shared by every Harmony patch. Two timestamps, optionally two heap reads, a small stack
    // that turns inclusive time into self time, and nothing that allocates.
    internal static class Probe
    {
        public static bool Recording;
        public static bool MeasureMemory = true;
        public static int StackRepairs;

        private const int MaxDepth = 256;
        private static readonly long[] childTicks = new long[MaxDepth];
        private static readonly long[] childBytes = new long[MaxDepth];
        private static readonly int[] descendants = new int[MaxDepth];
        private static readonly int[] children = new int[MaxDepth];
        private static int depth;

        [ThreadStatic] private static bool isMainThread;

        // Called from the main thread (the mod's Update) before recording starts.
        public static void MarkMainThread()
        {
            isMainThread = true;
        }

        // Self time per category in the current frame; Timeline collects and clears it at the frame boundary.
        public static readonly long[] FrameSelf = new long[Cat.Count];
        public static readonly long[] TotalSelf = new long[Cat.Count];
        public static long FrameTopBytes;   // allocations inside top-level timed calls this frame
        public static int FrameEvents;
        public static int FramePicks;

        public static Sample Begin()
        {
            Sample s;
            s.Tag = null;
            // Only the main thread is measured: the stack below is not thread safe, and a mod's iterator or
            // helper may run on a worker thread.
            if (!Recording || !isMainThread)
            {
                s.Ticks = 0L;
                s.Memory = 0L;
                s.Depth = 0;
                s.Gap = 0L;
                return s;
            }

            if (depth < MaxDepth - 1)
            {
                s.Depth = depth;
                depth++;
                childTicks[depth] = 0L;
                childBytes[depth] = 0L;
                descendants[depth] = 0;
                children[depth] = 0;
            }
            else
            {
                s.Depth = -1;
            }
            s.Memory = MeasureMemory ? GC.GetTotalMemory(false) : 0L;
            s.Gap = s.Depth == 0 && lastMarkMemory != 0L && s.Memory > lastMarkMemory ? s.Memory - lastMarkMemory : 0L;
            s.Ticks = Stopwatch.GetTimestamp();
            return s;
        }

        // Call only when s.Ticks != 0. Books self time under the category and records a span if tracing.
        public static Measured End(ref Sample s, int category, int nameId)
        {
            long now = Stopwatch.GetTimestamp();
            Measured m;
            m.Elapsed = now - s.Ticks;
            m.Bytes = 0L;
            long memory = 0L;
            if (MeasureMemory)
            {
                memory = GC.GetTotalMemory(false);
                long bytes = memory - s.Memory;
                if (bytes > 0L)
                    m.Bytes = bytes;
            }

            if (s.Depth >= 0)
            {
                int slot = s.Depth + 1;
                m.Self = m.Elapsed - childTicks[slot];
                m.SelfBytes = Math.Max(0L, m.Bytes - childBytes[slot]);
                m.Descendants = descendants[slot];
                m.Children = children[slot];
                // Restoring the caller's depth also repairs the stack after an exception skipped a postfix.
                depth = s.Depth;
                if (depth == 0)
                {
                    FrameTopBytes += m.Bytes;
                    if (MeasureMemory)
                    {
                        if (s.Gap > 0L)
                            AddGap(nameId, s.Gap);
                        lastMarkMemory = memory;
                        lastMarkName = nameId;
                    }
                }
                childTicks[depth] += m.Elapsed;
                childBytes[depth] += m.Bytes;
                descendants[depth] += m.Descendants + 1;
                children[depth]++;
            }
            else
            {
                m.Self = m.Elapsed;
                m.SelfBytes = m.Bytes;
                m.Descendants = 0;
                m.Children = 0;
            }

            if (category >= 0)
            {
                FrameSelf[category] += m.Self;
                TotalSelf[category] += m.Self;
            }
            if (SpanRecorder.Active)
                SpanRecorder.Add(s.Ticks, m.Elapsed, nameId, s.Depth < 0 ? MaxDepth : s.Depth);
            return m;
        }

        // Allocations between timed calls: heap growth from the end of one top-level timed call (or a marker such
        // as the frame boundary) to the start of the next is booked to that gap, "after A, before B". This finds
        // allocations in code that is not timed without knowing in advance which code that is.
        public static readonly Dictionary<long, long> Gaps = new Dictionary<long, long>(8192);
        private static long lastMarkMemory;
        private static int lastMarkName = -1;

        private static void AddGap(int next, long bytes)
        {
            long key = ((long)lastMarkName << 32) | (uint)next;
            long total;
            Gaps.TryGetValue(key, out total);
            Gaps[key] = total + bytes;
        }

        // A fixed point in the frame that is not a timed call (frame boundary, end of physics, end of rendering).
        public static void Marker(int nameId, long memory)
        {
            if (!Recording || !MeasureMemory || depth != 0)
                return;
            if (lastMarkMemory != 0L && memory > lastMarkMemory)
                AddGap(nameId, memory - lastMarkMemory);
            lastMarkMemory = memory;
            lastMarkName = nameId;
        }

        public static int GapFrom(long key) { return (int)(key >> 32); }
        public static int GapTo(long key) { return (int)(key & 0xffffffffL); }

        // Heap growth while the main thread sleeps comes from other threads (audio, networking, mod workers).
        public static double BackgroundKBPerSecond = -1;

        public static void MeasureBackground()
        {
            double best = double.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                int gc = GC.CollectionCount(0);
                long before = GC.GetTotalMemory(false);
                long t0 = Stopwatch.GetTimestamp();
                System.Threading.Thread.Sleep(250);
                long after = GC.GetTotalMemory(false);
                double seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                if (GC.CollectionCount(0) != gc)
                    continue;
                best = Math.Min(best, Math.Max(0, after - before) / 1024.0 / seconds);
            }
            BackgroundKBPerSecond = best == double.MaxValue ? -1 : best;
        }

        // Called at the frame boundary, where no timed call can be running.
        public static void CheckStack()
        {
            if (depth != 0)
            {
                StackRepairs++;
                depth = 0;
            }
        }

        public static void Reset()
        {
            depth = 0;
            StackRepairs = 0;
            Array.Clear(FrameSelf, 0, FrameSelf.Length);
            Array.Clear(TotalSelf, 0, TotalSelf.Length);
            FrameEvents = 0;
            FramePicks = 0;
            FrameTopBytes = 0;
            Gaps.Clear();
            lastMarkMemory = 0L;
            lastMarkName = -1;
        }
    }
}
