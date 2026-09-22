using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MWCFsmProfiler
{
    // Chrome Trace Event format ("X" complete events), which ui.perfetto.dev and chrome://tracing open as a
    // flame chart on a timeline. Nesting comes from the timestamps, so the depth is not needed.
    // Thread 1 = main thread calls, thread 2 = frame markers and GC.
    internal static class TraceExport
    {
        public static void Write(TextWriter w, Span[] spans, int count, long t0, List<long> frameStarts, List<int> gcFrames, float singleFrameMs)
        {
            double toUs = 1000000.0 / Stopwatch.Frequency;
            CultureInfo inv = CultureInfo.InvariantCulture;
            JsonWriter j = new JsonWriter(w);

            j.BeginObject();
            j.Prop("displayTimeUnit", "ms");
            j.Name("traceEvents").BeginArray();
            Meta(j, 1, "Main thread (timed calls)");
            Meta(j, 2, "Frames");

            for (int i = 0; i < count; i++)
            {
                Span s = spans[i];
                if (s.Start < t0)
                    continue;
                string name = s.Name >= 0 && s.Name < Names.List.Count ? Names.List[s.Name] : "?";
                j.BeginObject().Prop("name", name).Prop("cat", Category(name)).Prop("ph", "X")
                    .Prop("ts", (s.Start - t0) * toUs, 3).Prop("dur", s.Duration * toUs, 3)
                    .Prop("pid", 1).Prop("tid", 1).EndObject();
            }

            for (int f = 0; f < frameStarts.Count; f++)
            {
                double start = (frameStarts[f] - t0) * toUs;
                double dur = f + 1 < frameStarts.Count ? (frameStarts[f + 1] - frameStarts[f]) * toUs : singleFrameMs * 1000.0;
                if (dur <= 0)
                    continue;
                j.BeginObject().Prop("name", string.Format(inv, "frame {0} ({1:F1} ms)", f, dur / 1000.0)).Prop("cat", "frame").Prop("ph", "X")
                    .Prop("ts", start, 3).Prop("dur", dur, 3).Prop("pid", 1).Prop("tid", 2).EndObject();
            }
            foreach (int f in gcFrames)
            {
                if (f < 0 || f >= frameStarts.Count)
                    continue;
                j.BeginObject().Prop("name", "GC collection in this frame").Prop("cat", "gc").Prop("ph", "i").Prop("s", "g")
                    .Prop("ts", (frameStarts[f] - t0) * toUs, 3).Prop("pid", 1).Prop("tid", 2).EndObject();
            }

            j.EndArray();
            j.EndObject();
        }

        private static void Meta(JsonWriter j, int tid, string name)
        {
            j.BeginObject().Prop("name", "thread_name").Prop("ph", "M").Prop("pid", 1).Prop("tid", tid);
            j.Name("args").BeginObject().Prop("name", name).EndObject();
            j.EndObject();
        }

        private static string Category(string name)
        {
            if (name.StartsWith("FSM "))
                return "fsm";
            if (name.StartsWith("Event "))
                return "event";
            if (name.StartsWith("Mod "))
                return "mod";
            if (name.StartsWith("Physics") || name.StartsWith("Rendering"))
                return "engine";
            return name.Contains(" @ ") ? "action" : "script";
        }
    }
}
