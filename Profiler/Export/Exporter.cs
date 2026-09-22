using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MWCFsmProfiler
{
    // Writes one recording's folder: report.txt, report.json, report.html, and trace files for Perfetto.
    internal static class Exporter
    {
        public const int MaxActionInstances = 3000;

        public static void WriteAll(string folder, ReportData r, bool trace)
        {
            // Spike traces first: their file names go into the reports.
            for (int i = 0; i < r.SpikeList.Count && i < SpanRecorder.Spikes.Count; i++)
            {
                SpikeSummary summary = r.SpikeList[i];
                SpikeFrame spike = FindSpike(summary.Frame);
                if (spike == null)
                    continue;
                summary.File = $"spike_{i + 1:D2}_{summary.Ms:F0}ms.json";
                using (StreamWriter w = new StreamWriter(Path.Combine(folder, summary.File), false, new UTF8Encoding(false)))
                    TraceExport.Write(w, spike.Spans, spike.Spans.Length, spike.FrameStart, new List<long> { spike.FrameStart },
                        spike.Gc ? new List<int> { 0 } : new List<int>(), summary.Ms);
            }

            if (trace)
            {
                using (StreamWriter w = new StreamWriter(Path.Combine(folder, "trace.json"), false, new UTF8Encoding(false)))
                {
                    long t0 = SpanRecorder.FrameStarts.Count > 0 ? SpanRecorder.FrameStarts[0] : 0L;
                    TraceExport.Write(w, SpanRecorder.TraceSpans, SpanRecorder.TraceCount, t0, SpanRecorder.FrameStarts, SpanRecorder.GcFrames, 0);
                }
                if (SpanRecorder.Dropped > 0)
                    r.Notes.Add($"trace buffer full, {SpanRecorder.Dropped} calls were not recorded; use fewer frames");
            }

            File.WriteAllText(Path.Combine(folder, "report.txt"), ProfileReport.Build(r));

            StringBuilder json = new StringBuilder(1 << 20);
            using (StringWriter sw = new StringWriter(json, System.Globalization.CultureInfo.InvariantCulture))
                WriteJson(new JsonWriter(sw), r);
            File.WriteAllText(Path.Combine(folder, "report.json"), json.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "report.html"), HtmlReport.Build(json.ToString()), new UTF8Encoding(false));
        }

        private static SpikeFrame FindSpike(int frame)
        {
            foreach (SpikeFrame s in SpanRecorder.Spikes)
            {
                if (s.Frame == frame)
                    return s;
            }
            return null;
        }

        public static void WriteJson(JsonWriter j, ReportData r)
        {
            j.BeginObject();
            j.Prop("format", "mwc-fsm-profiler/2");
            j.Prop("created", r.Created.ToString("yyyy-MM-dd HH:mm:ss"));
            j.Prop("mode", r.Mode);

            j.Name("summary").BeginObject()
                .Prop("frames", r.Frames).Prop("seconds", r.Seconds, 2)
                .Prop("avgMs", r.AvgMs).Prop("fps", r.AvgMs > 0 ? 1000.0 / r.AvgMs : 0, 2)
                .Prop("p50", r.P50).Prop("p95", r.P95).Prop("p99", r.P99).Prop("maxMs", r.MaxMs).Prop("low1Fps", r.Low1Fps, 2)
                .Prop("spikes", r.Spikes).Prop("gcCount", r.GcCount).Prop("gcPerMin", r.Seconds > 0 ? r.GcCount / r.Seconds * 60 : 0, 2)
                .Prop("gcFrameAvgMs", r.GcFrameAvgMs).Prop("allocKBPerFrame", r.AllocKBPerFrame, 2)
                .Prop("allocTimedKB", r.AllocTimedKB, 2).Prop("allocPhysicsKB", r.AllocPhysicsKB, 2).Prop("allocPhysicsUntimedKB", r.AllocPhysicsUntimedKB, 2)
                .Prop("allocRenderKB", r.AllocRenderKB, 2).Prop("allocRenderUntimedKB", r.AllocRenderUntimedKB, 2)
                .Prop("heapStartMB", r.HeapStartMB, 1).Prop("heapEndMB", r.HeapEndMB, 1)
                .Prop("overheadUs", r.OverheadUs).Prop("overheadFsmUs", r.CategoryOverheadUs[Cat.Fsm]).Prop("overheadActionUs", r.CategoryOverheadUs[Cat.Action])
                .Prop("overheadScriptUs", r.CategoryOverheadUs[Cat.Script]).Prop("stackRepairs", r.StackRepairs)
                .Prop("eventsPerFrame", r.EventsPerFrame, 2).Prop("mousePicksPerFrame", r.MousePicksPerFrame, 2)
                .Prop("fixedStepsPerFrame", r.FixedStepsPerFrame, 3)
                .EndObject();

            j.Name("breakdown").BeginObject();
            for (int c = 0; c < Cat.Count; c++)
                j.Prop(Cat.Names[c], r.CategoryMs[c]);
            j.Prop("Physics", r.PhysicsMs).Prop("Rendering", r.RenderMs).Prop("Profiler overhead", r.ProbeOverheadMs).Prop("Not timed", r.UntrackedMs);
            j.EndObject();

            WriteTimeline(j);
            WriteRows(j, "fsms", r.Fsms, int.MaxValue);
            WriteRows(j, "actionInstances", r.ActionInstances, MaxActionInstances);
            WriteRows(j, "actionTypes", r.ActionTypes, int.MaxValue);
            WriteRows(j, "events", r.Events, int.MaxValue);
            WriteRows(j, "scripts", r.Scripts, int.MaxValue);
            WriteRows(j, "mods", r.Mods, int.MaxValue);
            WriteRows(j, "byFsmName", r.ByFsmName, 200);
            WriteRows(j, "byRoot", r.ByRoot, 200);
            WriteWaste(j, r);
            WriteRows(j, "idle", r.IdleRows, int.MaxValue);
            WriteRows(j, "gaps", r.GapRows, 200);
            WriteRows(j, "properties", r.PropertyRows, int.MaxValue);
            j.Prop("backgroundKBPerSecond", r.BackgroundKBPerSecond, 2);

            j.Name("spikes").BeginArray();
            foreach (SpikeSummary s in r.SpikeList)
            {
                j.BeginObject().Prop("frame", s.Frame).Prop("ms", s.Ms, 2).Prop("medianMs", s.MedianMs, 2).Prop("gc", s.Gc)
                    .Prop("coveredMs", s.CoveredMs, 2).Prop("cause", s.Cause).Prop("file", s.File).Prop("dropped", s.Dropped);
                j.Name("top").BeginArray();
                foreach (Row t in s.Top)
                    j.BeginObject().Prop("name", t.Name).Prop("selfMs", t.Self, 3).EndObject();
                j.EndArray().EndObject();
            }
            j.EndArray();

            if (r.Benchmark != null)
            {
                j.Name("benchmark");
                r.Benchmark(j);
            }

            j.Prop("turbo", r.TurboText);
            j.Prop("render", r.RenderText);
            j.Name("notes").BeginArray();
            foreach (string n in r.Notes)
                j.Value(n);
            j.EndArray();
            j.EndObject();
        }

        private static void WriteTimeline(JsonWriter j)
        {
            int n = Timeline.Count;
            FrameSample[] f = Timeline.Frames;
            j.Name("timeline").BeginObject();
            Series(j, "ms", n, i => f[i].Ms);
            Series(j, "fsm", n, i => f[i].Fsm);
            Series(j, "action", n, i => f[i].Action);
            Series(j, "event", n, i => f[i].Event);
            Series(j, "script", n, i => f[i].Script);
            Series(j, "mod", n, i => f[i].Mod);
            Series(j, "physics", n, i => f[i].Physics);
            Series(j, "render", n, i => f[i].Render);
            Series(j, "allocKB", n, i => f[i].AllocKB);
            Series(j, "heapMB", n, i => f[i].HeapMB);
            j.Name("gc").BeginArray();
            for (int i = 0; i < n; i++)
            {
                if (f[i].Gc)
                    j.Value(i);
            }
            j.EndArray();
            j.EndObject();
        }

        private static void Series(JsonWriter j, string name, int n, System.Func<int, float> get)
        {
            j.Name(name).BeginArray();
            for (int i = 0; i < n; i++)
                j.Value(get(i), 2);
            j.EndArray();
        }

        private static void WriteRows(JsonWriter j, string name, List<Row> rows, int max)
        {
            j.Name(name).BeginArray();
            for (int i = 0; i < rows.Count && i < max; i++)
                WriteRow(j, rows[i]);
            j.EndArray();
        }

        private static void WriteRow(JsonWriter j, Row r)
        {
            j.BeginObject()
                .Prop("name", r.Name).Prop("detail", r.Detail)
                .Prop("calls", r.Calls, 3).Prop("self", r.CorrSelf, 5).Prop("rawSelf", r.Self, 5).Prop("incl", r.Incl, 5)
                .Prop("usPerCall", r.UsPerCall, 3).Prop("maxMs", r.MaxMs, 3).Prop("kb", r.KB, 3).Prop("selfKb", r.SelfKB, 3);
            if (r.Children != null && r.Children.Count > 0)
            {
                j.Name("children").BeginArray();
                foreach (Row c in r.Children)
                    WriteRow(j, c);
                j.EndArray();
            }
            j.EndObject();
        }

        private static void WriteWaste(JsonWriter j, ReportData r)
        {
            j.Name("waste").BeginArray();
            foreach (Row w in r.WasteRows)
                j.BeginObject().Prop("type", w.Name).Prop("where", w.Detail).Prop("sameShare", w.SameShare, 4)
                    .Prop("samePerFrame", w.Calls, 3).Prop("checked", w.Checked).Prop("selfMs", w.CorrSelf, 5).EndObject();
            j.EndArray();
        }
    }
}
