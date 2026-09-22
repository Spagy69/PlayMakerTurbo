using System;
using System.Diagnostics;

namespace MWCFsmProfiler
{
    internal struct FrameSample
    {
        public float Ms;
        public float Fsm, Action, Event, Script, Mod; // self time per category
        public float Physics, Render;
        public float Overhead;  // probe cost outside timed calls (profiler only)
        public float AllocKB;
        public float AllocTimedKB;    // inside top-level timed calls
        public float AllocPhysicsKB;  // physics window: simulation + collision/trigger callbacks
        public float AllocRenderKB;   // render window: culling/render callbacks, OnGUI, image effects
        public float AllocPhysicsUntimedKB, AllocRenderUntimedKB;
        public float HeapMB;
        public bool Gc;
        public short Events;
        public short Picks;
    }

    // One sample per frame, in a buffer allocated up front. A frame is measured boundary to boundary
    // (FramePhases.Update), so its length and the time booked inside it cover exactly the same interval.
    internal static class Timeline
    {
        public const int Capacity = 36000;
        public static readonly FrameSample[] Frames = new FrameSample[Capacity];
        public static int Count;
        public static int Overflow;

        // Filled by FramePhases during the frame.
        public static long CurPhysics;
        public static long CurRender;
        public static long PhysicsMemoryStart, RenderMemoryStart;
        public static long CurPhysicsBytes, CurRenderBytes, CurPhysicsUntimed, CurRenderUntimed;

        public static int MarkerFrame = Names.Get("[frame boundary]");
        public static int MarkerPhysics = Names.Get("[end of physics]");
        public static int MarkerRender = Names.Get("[end of rendering]");
        public static int MarkerAfterSpike = Names.Get("[profiler: after spike check]");
        public static int MarkerAfterSpans = Names.Get("[profiler: after span recorder]");
        public static int MarkerBookkeepingEnd = Names.Get("[profiler: end of frame bookkeeping]");
        public static int MarkerAfterUpdate = Names.Get("[after all Update scripts, before animation/LateUpdate]");

        public static float SpikeMultiplier = 3f;
        public static int SpikeCount;

        private static readonly float[] recent = new float[120];
        private static readonly float[] sortScratch = new float[120];
        private static int recentCount;
        private static int recentNext;
        private static float median;
        private static int sinceMedian;

        private static long lastBoundary;
        private static long lastHeap;
        private static int lastGc;
        private static double tickToMs;

        public static void Begin()
        {
            Count = 0;
            Overflow = 0;
            SpikeCount = 0;
            recentCount = recentNext = sinceMedian = 0;
            median = 0f;
            lastBoundary = 0L;
            lastHeap = GC.GetTotalMemory(false);
            lastGc = GC.CollectionCount(0);
            CurPhysics = CurRender = 0L;
            CurPhysicsBytes = CurRenderBytes = CurPhysicsUntimed = CurRenderUntimed = 0L;
            tickToMs = 1000.0 / Stopwatch.Frequency;
            Array.Clear(Probe.FrameSelf, 0, Probe.FrameSelf.Length);
        }

        public static void Boundary()
        {
            long now = Stopwatch.GetTimestamp();
            Probe.CheckStack();

            long heap = GC.GetTotalMemory(false);
            int gcCount = GC.CollectionCount(0);
            bool gc = gcCount != lastGc;
            Probe.Marker(MarkerFrame, heap);

            if (lastBoundary != 0L)
            {
                float ms = (float)((now - lastBoundary) * tickToMs);
                bool spike = IsSpike(ms);
                Probe.Marker(MarkerAfterSpike, GC.GetTotalMemory(false));

                if (Count < Capacity)
                {
                    FrameSample f;
                    f.Ms = ms;
                    f.Fsm = (float)(Probe.FrameSelf[Cat.Fsm] * tickToMs);
                    f.Action = (float)(Probe.FrameSelf[Cat.Action] * tickToMs);
                    f.Event = (float)(Probe.FrameSelf[Cat.Event] * tickToMs);
                    f.Script = (float)(Probe.FrameSelf[Cat.Script] * tickToMs);
                    f.Mod = (float)(Probe.FrameSelf[Cat.Mod] * tickToMs);
                    f.Physics = (float)(CurPhysics * tickToMs);
                    f.Render = (float)(CurRender * tickToMs);
                    f.Overhead = (float)(Probe.FrameOverhead * tickToMs);
                    // Heap growth between boundaries; when a collection ran the drop hides it, so count none.
                    f.AllocKB = heap > lastHeap ? (heap - lastHeap) / 1024f : 0f;
                    f.HeapMB = heap / 1048576f;
                    f.AllocTimedKB = Probe.FrameTopBytes / 1024f;
                    f.AllocPhysicsKB = CurPhysicsBytes / 1024f;
                    f.AllocRenderKB = CurRenderBytes / 1024f;
                    f.AllocPhysicsUntimedKB = CurPhysicsUntimed / 1024f;
                    f.AllocRenderUntimedKB = CurRenderUntimed / 1024f;
                    f.Gc = gc;
                    f.Events = (short)Math.Min(Probe.FrameEvents, short.MaxValue);
                    f.Picks = (short)Math.Min(Probe.FramePicks, short.MaxValue);
                    Frames[Count++] = f;
                }
                else
                {
                    Overflow++;
                }

                if (spike)
                    SpikeCount++;
                SpanRecorder.OnFrameEnd(Count - 1, now, ms, median, gc, spike);
                Probe.Marker(MarkerAfterSpans, GC.GetTotalMemory(false));
                AbRunner.OnFrame(ms, gc);
            }
            else
            {
                SpanRecorder.OnFrameEnd(-1, now, 0f, 0f, false, false);
            }

            lastBoundary = now;
            lastHeap = heap;
            lastGc = gcCount;
            CurPhysics = CurRender = 0L;
            CurPhysicsBytes = CurRenderBytes = CurPhysicsUntimed = CurRenderUntimed = 0L;
            Probe.FrameTopBytes = 0L;
            Probe.FrameOverhead = 0;
            Array.Clear(Probe.FrameSelf, 0, Probe.FrameSelf.Length);
            Probe.FrameEvents = 0;
            Probe.FramePicks = 0;
            Probe.Marker(MarkerBookkeepingEnd, GC.GetTotalMemory(false));
        }

        // A spike is a frame several times longer than the median of the last two seconds or so.
        private static bool IsSpike(float ms)
        {
            bool spike = recentCount >= 30 && median > 0f && ms > median * SpikeMultiplier;

            // Spikes stay out of the median so one long stall does not raise the bar for the next one.
            if (!spike)
            {
                recent[recentNext] = ms;
                recentNext = (recentNext + 1) % recent.Length;
                if (recentCount < recent.Length)
                    recentCount++;
            }
            if (++sinceMedian >= 30 || median == 0f)
            {
                sinceMedian = 0;
                Array.Copy(recent, sortScratch, recentCount);
                Array.Sort(sortScratch, 0, recentCount, FloatComparer.Instance);
                median = recentCount > 0 ? sortScratch[recentCount / 2] : 0f;
            }
            return spike;
        }
    }
}
