namespace NetToolbox.Models;

/// <summary>单次采样：LatencyMs &lt; 0 表示失败/超时</summary>
public record MetricSample(DateTime Time, double LatencyMs)
{
    public bool Success => LatencyMs >= 0;
}

/// <summary>一个测试维度（DNS/TCP/UDP/NAT）的完整结果</summary>
public class DimensionResult
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Skipped { get; set; }

    public List<MetricSample> Samples { get; } = new();
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public double SuccessRate => TotalCount == 0 ? 0 : 100.0 * SuccessCount / TotalCount;

    public double AvgMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double JitterMs { get; set; }

    /// <summary>0-100 维度得分</summary>
    public double Score { get; set; }
    public string Grade { get; set; } = "";

    /// <summary>结论文本（如 NAT 类型、容量结论）</summary>
    public List<string> Findings { get; } = new();
    /// <summary>针对性优化建议</summary>
    public List<string> Suggestions { get; } = new();

    /// <summary>附加键值数据（报告表格用）</summary>
    public List<(string Key, string Value)> Extras { get; } = new();
}

public class ReportData
{
    public TestConfig Config { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool Cancelled { get; set; }

    public List<DimensionResult> Dimensions { get; } = new();

    /// <summary>维度名 -> 权重，综合总分</summary>
    public double TotalScore { get; set; }
    public string Grade { get; set; } = "";

    public static string GradeOf(double score) =>
        score >= 90 ? "优秀" : score >= 75 ? "良好" : score >= 60 ? "一般" : "较差";
}
