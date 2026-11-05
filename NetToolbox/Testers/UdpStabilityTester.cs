using System.Net;
using System.Net.Sockets;
using NetToolbox.Engine;
using NetToolbox.Models;
using NetToolbox.Net;

namespace NetToolbox.Testers;

/// <summary>
/// UDP 连通性稳定性：持续向 STUN 服务器发送 Binding Request，
/// 以响应超时近似丢包，统计丢包率 / RTT / 抖动。
/// </summary>
public class UdpStabilityTester : ITester
{
    public string Key => "udp";
    public string Name => "UDP 连通性稳定性";

    public async Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct)
    {
        var r = new DimensionResult { Key = Key, Name = Name };

        // 依次尝试 STUN 服务器，选第一个可响应的
        IPEndPoint? server = null;
        string serverName = "";
        foreach (var s in cfg.StunServers)
        {
            var cand = await StunClient.ResolveAsync(s, ct);
            if (cand == null) continue;
            using var probe = new UdpClient(AddressFamily.InterNetwork);
            var (m, _) = await StunClient.BindingAsync(probe, cand, 1500, ct);
            if (m != null) { server = cand; serverName = s; log($"[UDP] 使用 STUN 服务器 {s}（{cand}）"); break; }
            log($"[UDP] STUN 服务器 {s} 无响应，尝试下一个");
        }
        if (server != null)
            r.Extras.Add(("使用的 STUN 服务器", $"{serverName}（{(serverName.Contains("google") ? "国际链路，延迟含跨境影响" : "国内链路")}）"));
        if (server == null)
        {
            log("[UDP] STUN 服务器不可用，跳过");
            r.Skipped = true;
            return r;
        }

        var deadline = DateTime.UtcNow.AddSeconds(cfg.DurationSeconds);
        log($"[UDP] 开始：STUN 探测 {server}，持续 {cfg.DurationSeconds}s");

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
                    metrics($"丢包率 {(r.Samples.Count == 0 ? 0 : 100.0 * (r.Samples.Count - ok) / r.Samples.Count):F1}%｜平均 RTT {avg:F0}ms｜样本 {r.Samples.Count}");
                }
            }
        });

        // 2 个并发探测流（避免单流被 STUN 服务器限速）
        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var (_, rtt) = await StunClient.BindingAsync(udp, server, 2000, ct);
                lock (r.Samples) r.Samples.Add(new MetricSample(DateTime.Now, rtt));
                try { await Task.Delay(400, ct); }
                catch (OperationCanceledException) { break; }
            }
        })).ToArray();

        await Task.WhenAll(workers.Concat(new[] { reporter }));

        var okLat = r.Samples.Where(x => x.Success).Select(x => x.LatencyMs).ToList();
        r.TotalCount = r.Samples.Count;
        r.SuccessCount = okLat.Count;
        var lossRate = r.TotalCount == 0 ? 100 : 100.0 * (r.TotalCount - r.SuccessCount) / r.TotalCount;
        (r.AvgMs, r.P50Ms, r.P95Ms, r.P99Ms, r.JitterMs) = StatsCalc.Compute(okLat);

        r.Findings.Add($"丢包率 {lossRate:F1}%（丢 {r.TotalCount - r.SuccessCount}/{r.TotalCount}），平均 RTT {r.AvgMs:F0}ms，P95 {r.P95Ms:F0}ms，抖动 {r.JitterMs:F1}ms");

        // 评分：不丢包 70 + RTT 20 + 抖动 10
        double score = 70.0 * (100 - lossRate) / 100;
        score += r.AvgMs switch { < 20 => 20, < 50 => 16, < 100 => 12, < 200 => 8, _ => 4 };
        score += r.JitterMs switch { < 5 => 10, < 15 => 8, < 30 => 5, _ => 2 };
        if (r.TotalCount < 10) score = 0;
        r.Score = Math.Round(score, 1);
        r.Grade = ReportData.GradeOf(r.Score);

        if (lossRate > 1) r.Suggestions.Add("UDP 存在丢包：实时应用（游戏/音视频）体验会受影响；建议检查 Wi-Fi 信号质量、网线或运营商线路，并联系 ISP 排查 QoS 限速。");
        if (r.AvgMs > 100) r.Suggestions.Add("UDP RTT 偏高：可能是跨境/跨运营商路径劣化，尝试更换出口或联系运营商。");
        if (r.SuccessCount == 0) r.Suggestions.Add("UDP 全部无响应：可能被防火墙/运营商完全阻断 UDP，NAT 探测结果也会受影响。");

        log($"[UDP] 完成：{r.Findings[0]}");
        return r;
    }
}
