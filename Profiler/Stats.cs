using System;
using System.Collections.Generic;

namespace MWCFsmProfiler
{
    internal struct Interval
    {
        public double Mean;
        public double Low;
        public double High;
        public double Sd;
        public int N;
    }

    internal sealed class FloatComparer : IComparer<float>
    {
        public static readonly FloatComparer Instance = new FloatComparer();
        public int Compare(float a, float b) { return a < b ? -1 : a > b ? 1 : 0; }
    }

    internal sealed class DoubleComparer : IComparer<double>
    {
        public static readonly DoubleComparer Instance = new DoubleComparer();
        public int Compare(double a, double b) { return a < b ? -1 : a > b ? 1 : 0; }
    }

    // Small statistics toolkit. .NET 3.5 has no statistics library, and the benchmark needs only these.
    internal static class Stats
    {
        // Expects sorted input. Nearest-rank percentile.
        public static double Percentile(float[] sorted, double p)
        {
            if (sorted.Length == 0)
                return 0;
            int i = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, i))];
        }

        public static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0)
                return 0;
            double[] a = values.ToArray();
            Array.Sort(a, DoubleComparer.Instance);
            int i = (int)Math.Ceiling(p * a.Length) - 1;
            return a[Math.Max(0, Math.Min(a.Length - 1, i))];
        }

        // "1% low": the FPS of the slowest 1% of frames, averaged. Expects sorted frame times in ms.
        public static double LowFps(float[] sorted, double fraction)
        {
            if (sorted.Length == 0)
                return 0;
            int count = Math.Max(1, (int)(sorted.Length * fraction));
            double sum = 0;
            for (int i = sorted.Length - count; i < sorted.Length; i++)
                sum += sorted[i];
            return 1000.0 / (sum / count);
        }

        public static double Mean(IList<double> values)
        {
            double sum = 0;
            for (int i = 0; i < values.Count; i++)
                sum += values[i];
            return values.Count > 0 ? sum / values.Count : 0;
        }

        public static double Variance(IList<double> values, double mean)
        {
            if (values.Count < 2)
                return 0;
            double sum = 0;
            for (int i = 0; i < values.Count; i++)
                sum += (values[i] - mean) * (values[i] - mean);
            return sum / (values.Count - 1);
        }

        // Two-sided 95% critical values of Student's t for df = 1..30; above 30 the normal value is close enough.
        private static readonly double[] t975 =
        {
            12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042
        };

        public static double TCritical(double df)
        {
            if (df < 1)
                return double.NaN;
            int i = (int)Math.Floor(df);
            if (i <= 30)
                return t975[i - 1];
            if (df <= 60)
                return 2.042 + (2.000 - 2.042) * (df - 30) / 30.0;
            return 1.96;
        }

        // Mean of paired differences with a 95% t interval.
        public static Interval Paired(IList<double> differences)
        {
            Interval r;
            r.N = differences.Count;
            r.Mean = Mean(differences);
            r.Sd = Math.Sqrt(Variance(differences, r.Mean));
            double half = r.N > 1 ? TCritical(r.N - 1) * r.Sd / Math.Sqrt(r.N) : double.PositiveInfinity;
            r.Low = r.Mean - half;
            r.High = r.Mean + half;
            return r;
        }

        // Welch's t interval for mean(b) - mean(a), for independent samples (two separate sessions).
        public static Interval Welch(IList<double> a, IList<double> b)
        {
            Interval r;
            double ma = Mean(a), mb = Mean(b);
            double va = Variance(a, ma) / Math.Max(1, a.Count);
            double vb = Variance(b, mb) / Math.Max(1, b.Count);
            double se = Math.Sqrt(va + vb);
            double df = (va + vb) * (va + vb) /
                (va * va / Math.Max(1, a.Count - 1) + vb * vb / Math.Max(1, b.Count - 1));
            r.N = Math.Min(a.Count, b.Count);
            r.Mean = mb - ma;
            r.Sd = se;
            double half = a.Count > 1 && b.Count > 1 && se > 0 ? TCritical(df) * se : double.PositiveInfinity;
            r.Low = r.Mean - half;
            r.High = r.Mean + half;
            return r;
        }

        // Percentile bootstrap of any statistic of paired values. Fixed seed so the same data always gives the
        // same report.
        public static Interval Bootstrap(IList<double> values, Func<List<double>, double> statistic, int resamples = 10000)
        {
            Interval r;
            r.N = values.Count;
            List<double> all = new List<double>(values);
            r.Mean = statistic(all);
            r.Sd = 0;
            if (values.Count < 2)
            {
                r.Low = double.NegativeInfinity;
                r.High = double.PositiveInfinity;
                return r;
            }

            System.Random random = new System.Random(12345);
            double[] results = new double[resamples];
            List<double> sample = new List<double>(values.Count);
            for (int k = 0; k < resamples; k++)
            {
                sample.Clear();
                for (int i = 0; i < values.Count; i++)
                    sample.Add(values[random.Next(values.Count)]);
                results[k] = statistic(sample);
            }
            Array.Sort(results, DoubleComparer.Instance);
            r.Low = results[(int)(resamples * 0.025)];
            r.High = results[(int)(resamples * 0.975) - 1];
            return r;
        }
    }
}
