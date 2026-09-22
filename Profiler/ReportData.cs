using System;
using System.Collections.Generic;
using System.Diagnostics;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MWCFsmProfiler
{
    // One line of a table. Times are ms per frame unless the name says otherwise.
    internal class Row
    {
        public string Name;
        public string Detail = "";
        public double Calls;      // per frame
        public double Incl;
        public double Self;
        public double CorrSelf;   // self minus measured probe overhead
        public double KB;         // inclusive KB per frame
        public double SelfKB;
        public double MaxMs;      // slowest single call
        public double UsPerCall;
        public long Checked;      // waste detector: writes checked
        public double SameShare;  // waste detector: share of checked writes that changed nothing
        public List<Row> Children;
    }

    internal class SpikeSummary
    {
        public int Frame;
        public float Ms;
        public float MedianMs;
        public bool Gc;
        public double CoveredMs;   // time inside timed calls and engine phases
        public List<Row> Top = new List<Row>();
        public string Cause;
        public int Dropped;
        public string File;
    }

    // Everything a report shows, computed once when a recording stops. The text, JSON and HTML outputs only
    // format this, so they cannot disagree with each other.
    internal class ReportData
    {
        public string Mode;
        public DateTime Created = DateTime.Now;
        public int Frames;
        public double Seconds;
        public double AvgMs, P50, P95, P99, MaxMs, Low1Fps;
        public int Spikes;
        public int GcCount;
        public double GcFrameAvgMs;
        public double AllocKBPerFrame;
        public double AllocTimedKB, AllocPhysicsKB, AllocPhysicsUntimedKB, AllocRenderKB, AllocRenderUntimedKB;
        public double HeapStartMB, HeapEndMB;
        public double OverheadUs;
        public int StackRepairs;
        public int TimelineOverflow;
        public int PatchedActions, PatchedScripts;
        public double FixedStepsPerFrame, FixedDeltaMs;
        public double MousePicksPerFrame, EventsPerFrame;

        public double[] CategoryMs = new double[Cat.Count];
        public double PhysicsMs, RenderMs, UntrackedMs;

        public List<Row> Fsms = new List<Row>();
        public List<Row> ActionInstances = new List<Row>();
        public List<Row> ActionTypes = new List<Row>();
        public List<Row> Events = new List<Row>();
        public List<Row> Scripts = new List<Row>();
        public List<Row> Mods = new List<Row>();
        public List<Row> ByFsmName = new List<Row>();
        public List<Row> ByRoot = new List<Row>();
        public List<SpikeSummary> SpikeList = new List<SpikeSummary>();
        public List<Row> WasteRows = new List<Row>();
        public List<Row> IdleRows = new List<Row>();
        public List<Row> GapRows = new List<Row>();
        public double BackgroundKBPerSecond = -1;
        public List<string> UntimedScripts = new List<string>();
        public List<Row> PropertyRows = new List<Row>();
        public Action<JsonWriter> Benchmark;     // filled by a benchmark run, written into report.json
        public string BenchmarkText;             // the same results for report.txt

        public string TurboText = "";
        public string RenderText = "";
        public List<string> Notes = new List<string>();

        private double tickToMs;
        private double perFrame;

        public static ReportData Build(string mode, long heapStart)
        {
            ReportData r = new ReportData { Mode = mode };
            r.tickToMs = 1000.0 / Stopwatch.Frequency;
            r.Frames = Math.Max(1, Timeline.Count);
            r.perFrame = 1.0 / r.Frames;
            r.OverheadUs = Calibration.OverheadMicroseconds;
            r.StackRepairs = Probe.StackRepairs;
            r.TimelineOverflow = Timeline.Overflow;
            r.PatchedActions = ActionTimings.PatchedMethods;
            r.PatchedScripts = ScriptTimings.PatchedMethods;
            r.HeapStartMB = heapStart / 1048576.0;
            r.HeapEndMB = GC.GetTotalMemory(false) / 1048576.0;
            r.FixedStepsPerFrame = FramePhases.FixedSteps * r.perFrame;
            r.FixedDeltaMs = FramePhases.FixedDeltaTime * 1000.0;
            r.MousePicksPerFrame = ActionTimings.MousePicks * r.perFrame;
            r.EventsPerFrame = ActionTimings.ProcessedEvents * r.perFrame;

            r.BuildFrameStats();
            r.BuildFsms();
            r.BuildActions();
            r.BuildEvents();
            r.BuildScripts();
            r.BuildSpikes();
            r.BuildWaste();
            r.BuildIdle();
            r.BuildGaps();
            r.PropertyRows = PropertyCoverage.Build(r.perFrame, r.tickToMs);
            return r;
        }

        private Row MakeRow(string name, Stat s)
        {
            return new Row
            {
                Name = name,
                Calls = s.Calls * perFrame,
                Incl = s.CorrectedTicks * tickToMs * perFrame,
                Self = s.Self * tickToMs * perFrame,
                CorrSelf = s.CorrectedSelf * tickToMs * perFrame,
                KB = s.Bytes / 1024.0 * perFrame,
                SelfKB = s.SelfBytes / 1024.0 * perFrame,
                MaxMs = s.MaxTicks * tickToMs,
                UsPerCall = s.Calls > 0 ? s.Ticks * tickToMs * 1000.0 / s.Calls : 0,
            };
        }

        private static void Accumulate(Row into, Row from)
        {
            into.Calls += from.Calls;
            into.Incl += from.Incl;
            into.Self += from.Self;
            into.CorrSelf += from.CorrSelf;
            into.KB += from.KB;
            into.SelfKB += from.SelfKB;
            if (from.MaxMs > into.MaxMs)
                into.MaxMs = from.MaxMs;
        }

        public static void SortBySelf(List<Row> rows)
        {
            rows.Sort((a, b) => b.CorrSelf.CompareTo(a.CorrSelf));
        }

        private void BuildFrameStats()
        {
            int n = Timeline.Count;
            if (n == 0)
                return;

            float[] ms = new float[n];
            double sum = 0, alloc = 0, gcMs = 0, timedKB = 0, physKB = 0, physUn = 0, rendKB = 0, rendUn = 0;
            double[] cat = new double[Cat.Count];
            double physics = 0, render = 0;
            for (int i = 0; i < n; i++)
            {
                FrameSample f = Timeline.Frames[i];
                ms[i] = f.Ms;
                sum += f.Ms;
                alloc += f.AllocKB;
                timedKB += f.AllocTimedKB;
                physKB += f.AllocPhysicsKB;
                physUn += f.AllocPhysicsUntimedKB;
                rendKB += f.AllocRenderKB;
                rendUn += f.AllocRenderUntimedKB;
                physics += f.Physics;
                render += f.Render;
                cat[Cat.Fsm] += f.Fsm;
                cat[Cat.Action] += f.Action;
                cat[Cat.Event] += f.Event;
                cat[Cat.Script] += f.Script;
                cat[Cat.Mod] += f.Mod;
                if (f.Gc)
                {
                    GcCount++;
                    gcMs += f.Ms;
                }
            }
            Seconds = sum / 1000.0;
            AvgMs = sum / n;
            AllocKBPerFrame = alloc / n;
            AllocTimedKB = timedKB / n;
            AllocPhysicsKB = physKB / n;
            AllocPhysicsUntimedKB = physUn / n;
            AllocRenderKB = rendKB / n;
            AllocRenderUntimedKB = rendUn / n;
            GcFrameAvgMs = GcCount > 0 ? gcMs / GcCount : 0;
            Spikes = Timeline.SpikeCount;

            Array.Sort(ms, FloatComparer.Instance);
            P50 = Stats.Percentile(ms, 0.50);
            P95 = Stats.Percentile(ms, 0.95);
            P99 = Stats.Percentile(ms, 0.99);
            MaxMs = ms[n - 1];
            Low1Fps = Stats.LowFps(ms, 0.01);

            double tracked = 0;
            for (int c = 0; c < Cat.Count; c++)
            {
                CategoryMs[c] = cat[c] / n;
                tracked += CategoryMs[c];
            }
            // Probe overhead sits in the callers' self time; take it back out of the categories in proportion.
            double overheadMs = Calibration.OverheadTicks * tickToMs * TotalCalls() / n;
            if (tracked > 0 && overheadMs > 0)
            {
                double scale = Math.Max(0, tracked - overheadMs) / tracked;
                for (int c = 0; c < Cat.Count; c++)
                    CategoryMs[c] *= scale;
                tracked *= scale;
            }
            PhysicsMs = physics / n;
            RenderMs = render / n;
            UntrackedMs = AvgMs - tracked - PhysicsMs - RenderMs;
        }

        private static long TotalCalls()
        {
            long calls = 0;
            foreach (FsmEntry e in FsmTimings.Entries.Values)
                calls += e.Total.Calls;
            foreach (ActionEntry a in ActionTimings.Entries.Values)
                calls += a.Frame.Calls + a.Enter.Calls;
            foreach (EventEntry e in EventFlow.Entries.Values)
                calls += e.Stat.Calls;
            foreach (Dictionary<Type, Stat> d in ScriptTimings.Stats)
                foreach (Stat s in d.Values)
                    calls += s.Calls;
            foreach (Stat s in ScriptTimings.ModStats.Values)
                calls += s.Calls;
            return calls;
        }

        private void BuildFsms()
        {
            Dictionary<string, Row> byName = new Dictionary<string, Row>();
            Dictionary<string, Row> byRoot = new Dictionary<string, Row>();
            foreach (FsmEntry e in FsmTimings.Entries.Values)
            {
                Row row = MakeRow(e.FsmName, e.Total);
                row.Detail = e.Path;
                row.Children = new List<Row>();
                foreach (StateEntry s in e.States.Values)
                    row.Children.Add(MakeRow(s.Name, s.Stat));
                SortBySelf(row.Children);
                Fsms.Add(row);
                Group(byName, e.FsmName, row);
                Group(byRoot, e.Root, row);
            }
            // An FSM's own self time is only PlayMaker's bookkeeping; its actions do the work. Inclusive time is
            // what the FSM costs, so FSMs are ranked by it (states inside stay ranked by self).
            Fsms.Sort((a, b) => b.Incl.CompareTo(a.Incl));
            ByFsmName = new List<Row>(byName.Values);
            ByRoot = new List<Row>(byRoot.Values);
            SortBySelf(ByFsmName);
            SortBySelf(ByRoot);
        }

        private static void Group(Dictionary<string, Row> groups, string key, Row row)
        {
            Row g;
            if (!groups.TryGetValue(key, out g))
            {
                g = new Row { Name = key, Detail = "0" };
                groups.Add(key, g);
            }
            Accumulate(g, row);
            g.Detail = (int.Parse(g.Detail) + 1).ToString();
        }

        private void BuildActions()
        {
            Dictionary<Type, Row> types = new Dictionary<Type, Row>();
            Dictionary<Type, List<Row>> callers = new Dictionary<Type, List<Row>>();
            foreach (ActionEntry a in ActionTimings.Entries.Values)
            {
                Row frame = MakeRow(a.Type.Name, a.Frame);
                Row enter = MakeRow(a.Type.Name, a.Enter);
                Row row = new Row { Name = a.Type.Name, Detail = a.Where };
                Accumulate(row, frame);
                Accumulate(row, enter);
                row.UsPerCall = frame.UsPerCall;
                row.Children = new List<Row> { Tag(frame, "per-frame callbacks"), Tag(enter, "OnEnter/OnExit") };
                if (a.WasteChecked > 0)
                    row.Children.Add(new Row { Name = "unchanged writes", Detail = $"{a.WasteSame}/{a.WasteChecked}", Calls = a.WasteSame * perFrame });
                ActionInstances.Add(row);

                Row t;
                if (!types.TryGetValue(a.Type, out t))
                {
                    t = new Row { Name = a.Type.Name, Detail = "0", Children = new List<Row>() };
                    types.Add(a.Type, t);
                    callers.Add(a.Type, new List<Row>());
                }
                Accumulate(t, row);
                t.Detail = (int.Parse(t.Detail) + 1).ToString();
                callers[a.Type].Add(new Row { Name = a.Where, Calls = row.Calls, Incl = row.Incl, Self = row.Self, CorrSelf = row.CorrSelf, KB = row.KB, MaxMs = row.MaxMs, UsPerCall = row.UsPerCall });
            }
            SortBySelf(ActionInstances);

            foreach (KeyValuePair<Type, Row> pair in types)
            {
                List<Row> list = callers[pair.Key];
                SortBySelf(list);
                pair.Value.Children = list.Count > 5 ? list.GetRange(0, 5) : list;
                pair.Value.UsPerCall = pair.Value.Calls > 0 ? pair.Value.Incl * 1000.0 / pair.Value.Calls : 0;
                ActionTypes.Add(pair.Value);
            }
            SortBySelf(ActionTypes);
        }

        // Why the FSMs that cost the most in Update were not skipped as idle: what their active state still runs
        // (unfinished actions) or waits for (delayed events). Read at the end of the recording, so it describes the
        // state they are in now.
        private void BuildIdle()
        {
            List<FsmEntry> list = new List<FsmEntry>(FsmTimings.Entries.Values);
            list.Sort((a, b) => b.Slots[FsmTimings.SlotUpdate].CorrectedTicks.CompareTo(a.Slots[FsmTimings.SlotUpdate].CorrectedTicks));
            for (int i = 0; i < list.Count && IdleRows.Count < 40; i++)
            {
                FsmEntry e = list[i];
                PlayMakerFSM component = e.Owner as PlayMakerFSM;
                if (component == null || component.Fsm == null)
                    continue;
                FsmState state = component.Fsm.ActiveState;
                List<string> running = new List<string>();
                if (state != null && state.Actions != null)
                {
                    foreach (FsmStateAction a in state.Actions)
                    {
                        if (a != null && a.Enabled && !a.Finished)
                            running.Add(a.GetType().Name);
                    }
                }
                int delayed = component.Fsm.DelayedEvents != null ? component.Fsm.DelayedEvents.Count : 0;
                string why = running.Count > 0 ? "runs " + string.Join(", ", running.ToArray()) : "all actions finished";
                if (delayed > 0)
                    why += $"; {delayed} delayed event(s) pending";
                Row row = MakeRow(e.FsmName, e.Slots[FsmTimings.SlotUpdate]);
                row.Detail = $"{e.Path} | state {(state != null ? state.Name : "-")}: {why}";
                IdleRows.Add(row);
            }
        }

        private void BuildGaps()
        {
            BackgroundKBPerSecond = Probe.BackgroundKBPerSecond;
            if (ScriptTimings.PatchedMethods > 0)
                UntimedScripts = ScriptTimings.UntimedActiveScripts();
            foreach (KeyValuePair<long, long> g in Probe.Gaps)
            {
                GapRows.Add(new Row
                {
                    Name = "after " + NameOf(Probe.GapFrom(g.Key)),
                    Detail = "before " + NameOf(Probe.GapTo(g.Key)),
                    KB = g.Value / 1024.0 * perFrame,
                });
            }
            GapRows.Sort((a, b) => b.KB.CompareTo(a.KB));
        }

        private static string NameOf(int id)
        {
            return id >= 0 && id < Names.List.Count ? Names.List[id] : "[start of recording]";
        }

        // Action instances with checked writes, most unchanged writes first.
        private void BuildWaste()
        {
            foreach (ActionEntry a in ActionTimings.Entries.Values)
            {
                if (a.WasteChecked == 0)
                    continue;
                WasteRows.Add(new Row
                {
                    Name = a.Type.Name,
                    Detail = a.Where,
                    Calls = a.WasteSame * perFrame,
                    CorrSelf = a.Frame.CorrectedSelf * tickToMs * perFrame,
                    Checked = a.WasteChecked,
                    SameShare = (double)a.WasteSame / a.WasteChecked,
                });
            }
            WasteRows.Sort((a, b) => b.Calls.CompareTo(a.Calls));
        }

        private static Row Tag(Row row, string name)
        {
            row.Name = name;
            return row;
        }

        private void BuildEvents()
        {
            foreach (EventEntry e in EventFlow.Entries.Values)
            {
                Row row = MakeRow(e.Name, e.Stat);
                row.Detail = $"from itself {e.FromSelf}, from scripts/global {e.FromOutside}";
                row.Children = new List<Row>();
                foreach (KeyValuePair<Fsm, int> s in Top(e.Senders, 5))
                    row.Children.Add(new Row { Name = "sent by " + EventFlow.Describe(s.Key), Calls = s.Value * perFrame });
                foreach (KeyValuePair<Fsm, int> s in Top(e.Receivers, 5))
                    row.Children.Add(new Row { Name = "received by " + EventFlow.Describe(s.Key), Calls = s.Value * perFrame });
                Events.Add(row);
            }
            SortBySelf(Events);
        }

        private static List<KeyValuePair<Fsm, int>> Top(Dictionary<Fsm, int> map, int count)
        {
            List<KeyValuePair<Fsm, int>> list = new List<KeyValuePair<Fsm, int>>(map);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list.Count > count ? list.GetRange(0, count) : list;
        }

        private void BuildScripts()
        {
            for (int k = 0; k < ScriptTimings.KindNames.Length; k++)
            {
                foreach (KeyValuePair<Type, Stat> pair in ScriptTimings.Stats[k])
                {
                    // Coroutines and engine methods carry a readable name of their own; the iterator type name is not.
                    string name = k == ScriptTimings.KindCoroutine || pair.Key.Assembly == typeof(MonoBehaviour).Assembly
                        ? Names.List[pair.Value.NameId]
                        : pair.Key.FullName + "." + ScriptTimings.KindNames[k];
                    Row row = MakeRow(name, pair.Value);
                    row.Detail = pair.Key.Assembly.GetName().Name;
                    Scripts.Add(row);
                }
            }
            SortBySelf(Scripts);
            foreach (KeyValuePair<string, Stat> pair in ScriptTimings.ModStats)
                Mods.Add(MakeRow(pair.Key, pair.Value));
            SortBySelf(Mods);
        }

        // Self time per span needs the nesting, which the spans only carry implicitly: a span is inside another
        // when it starts after and ends before it. Sorting by start (longer first on ties) turns that into a stack.
        private void BuildSpikes()
        {
            List<SpikeFrame> spikes = new List<SpikeFrame>(SpanRecorder.Spikes);
            spikes.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            foreach (SpikeFrame spike in spikes)
            {
                SpikeSummary summary = new SpikeSummary
                {
                    Frame = spike.Frame,
                    Ms = spike.Ms,
                    MedianMs = spike.MedianMs,
                    Gc = spike.Gc,
                    Dropped = spike.Dropped,
                };

                Span[] spans = (Span[])spike.Spans.Clone();
                Array.Sort(spans, (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Duration.CompareTo(a.Duration));
                Dictionary<int, double> self = new Dictionary<int, double>();
                long[] stackEnd = new long[512];
                int[] stackIdx = new int[512];
                double[] selfTicks = new double[spans.Length];
                int top = 0;
                long covered = 0;
                for (int i = 0; i < spans.Length; i++)
                {
                    long start = spans[i].Start;
                    long end = start + spans[i].Duration;
                    while (top > 0 && start >= stackEnd[top - 1])
                        top--;
                    if (top > 0)
                        selfTicks[stackIdx[top - 1]] -= spans[i].Duration;
                    else
                        covered += spans[i].Duration;
                    selfTicks[i] += spans[i].Duration;
                    if (top < stackEnd.Length)
                    {
                        stackEnd[top] = end;
                        stackIdx[top] = i;
                        top++;
                    }
                }
                for (int i = 0; i < spans.Length; i++)
                {
                    double v;
                    self.TryGetValue(spans[i].Name, out v);
                    self[spans[i].Name] = v + selfTicks[i];
                }

                summary.CoveredMs = covered * tickToMs;
                List<KeyValuePair<int, double>> ranked = new List<KeyValuePair<int, double>>(self);
                ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < ranked.Count && i < 8; i++)
                {
                    string name = ranked[i].Key >= 0 && ranked[i].Key < Names.List.Count ? Names.List[ranked[i].Key] : "?";
                    summary.Top.Add(new Row { Name = name, Self = ranked[i].Value * tickToMs });
                }

                double extra = spike.Ms - spike.MedianMs;
                double untracked = spike.Ms - summary.CoveredMs;
                // A collection runs inside whatever call allocated when the heap filled up, so its pause shows up
                // as that call's self time. The call only triggered it; the allocation rate of the whole game did.
                if (spike.Gc && summary.Top.Count > 0 && summary.Top[0].Self > extra * 0.5)
                    summary.Cause = $"garbage collection pause, triggered inside {summary.Top[0].Name} (it only happened to allocate when the heap was full)";
                else if (spike.Gc && untracked > extra * 0.5)
                    summary.Cause = $"garbage collection (about {untracked:F0} ms outside any timed code)";
                else if (summary.Top.Count > 0 && summary.Top[0].Self > extra * 0.5)
                    summary.Cause = summary.Top[0].Name;
                else if (untracked > extra * 0.5)
                    summary.Cause = $"engine work outside timed code ({untracked:F0} ms: loading, streaming, animation, audio...)";
                else
                    summary.Cause = "spread over many calls";
                SpikeList.Add(summary);
            }
        }
    }
}
