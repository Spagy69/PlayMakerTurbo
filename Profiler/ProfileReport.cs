using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MWCFsmProfiler
{
    // report.txt: the same data as report.json/html, laid out for reading in a text editor.
    internal static class ProfileReport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Build(ReportData r)
        {
            StringBuilder sb = new StringBuilder();
            Line(sb, "MWC FSM Profiler 2.0, {0}", r.Created.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            Line(sb, "Mode: {0}", r.Mode);
            Line(sb, "Frames: {0} over {1:F1} s", r.Frames, r.Seconds);
            Line(sb, "Frame time: avg {0:F2} ms ({1:F1} FPS), median {2:F2}, p95 {3:F2}, p99 {4:F2}, max {5:F1} ms, 1% low {6:F1} FPS",
                r.AvgMs, r.AvgMs > 0 ? 1000.0 / r.AvgMs : 0, r.P50, r.P95, r.P99, r.MaxMs, r.Low1Fps);
            Line(sb, "Spikes (> {0:F1}x recent median): {1}", Timeline.SpikeMultiplier, r.Spikes);
            Line(sb, "GC: {0} collections ({1:F1}/min), frames with a GC average {2:F1} ms", r.GcCount, r.Seconds > 0 ? r.GcCount / r.Seconds * 60 : 0, r.GcFrameAvgMs);
            Line(sb, "Allocations: {0:F1} KB/frame, heap {1:F0} -> {2:F0} MB", r.AllocKBPerFrame, r.HeapStartMB, r.HeapEndMB);
            Line(sb, "Probe overhead per timed call (subtracted in 'self' and 'incl'): FSM {0:F3} us, action {1:F3}, event {2:F3}, script {3:F3}, minimal {4:F3} ({5} actions, {6} scripts patched)",
                r.CategoryOverheadUs[Cat.Fsm], r.CategoryOverheadUs[Cat.Action], r.CategoryOverheadUs[Cat.Event], r.CategoryOverheadUs[Cat.Script], r.OverheadUs, r.PatchedActions, r.PatchedScripts);
            Line(sb, "Events processed: {0:F1}/frame, mouse-pick requests: {1:F1}/frame", r.EventsPerFrame, r.MousePicksPerFrame);
            if (r.StackRepairs > 0)
                Line(sb, "Note: {0} frames ended with an open timing stack (an exception skipped a postfix); nesting in those frames is approximate.", r.StackRepairs);
            if (r.TimelineOverflow > 0)
                Line(sb, "Note: recording longer than {0} frames, the last {1} are not in the timeline.", Timeline.Capacity, r.TimelineOverflow);
            foreach (string note in r.Notes)
                sb.AppendLine("Note: " + note);
            sb.AppendLine();

            sb.AppendLine("== Frame breakdown (ms per frame, self time, overhead removed) ==");
            for (int c = 0; c < Cat.Count; c++)
                Line(sb, "{0,-36}{1,8:F2}", Cat.Names[c], r.CategoryMs[c]);
            Line(sb, "{0,-36}{1,8:F2}   ({2:F2} fixed steps/frame, fixedDeltaTime {3:F1} ms)", "Physics simulation + collisions", r.PhysicsMs, r.FixedStepsPerFrame, r.FixedDeltaMs);
            Line(sb, "{0,-36}{1,8:F2}   (culling, shadows, draw submission, waiting for the render thread)", "Rendering (main thread)", r.RenderMs);
            Line(sb, "{0,-36}{1,8:F2}   (probes of top-level calls; absent without the profiler)", "Profiler overhead", r.ProbeOverheadMs);
            Line(sb, "{0,-36}{1,8:F2}   (animation, audio, particles, input, GC, frame pacing)", "Not timed", r.UntrackedMs);
            Line(sb, "{0,-36}{1,8:F2}", "Total frame", r.AvgMs);
            sb.AppendLine();

            // Frames with a collection are left out of the heap growth, so the parts can add up to a bit more.
            sb.AppendLine("== Where the allocations happen (KB per frame) ==");
            Line(sb, "{0,-44}{1,8:F2}", "All managed allocations (heap growth)", r.AllocKBPerFrame);
            Line(sb, "{0,-44}{1,8:F2}", "  inside timed calls (see allocator table)", r.AllocTimedKB);
            Line(sb, "{0,-44}{1,8:F2}", "  outside any timed call", Math.Max(0, r.AllocKBPerFrame - r.AllocTimedKB));
            Line(sb, "{0,-44}{1,8:F2}   (of which {2:F2} outside timed callbacks)", "Physics window (simulation + callbacks)", r.AllocPhysicsKB, r.AllocPhysicsUntimedKB);
            Line(sb, "{0,-44}{1,8:F2}   (of which {2:F2} outside timed callbacks)", "Render window (render callbacks, OnGUI)", r.AllocRenderKB, r.AllocRenderUntimedKB);
            if (r.BackgroundKBPerSecond >= 0)
                Line(sb, "{0,-44}{1,8:F2}   ({2:F1} KB/s while the main thread slept 250 ms at the start)", "Other threads (estimate)", r.AvgMs > 0 ? r.BackgroundKBPerSecond * r.AvgMs / 1000.0 : 0, r.BackgroundKBPerSecond);
            sb.AppendLine();

            if (r.GapRows.Count > 0)
            {
                sb.AppendLine("== Allocations between timed calls (where untimed code allocates) ==");
                sb.AppendLine("KB/frame  between");
                for (int i = 0; i < r.GapRows.Count && i < 25; i++)
                    Line(sb, "{0,7:F2}  {1}  ->  {2}", r.GapRows[i].KB, r.GapRows[i].Name, r.GapRows[i].Detail);
                sb.AppendLine();
            }

            if (r.PropertyRows.Count > 0)
            {
                sb.AppendLine("== GetProperty / SetProperty by member (KB/frame, calls/frame, self ms, PlayMakerTurbo path) ==");
                for (int i = 0; i < r.PropertyRows.Count && i < 40; i++)
                {
                    Row p = r.PropertyRows[i];
                    Line(sb, "{0,6:F2}  {1,6:F1}  {2,7:F3}  {3}  |  {4}", p.KB, p.Calls, p.CorrSelf, p.Name, p.Detail);
                }
                sb.AppendLine();
            }

            if (r.UntimedScripts.Count > 0)
            {
                sb.AppendLine("== Active scripts with Update/LateUpdate/FixedUpdate that are NOT timed (instances, method) ==");
                foreach (string s in r.UntimedScripts)
                    sb.AppendLine(s);
                sb.AppendLine();
            }

            Table(sb, "Top 60 FSMs by inclusive time (with their most expensive states)", r.Fsms, 60, 3);
            Table(sb, "Top 60 action instances (which FSM / state / action index)", r.ActionInstances, 60, 0);
            Table(sb, "Top 40 action types (with the 5 instances that cost the most)", r.ActionTypes, 40, 5);
            Table(sb, "Top 30 events (ProcessEvent self time = transition lookup; senders/receivers)", r.Events, 30, 10);
            Table(sb, "Top 50 MonoBehaviour scripts", r.Scripts, 50, 0);
            Table(sb, "MSCLoader mods", r.Mods, 50, 0);
            Table(sb, "Top 30 FSM names (same logic on many objects, detail = count)", r.ByFsmName, 30, 0);
            Table(sb, "Top 30 root objects (map area / vehicle, detail = FSM count)", r.ByRoot, 30, 0);

            List<Row> allocators = new List<Row>();
            allocators.AddRange(r.Scripts);
            allocators.AddRange(r.ActionInstances);
            allocators.AddRange(r.Mods);
            allocators.Sort((a, b) => b.SelfKB.CompareTo(a.SelfKB));
            Table(sb, "Top 30 allocators by self KB/frame (coarse: Boehm GC heap growth)", allocators, 30, 0);

            if (r.WasteRows.Count > 0)
            {
                sb.AppendLine("== Writes that changed nothing (everyFrame actions writing the value the target already has) ==");
                sb.AppendLine("same/f   same %   self ms  action | where");
                for (int i = 0; i < r.WasteRows.Count && i < 40; i++)
                {
                    Row w = r.WasteRows[i];
                    Line(sb, "{0,6:F1}  {1,6:F1}  {2,8:F3}  {3} | {4}", w.Calls, w.SameShare * 100, w.CorrSelf, w.Name, w.Detail);
                }
                sb.AppendLine();
            }

            Table(sb, "FSMs that cost the most in Update and why they are not idle (state at the end of the recording)", r.IdleRows, 40, 0);

            if (!string.IsNullOrEmpty(r.BenchmarkText))
            {
                sb.AppendLine(r.BenchmarkText);
                sb.AppendLine();
            }

            if (r.SpikeList.Count > 0)
            {
                sb.AppendLine("== Spike frames (worst first) ==");
                foreach (SpikeSummary s in r.SpikeList)
                {
                    Line(sb, "frame {0}: {1:F1} ms (median {2:F1}){3}, timed {4:F1} ms -> {5}{6}",
                        s.Frame, s.Ms, s.MedianMs, s.Gc ? ", GC" : "", s.CoveredMs, s.Cause, s.File != null ? "   [" + s.File + "]" : "");
                    foreach (Row t in s.Top)
                        Line(sb, "      {0,8:F2} ms  {1}", t.Self, t.Name);
                }
                sb.AppendLine();
            }

            sb.AppendLine(r.TurboText);
            sb.AppendLine();
            sb.AppendLine(r.RenderText);
            return sb.ToString();
        }

        private static void Table(StringBuilder sb, string title, List<Row> rows, int top, int children)
        {
            if (rows.Count == 0)
                return;
            sb.AppendLine("== " + title + " ==");
            sb.AppendLine("self ms   incl ms  calls/f   us/call   max ms  KB/f  name | detail");
            for (int i = 0; i < rows.Count && i < top; i++)
            {
                RowLine(sb, rows[i], "");
                if (children > 0 && rows[i].Children != null)
                {
                    for (int j = 0; j < rows[i].Children.Count && j < children; j++)
                        RowLine(sb, rows[i].Children[j], "    ");
                }
            }
            sb.AppendLine();
        }

        private static void RowLine(StringBuilder sb, Row r, string indent)
        {
            sb.AppendLine(string.Format(Inv, "{0,7:F3}  {1,7:F3}  {2,7:F1}  {3,8:F2}  {4,7:F2}  {5,4:F1}  {6}{7}{8}",
                r.CorrSelf, r.Incl, r.Calls, r.UsPerCall, r.MaxMs, r.KB, indent, r.Name,
                string.IsNullOrEmpty(r.Detail) ? "" : " | " + r.Detail));
        }

        private static void Line(StringBuilder sb, string format, params object[] args)
        {
            sb.AppendLine(string.Format(Inv, format, args));
        }
    }
}
