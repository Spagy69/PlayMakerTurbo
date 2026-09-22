using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MWCFsmProfiler
{
    internal class Block
    {
        public string Option;
        public bool On;
        public int Index;
        public double StartS;
        public int Frames;
        public double MeanMs, MedianMs, P99, Low1Fps;
        public int GcFrames;
        public double GcAvgMs;
    }

    internal class OptionResult
    {
        public string Option;
        public double OffMs, OnMs;
        public Interval Gain;          // ms per frame, positive = option makes frames shorter
        public double GainPct;
        public Interval P99Gain;
        public Interval MedianGain;
        public double Low1Off, Low1On;
        public int GcOff, GcOn;
        public int Pairs;
        public string Verdict;
    }

    // Runs the A/B benchmark: each option is switched off and on in blocks (ABBA BAAB ...), so slow drift
    // (GPU clocks, heap growth) cancels out within every quartet. The first 0.5 s after each switch is thrown away:
    // in that frame part of the FSMs ran with the old value, and caches are refilling.
    internal static class AbRunner
    {
        public enum Kind { AB, AA, Baseline }
        private enum Phase { Warmup, Settle, Measure }

        public const string AAName = "A/A (switches nothing)";
        public static bool Active;
        public static bool Finished;
        public static Kind RunKind;

        public static float BlockSeconds = 10f;
        public static float SettleSeconds = 0.5f;
        public static float WarmupSeconds = 5f;
        public static int MinPairs = 8;
        public static int MaxPairs = 20;
        public static int BaselineBlocks = 20;
        public static double TargetHalfWidthMs = 0.05;
        public const double NegligibleMs = 0.02;

        public static readonly List<Block> Blocks = new List<Block>();
        public static readonly List<OptionResult> Results = new List<OptionResult>();
        public static string SessionText;
        public static Action<JsonWriter> SessionJson;

        private static readonly List<string> queue = new List<string>();
        private static readonly Dictionary<string, bool> original = new Dictionary<string, bool>();
        private static readonly List<float> frames = new List<float>(8192);
        private static int optionIndex;
        private static int blockInOption;
        private static Phase phase;
        private static double phaseMs;
        private static double elapsedMs;
        private static int gcFrames;
        private static double gcMs;
        private static bool currentOn;

        public static int OptionCount => queue.Count;
        public static string CurrentOption => optionIndex < queue.Count ? queue[optionIndex] : "";

        public static void Start(Kind kind, List<string> options)
        {
            RunKind = kind;
            Blocks.Clear();
            Results.Clear();
            queue.Clear();
            original.Clear();
            SessionText = null;
            SessionJson = null;
            if (kind == Kind.AB)
                queue.AddRange(options);
            else
                queue.Add(kind == Kind.AA ? AAName : "Baseline (current settings)");
            foreach (string o in TurboCounters.OptionNames())
                original[o] = TurboCounters.GetOption(o);

            optionIndex = 0;
            blockInOption = 0;
            elapsedMs = 0;
            phase = Phase.Warmup;
            phaseMs = 0;
            Finished = false;
            Active = true;
        }

        public static void Abort()
        {
            RestoreOptions();
            Active = false;
        }

        private static void RestoreOptions()
        {
            foreach (KeyValuePair<string, bool> o in original)
                TurboCounters.SetOption(o.Key, o.Value);
        }

        // Called from the frame boundary with the length of the frame that just ended.
        public static void OnFrame(float ms, bool gc)
        {
            if (!Active)
                return;
            elapsedMs += ms;
            phaseMs += ms;

            switch (phase)
            {
                case Phase.Warmup:
                    if (phaseMs >= WarmupSeconds * 1000f)
                        BeginBlock();
                    break;
                case Phase.Settle:
                    if (phaseMs >= SettleSeconds * 1000f)
                    {
                        phase = Phase.Measure;
                        phaseMs = 0;
                    }
                    break;
                case Phase.Measure:
                    if (gc)
                    {
                        gcFrames++;
                        gcMs += ms;
                    }
                    else
                    {
                        frames.Add(ms);
                    }
                    if (phaseMs >= BlockSeconds * 1000f)
                        EndBlock();
                    break;
            }
        }

        // ABBA for even quartets, BAAB for odd ones.
        private static bool ArmOn(int block)
        {
            int quartet = block / 4, pos = block % 4;
            bool abba = quartet % 2 == 0;
            bool first = pos == 0 || pos == 3;
            return abba ? !first : first;
        }

        private static void BeginBlock()
        {
            string option = queue[optionIndex];
            currentOn = RunKind == Kind.Baseline || ArmOn(blockInOption);
            if (RunKind == Kind.AB)
                TurboCounters.SetOption(option, currentOn);
            frames.Clear();
            gcFrames = 0;
            gcMs = 0;
            phase = Phase.Settle;
            phaseMs = 0;
        }

        private static void EndBlock()
        {
            string option = queue[optionIndex];
            float[] sorted = frames.ToArray();
            Array.Sort(sorted, FloatComparer.Instance);
            double sum = 0;
            foreach (float f in sorted)
                sum += f;
            Blocks.Add(new Block
            {
                Option = option,
                On = currentOn,
                Index = blockInOption,
                StartS = elapsedMs / 1000.0,
                Frames = sorted.Length,
                MeanMs = sorted.Length > 0 ? sum / sorted.Length : 0,
                MedianMs = Stats.Percentile(sorted, 0.5),
                P99 = Stats.Percentile(sorted, 0.99),
                Low1Fps = Stats.LowFps(sorted, 0.01),
                GcFrames = gcFrames,
                GcAvgMs = gcFrames > 0 ? gcMs / gcFrames : 0,
            });
            blockInOption++;

            if (OptionDone(option))
            {
                if (RunKind == Kind.AB)
                    TurboCounters.SetOption(option, original.ContainsKey(option) && original[option]);
                optionIndex++;
                blockInOption = 0;
                if (optionIndex >= queue.Count)
                {
                    Finish();
                    return;
                }
            }
            BeginBlock();
        }

        private static bool OptionDone(string option)
        {
            if (RunKind == Kind.Baseline)
                return blockInOption >= BaselineBlocks;
            if (blockInOption % 4 != 0)
                return false;
            int pairs = blockInOption / 2;
            if (pairs >= MaxPairs)
                return true;
            if (pairs < MinPairs)
                return false;
            Interval i = Stats.Paired(PairDifferences(option, b => b.MeanMs));
            return (i.High - i.Low) / 2 <= TargetHalfWidthMs;
        }

        // on minus off for each adjacent pair of blocks (0,1), (2,3), ...
        private static List<double> PairDifferences(string option, Func<Block, double> value)
        {
            List<Block> list = Blocks.FindAll(b => b.Option == option);
            List<double> d = new List<double>();
            for (int i = 0; i + 1 < list.Count; i += 2)
            {
                Block a = list[i].On ? list[i + 1] : list[i];
                Block b = list[i].On ? list[i] : list[i + 1];
                d.Add(value(b) - value(a));
            }
            return d;
        }

        private static void Finish()
        {
            Active = false;
            Finished = true;
            RestoreOptions();
            if (RunKind == Kind.Baseline)
                return;

            foreach (string option in queue)
            {
                List<Block> list = Blocks.FindAll(b => b.Option == option);
                OptionResult r = new OptionResult { Option = option, Pairs = list.Count / 2 };
                r.OffMs = Stats.Mean(list.FindAll(b => !b.On).ConvertAll(b => b.MeanMs));
                r.OnMs = Stats.Mean(list.FindAll(b => b.On).ConvertAll(b => b.MeanMs));
                r.Low1Off = Stats.Mean(list.FindAll(b => !b.On).ConvertAll(b => b.Low1Fps));
                r.Low1On = Stats.Mean(list.FindAll(b => b.On).ConvertAll(b => b.Low1Fps));
                r.GcOff = list.FindAll(b => !b.On).ConvertAll(b => b.GcFrames).Sum();
                r.GcOn = list.FindAll(b => b.On).ConvertAll(b => b.GcFrames).Sum();

                // Differences are on - off; a gain is the negative of that.
                r.Gain = Negate(Stats.Paired(PairDifferences(option, b => b.MeanMs)));
                r.GainPct = r.OffMs > 0 ? r.Gain.Mean / r.OffMs * 100.0 : 0;
                r.P99Gain = Negate(Stats.Bootstrap(PairDifferences(option, b => b.P99), Stats.Mean));
                r.MedianGain = Negate(Stats.Bootstrap(PairDifferences(option, b => b.MedianMs), Stats.Mean));
                r.Verdict = Verdict(r.Gain, r.Pairs);
                Results.Add(r);
            }
        }

        private static int Sum(this List<int> list)
        {
            int s = 0;
            foreach (int i in list)
                s += i;
            return s;
        }

        private static Interval Negate(Interval i)
        {
            Interval r = i;
            r.Mean = -i.Mean;
            r.Low = -i.High;
            r.High = -i.Low;
            return r;
        }

        public static string Verdict(Interval gain, int pairs)
        {
            if (pairs < MinPairs)
                return "too little data";
            if (gain.Low > 0)
                return gain.Mean < NegligibleMs ? "provable but negligible gain" : "provable gain";
            if (gain.High < 0)
                return "worse";
            return "in noise";
        }

        // ---- two sessions (Turbo vs original needs a restart in between) ----

        // Compares this baseline with the previous one saved in the settings folder, then keeps this one.
        public static void CompareSessions(string folder, string conditions)
        {
            string current = Path.Combine(folder, "benchmark_session.txt");
            string previous = Path.Combine(folder, "benchmark_session_previous.txt");
            List<double> now = Blocks.ConvertAll(b => b.MeanMs);
            CultureInfo inv = CultureInfo.InvariantCulture;

            StringBuilder sb = new StringBuilder("== Session comparison (this baseline vs the previous one) ==\n");
            if (File.Exists(current))
            {
                string[] lines = File.ReadAllLines(current);
                string prevConditions = "", prevCreated = "";
                List<double> before = new List<double>();
                foreach (string line in lines)
                {
                    if (line.StartsWith("created="))
                        prevCreated = line.Substring(8);
                    else if (line.StartsWith("conditions="))
                        prevConditions = line.Substring(11);
                    else if (line.StartsWith("blocks="))
                        foreach (string v in line.Substring(7).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            before.Add(double.Parse(v, inv));
                }

                // Welch: independent samples, no pairing across a restart. Welch(a, b) is mean(b) - mean(a), so
                // Welch(now, before) = previous - this = how much faster this session is.
                Interval gain = Stats.Welch(now, before);
                string verdict = Verdict(gain, Math.Min(now.Count, before.Count) >= MinPairs ? MinPairs : 0);
                sb.AppendLine(string.Format(inv, "previous session {0}: {1:F3} ms/frame ({2} blocks)", prevCreated, Stats.Mean(before), before.Count));
                sb.AppendLine(string.Format(inv, "this session:          {0:F3} ms/frame ({1} blocks)", Stats.Mean(now), now.Count));
                sb.AppendLine(string.Format(inv, "this session is faster by {0:F3} ms/frame (95% {1:F3} .. {2:F3}), {3}", gain.Mean, gain.Low, gain.High, verdict));
                sb.AppendLine(string.Format(inv, "in FPS: {0:F1} -> {1:F1}", 1000.0 / Stats.Mean(before), 1000.0 / Stats.Mean(now)));
                if (prevConditions != conditions)
                    sb.AppendLine("WARNING: conditions differ between the sessions:\n  previous: " + prevConditions + "\n  this:     " + conditions);

                double prevMean = Stats.Mean(before), nowMean = Stats.Mean(now);
                SessionJson = j => j.BeginObject().Prop("previousCreated", prevCreated).Prop("previousMs", prevMean).Prop("thisMs", nowMean)
                    .Prop("gainMs", gain.Mean).Prop("gainLow", gain.Low).Prop("gainHigh", gain.High).Prop("verdict", verdict)
                    .Prop("sameConditions", prevConditions == conditions).Prop("previousConditions", prevConditions).Prop("thisConditions", conditions).EndObject();
                File.Copy(current, previous, true);
            }
            else
            {
                sb.AppendLine("No previous session yet. Restart (e.g. with the original PlayMaker restored), stand on the same spot and run the baseline again to compare.");
            }

            StringBuilder file = new StringBuilder();
            file.AppendLine("created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            file.AppendLine("conditions=" + conditions);
            file.Append("blocks=");
            foreach (double v in now)
                file.Append(v.ToString("R", inv)).Append(' ');
            file.AppendLine();
            File.WriteAllText(current, file.ToString());
            SessionText = sb.ToString();
        }

        // ---- output ----

        public static string Text()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            string kind = RunKind == Kind.AB ? "A/B, each option off (A) vs on (B)" : RunKind == Kind.AA ? "A/A method check" : "baseline session";
            sb.AppendLine($"== Benchmark: {kind} ==");
            sb.AppendLine(string.Format(inv, "blocks of {0:F0} s after {1:F1} s settling, {2:F0} s warm-up; pairs {3}..{4}; GC frames left out of the averages", BlockSeconds, SettleSeconds, WarmupSeconds, MinPairs, MaxPairs));
            if (Results.Count > 0)
            {
                sb.AppendLine("option                          off ms   on ms   gain ms/f   95% interval        %      p99 gain   1% low off->on   GC off/on  pairs  verdict");
                foreach (OptionResult r in Results)
                {
                    sb.AppendLine(string.Format(inv, "{0,-30} {1,7:F3} {2,7:F3}  {3,9:F4}  {4,8:F4} .. {5,8:F4}  {6,6:F2}  {7,9:F3}  {8,6:F1} -> {9,6:F1}  {10,4}/{11,-4}  {12,5}  {13}",
                        r.Option, r.OffMs, r.OnMs, r.Gain.Mean, r.Gain.Low, r.Gain.High, r.GainPct, r.P99Gain.Mean, r.Low1Off, r.Low1On, r.GcOff, r.GcOn, r.Pairs, r.Verdict));
                }
                sb.AppendLine("gain > 0 means the option makes frames shorter. Method noise (SD of pair differences) is in report.json.");
            }
            if (RunKind == Kind.Baseline)
            {
                List<double> means = Blocks.ConvertAll(b => b.MeanMs);
                double m = Stats.Mean(means);
                sb.AppendLine(string.Format(inv, "baseline: {0:F3} ms/frame ({1:F1} FPS) over {2} blocks, block SD {3:F3} ms", m, 1000.0 / m, means.Count, Math.Sqrt(Stats.Variance(means, m))));
            }
            if (SessionText != null)
                sb.Append(SessionText);
            return sb.ToString();
        }

        public static void Json(JsonWriter j, string description)
        {
            j.BeginObject();
            j.Prop("kind", RunKind.ToString()).Prop("description", description);
            j.Name("results").BeginArray();
            foreach (OptionResult r in Results)
            {
                List<double> d = PairDifferences(r.Option, b => b.MeanMs);
                double sd = Math.Sqrt(Stats.Variance(d, Stats.Mean(d)));
                j.BeginObject().Prop("option", r.Option).Prop("offMs", r.OffMs).Prop("onMs", r.OnMs)
                    .Prop("gainMs", r.Gain.Mean, 5).Prop("gainLow", r.Gain.Low, 5).Prop("gainHigh", r.Gain.High, 5).Prop("gainPct", r.GainPct, 3)
                    .Prop("p99Gain", r.P99Gain.Mean).Prop("p99Low", r.P99Gain.Low).Prop("p99High", r.P99Gain.High)
                    .Prop("medianGain", r.MedianGain.Mean, 5).Prop("medianLow", r.MedianGain.Low, 5).Prop("medianHigh", r.MedianGain.High, 5)
                    .Prop("low1Off", r.Low1Off, 2).Prop("low1On", r.Low1On, 2).Prop("gcOff", r.GcOff).Prop("gcOn", r.GcOn)
                    .Prop("pairs", r.Pairs).Prop("pairSdMs", sd, 5).Prop("verdict", r.Verdict).EndObject();
            }
            j.EndArray();
            j.Name("blocks").BeginArray();
            foreach (Block b in Blocks)
                j.BeginObject().Prop("option", b.Option).Prop("on", b.On).Prop("index", b.Index).Prop("t", b.StartS, 1).Prop("frames", b.Frames)
                    .Prop("meanMs", b.MeanMs).Prop("medianMs", b.MedianMs).Prop("p99", b.P99).Prop("low1", b.Low1Fps, 2)
                    .Prop("gcFrames", b.GcFrames).Prop("gcAvgMs", b.GcAvgMs, 2).EndObject();
            j.EndArray();
            if (SessionJson != null)
            {
                j.Name("session");
                SessionJson(j);
            }
            j.Name("conditions");
            Benchmark.WriteConditions(j);
            j.EndObject();
        }
    }
}
