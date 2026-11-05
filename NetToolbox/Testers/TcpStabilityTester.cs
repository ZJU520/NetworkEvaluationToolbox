using System.Net;
using System.Net.Sockets;
using NetToolbox.Engine;
using NetToolbox.Models;
using NetToolbox.Net;

namespace NetToolbox.Testers;

/// <summary>
/// TCP 连接稳定性：持续发起短连接（记录建连成功率与建连延迟），
/// 同时保持一条长连接观察是否被中途断开。
/// </summary>
public class TcpStabilityTester : ITester
{
    public string Key => "tcp";
    public string Name => "TCP 连接稳定性";

    public async Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct)
    {
        var r = new DimensionResult { Key = Key, Name = Name };

        var targets = new List<IPEndPoint>();
        foreach (var t in cfg.TcpTargets)
        {
            var idx = t.LastIndexOf(':');
            if (idx <= 0) { log($"[TCP] 目标格式错误（应为 host:port）：{t}"); continue; }
            var ep = await AddrResolver.ResolveHostPortAsync(t, ct); // 系统解析失败自动走公共 DNS 兜底
            if (ep == null) { log($"[TCP] 解析目标失败：{t}"); continue; }
            targets.Add(ep);
            log($"[TCP] 目标就绪：{t} → {ep}");
        }
        if (targets.Count == 0)
        {
            log("[TCP] 无可用目标，跳过");
            r.Skipped = true;
            return r;
        }

        var workerCount = Math.Clamp(cfg.Concurrency, 2, 32);
        var deadline = DateTime.UtcNow.AddSeconds(cfg.DurationSeconds);
        log($"[TCP] 开始：{workerCount} 并发短连接 + 1 条长连接保持，持续 {cfg.DurationSeconds}s");

        int longHoldDrops = 0;

        // —— 长连接保持：中途断开则重连并计数 ——
        var holdTask = Task.Run(async () =>
        {
            var target = targets[0];
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(3000);
                    await tcp.ConnectAsync(target, timeoutCts.Token);
                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && tcp.Connected)
                    {
                        try { await Task.Delay(1000, ct); }
                        catch (OperationCanceledException) { break; }
                        // Poll 检测连接是否已被对端关闭/重置
                        if (tcp.Client.Poll(0, SelectMode.SelectRead) && tcp.Available == 0)
                        {
                            Interlocked.Increment(ref longHoldDrops);
                            log("[TCP] 长连接被对端断开（可能为空闲回收或中间设备重置）");
                            break;
                        }
                    }
                }
                catch { Interlocked.Increment(ref longHoldDrops); }
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        });

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
                    metrics($"建连成功率 {(r.Samples.Count == 0 ? 0 : 100.0 * ok / r.Samples.Count):F1}%｜平均建连 {avg:F0}ms｜样本 {r.Samples.Count}｜长连接断开 {longHoldDrops} 次");
                }
            }
        });

        var workers = Enumerable.Range(0, workerCount).Select(w => Task.Run(async () =>
        {
            var i = w;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var target = targets[i % targets.Count];
                i += workerCount;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double ms;
                try
                {
                    using var tcp = new TcpClient();
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(3000);
                    await tcp.ConnectAsync(target, timeoutCts.Token);
                    sw.Stop();
                    ms = sw.Elapsed.TotalMilliseconds;
                }
                catch { ms = -1; }

                lock (r.Samples) r.Samples.Add(new MetricSample(DateTime.Now, ms));
                try { await Task.Delay(300, ct); }
                catch (OperationCanceledException) { break; }
            }
        })).ToArray();

        await Task.WhenAll(workers.Concat(new[] { reporter, holdTask }));

        var okLat = r.Samples.Where(x => x.Success).Select(x => x.LatencyMs).ToList();
        r.TotalCount = r.Samples.Count;
        r.SuccessCount = okLat.Count;
        (r.AvgMs, r.P50Ms, r.P95Ms, r.P99Ms, r.JitterMs) = StatsCalc.Compute(okLat);

        r.Extras.Add(("长连接保持", $"断开 {longHoldDrops} 次 / {cfg.DurationSeconds}s"));
        r.Findings.Add($"建连成功率 {r.SuccessRate:F1}%（{r.SuccessCount}/{r.TotalCount}），平均建连 {r.AvgMs:F0}ms，P95 {r.P95Ms:F0}ms，抖动 {r.JitterMs:F1}ms");

        // 评分：成功率 70 + 建连延迟 20 + 抖动 10；长连接断开按次扣分
        double score = 70.0 * r.SuccessRate / 100;
        score += r.AvgMs switch { < 10 => 20, < 30 => 16, < 60 => 12, < 150 => 8, _ => 4 };
        score += r.JitterMs switch { < 3 => 10, < 10 => 8, < 25 => 5, _ => 2 };
        score -= Math.Min(20, longHoldDrops * 3);
        if (r.TotalCount < 10) score = 0;
        r.Score = Math.Round(Math.Max(0, score), 1);
        r.Grade = ReportData.GradeOf(r.Score);

        if (r.SuccessRate < 99) r.Suggestions.Add("TCP 建连存在失败：可能是目标端口限流、本地网络丢包或防火墙干扰，建议换目标对比定位。");
        if (r.P95Ms > 100) r.Suggestions.Add("TCP 建连 P95 延迟高：常见于网络拥塞或跨运营商路由，可尝试有线连接或更换出口。");
        if (longHoldDrops >= 3) r.Suggestions.Add("长连接频繁被断开：NAT 空闲回收过短或中间设备重置连接，长连接应用建议开启心跳保活（间隔小于 60s）。");

        log($"[TCP] 完成：{r.Findings[0]}，长连接断开 {longHoldDrops} 次");
        return r;
    }
}
