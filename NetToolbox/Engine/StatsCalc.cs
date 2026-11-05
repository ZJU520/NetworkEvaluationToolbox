namespace NetToolbox.Engine;

/// <summary>延迟统计计算</summary>
public static class StatsCalc
{
    /// <summary>返回 (平均, P50, P95, P99, 抖动)。抖动 = 相邻样本延迟差绝对值的均值。</summary>
    public static (double Avg, double P50, double P95, double P99, double Jitter) Compute(List<double> lat)
    {
        if (lat.Count == 0) return (0, 0, 0, 0, 0);
        var sorted = lat.OrderBy(x => x).ToList();

        double P(double q)
        {
            var idx = (int)Math.Clamp(Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1);
            return Math.Round(sorted[idx], 1);
        }

        var avg = Math.Round(lat.Average(), 1);
        double jitter = 0;
        if (lat.Count > 1)
        {
            double sum = 0;
            for (int i = 1; i < lat.Count; i++) sum += Math.Abs(lat[i] - lat[i - 1]);
            jitter = Math.Round(sum / (lat.Count - 1), 1);
        }
        return (avg, P(0.5), P(0.95), P(0.99), jitter);
    }
}
