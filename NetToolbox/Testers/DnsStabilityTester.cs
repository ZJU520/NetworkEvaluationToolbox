using System.Net;
using NetToolbox.Engine;
using NetToolbox.Models;
using NetToolbox.Net;

namespace NetToolbox.Testers;

public interface ITester
{
    string Key { get; }
    string Name { get; }

    /// <summary>运行测试。log 输出日志行，metrics 输出该维度实时指标摘要。</summary>
    Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct);
}

/// <summary>DNS 解析稳定性：多服务器 × 多域名持续解析，统计成功率 / 延迟 / 抖动。</summary>
public class DnsStabilityTester : ITester
{
    public string Key => "dns";
    public string Name => "DNS 解析稳定性";

    public async Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct)
    {
        var r = new DimensionResult { Key = Key, Name = Name };

        var servers = new List<IPAddress>();
        foreach (var s in cfg.DnsServers)
            if (IPAddress.TryParse(s.Trim(), out var ip)) servers.Add(ip);
        if (servers.Count == 0)
        {
            log("[DNS] 未配置有效服务器地址，跳过");
            r.Skipped = true;
            return r;
        }
        var perServer = servers.ToDictionary(s => s, _ => (total: 0, ok: 0));

        var workerCount = Math.Clamp(cfg.Concurrency, 2, 32);
        var deadline = DateTime.UtcNow.AddSeconds(cfg.DurationSeconds);
        log($"[DNS] 开始：{workerCount} 并发 × {servers.Count} 服务器 × {cfg.TestDomains.Count} 域名，持续 {cfg.DurationSeconds}s");

        var reporter = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
                lock (r.Samples)
                {
                    var ok = r.Samples.Count(x => x.Success);
                    var avg = ok > 0 ? r.Samples.Where(x => x.Success).Average(x => x.LatencyMs) : 0;
                    metrics($"成功率 {(r.Samples.Count == 0 ? 0 : 100.0 * ok / r.Samples.Count):F1}%｜平均 {avg:F0}ms｜样本 {r.Samples.Count}");
                }
            }
        });

        var workers = Enumerable.Range(0, workerCount).Select(w => Task.Run(async () =>
        {
            var i = w;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var server = servers[i % servers.Count];
                var domain = cfg.TestDomains[i % cfg.TestDomains.Count];
                i += workerCount;

                var ms = await DnsProbe.QueryAsync(server, domain, 2000, ct);
                lock (r.Samples)
                {
                    r.Samples.Add(new MetricSample(DateTime.Now, ms));
                    var (t, o) = perServer[server];
                    perServer[server] = (t + 1, o + (ms >= 0 ? 1 : 0));
                }
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        })).ToArray();

        await Task.WhenAll(workers.Concat(new[] { reporter }));

        var okLat = r.Samples.Where(x => x.Success).Select(x => x.LatencyMs).ToList();
        r.TotalCount = r.Samples.Count;
        r.SuccessCount = okLat.Count;
        (r.AvgMs, r.P50Ms, r.P95Ms, r.P99Ms, r.JitterMs) = StatsCalc.Compute(okLat);

        foreach (var kv in perServer)
        {
            var (t, o) = kv.Value;
            r.Extras.Add((kv.Key.ToString(), t == 0 ? "未测试" : $"{100.0 * o / t:F1}%（{o}/{t}）"));
        }
        r.Findings.Add($"总成功率 {r.SuccessRate:F1}%（{r.SuccessCount}/{r.TotalCount}），平均 {r.AvgMs:F0}ms，P95 {r.P95Ms:F0}ms，抖动 {r.JitterMs:F1}ms");

        var worst = perServer.OrderByDescending(kv => kv.Value.total == 0 ? 0 : 1.0 - (double)kv.Value.ok / kv.Value.total).First();
        if (worst.Value.total > 0 && (double)worst.Value.ok / worst.Value.total < 0.95)
            r.Findings.Add($"服务器 {worst.Key} 成功率偏低（{100.0 * worst.Value.ok / worst.Value.total:F1}%），建议更换");

        // 评分：成功率 70 + 延迟 20 + 抖动 10
        double score = 70.0 * r.SuccessRate / 100;
        score += r.AvgMs switch { < 20 => 20, < 50 => 16, < 100 => 12, < 200 => 8, _ => 4 };
        score += r.JitterMs switch { < 5 => 10, < 15 => 8, < 30 => 5, _ => 2 };
        if (r.TotalCount < 10) score = 0; // 样本不足不给分
        r.Score = Math.Round(score, 1);
        r.Grade = ReportData.GradeOf(r.Score);

        if (r.SuccessRate < 98) r.Suggestions.Add("DNS 成功率不足 98%：若使用路由器默认 DNS，建议改用 223.5.5.5 / 119.29.29.29 等公共 DNS，或在路由器上检查 DNS 转发是否异常。");
        if (r.AvgMs > 50) r.Suggestions.Add("DNS 平均延迟偏高：选择物理位置更近的运营商 DNS，或开启本地 DNS 缓存（Windows DNS Client 服务）。");
        if (r.JitterMs > 15) r.Suggestions.Add("DNS 延迟抖动较大：常见于无线网络丢包或 DNS 服务器过载，建议有线连接测试对比。");

        log($"[DNS] 完成：{r.Findings[0]}");
        return r;
    }
}
