using System;
using System.Collections.Generic;

namespace MWCFsmProfiler
{
    internal struct Span
    {
        public long Start;
        public int Duration;
        public int Name;
        public int Depth;
    }

    // A frame that took far longer than usual, with every timed call it contained.
    internal class SpikeFrame
    {
        public int Frame;
        public float Ms;
        public float MedianMs;
        public bool Gc;
        public long FrameStart;
        public Span[] Spans;
        public int Dropped;
    }

    // Records every timed call as a span. Two uses:
    //  - Trace mode (F11): keeps all spans of the next N frames, exported for ui.perfetto.dev.
    //  - Spike capture (during a full recording): keeps only the current frame; when that frame turns out to
    //    be a spike, it is copied aside. Buffers are allocated once up front so recording never allocates.
    internal static class SpanRecorder
    {
        public static bool Active;
        public static bool TraceMode;
        public static bool TraceDone;
        public static int TraceFrames;
        public static int Dropped;

        public static int NamePhysics;
        public static int NameRender;

        private static Span[] spans = new Span[0];
        private static int count;
        private static int droppedThisFrame;
        private static long frameStart;

        public static readonly List<long> FrameStarts = new List<long>();
        public static readonly List<int> GcFrames = new List<int>();
        public static readonly List<SpikeFrame> Spikes = new List<SpikeFrame>();
        public const int MaxSpikes = 20;

        public static Span[] TraceSpans => spans;
        public static int TraceCount => count;

        public static void StartFrameCapture()
        {
            Prepare(1 << 18);
            TraceMode = false;
            Active = true;
        }

        public static void StartTrace(int frames)
        {
            Prepare(1 << 21);
            TraceMode = true;
            TraceFrames = frames;
            Active = true;
        }

        private static void Prepare(int capacity)
        {
            if (spans.Length != capacity)
                spans = new Span[capacity];
            count = 0;
            Dropped = 0;
            droppedThisFrame = 0;
            TraceDone = false;
            frameStart = 0L;
            FrameStarts.Clear();
            GcFrames.Clear();
            Spikes.Clear();
            NamePhysics = Names.Get("Physics simulation (+ collision callbacks)");
            NameRender = Names.Get("Rendering + OnGUI (main thread)");
        }

        public static void Stop()
        {
            Active = false;
        }

        // Frees the big trace buffer once it has been written out.
        public static void Release()
        {
            spans = new Span[0];
            count = 0;
        }

        public static void Add(long start, long elapsed, int name, int depth)
        {
            if (count >= spans.Length)
            {
                Dropped++;
                droppedThisFrame++;
                return;
            }
            spans[count].Start = start;
            spans[count].Duration = elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
            spans[count].Name = name;
            spans[count].Depth = depth;
            count++;
        }

        // Called by Timeline at each frame boundary, after the frame that just ended has been measured.
        public static void OnFrameEnd(int frame, long boundary, float ms, float median, bool gc, bool spike)
        {
            if (!Active)
                return;

            if (TraceMode)
            {
                if (frameStart == 0L)
                {
                    // Spans recorded before the first boundary belong to a partial frame; drop them.
                    count = 0;
                }
                FrameStarts.Add(boundary);
                if (gc)
                    GcFrames.Add(FrameStarts.Count - 1);
                frameStart = boundary;
                if (FrameStarts.Count > TraceFrames)
                {
                    Active = false;
                    TraceDone = true;
                }
                return;
            }

            if (spike && frameStart != 0L)
                KeepSpike(frame, ms, median, gc);
            frameStart = boundary;
            count = 0;
            droppedThisFrame = 0;
        }

        private static void KeepSpike(int frame, float ms, float median, bool gc)
        {
            if (Spikes.Count >= MaxSpikes)
            {
                int smallest = 0;
                for (int i = 1; i < Spikes.Count; i++)
                {
                    if (Spikes[i].Ms < Spikes[smallest].Ms)
                        smallest = i;
                }
                if (Spikes[smallest].Ms >= ms)
                    return;
                Spikes.RemoveAt(smallest);
            }

            Span[] copy = new Span[count];
            Array.Copy(spans, copy, count);
            Spikes.Add(new SpikeFrame
            {
                Frame = frame,
                Ms = ms,
                MedianMs = median,
                Gc = gc,
                FrameStart = frameStart,
                Spans = copy,
                Dropped = droppedThisFrame,
            });
        }
    }
}
