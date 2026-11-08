using System.IO;
using System.Net;
using System.Net.Http;
using NetToolbox.Engine;
using NetToolbox.Models;

namespace NetToolbox.Testers;

/// <summary>
/// 带宽测速：多并发 HTTP 下载/上传，统计实际吞吐量（Mbps）。
/// 默认使用 Cloudflare 全球测速端点（国内可直连）；地址可在配置中更换为内网/CDN 测速点。
/// 注意：测速会打满链路，必须在稳定性压测结束后单独运行，避免干扰延迟采样。
/// </summary>
public class SpeedTester : ITester
{
    public string Key => "speed";
    public string Name => "带宽测速";

    public async Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct)
    {
        var r = new DimensionResult { Key = Key, Name = Name };

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // 测速端点（Cloudflare WAF）会拒绝非浏览器 UA，需伪装
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");

        var conns = Math.Clamp(cfg.SpeedTestConnections, 2, 16);
        var seconds = Math.Clamp(cfg.SpeedTestDurationSeconds, 5, 120);

        long downBytes = 0, upBytes = 0;
        int downErrors = 0, upErrors = 0;
        var dlStart = DateTime.UtcNow;
        var dlEnd = dlStart.AddSeconds(seconds);

        // —— 实时指标 ——
        var reporter = Task.Run(async () =>
        {
            var phaseDl = true;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
                var now = DateTime.UtcNow;
                if (now < dlEnd)
                {
                    var el = (now - dlStart).TotalSeconds;
                    metrics($"下载测速中… ↓ {(Interlocked.Read(ref downBytes) * 8.0 / 1e6 / Math.Max(1, el)):F1} Mbps");
                }
                else if (now < dlEnd.AddSeconds(seconds))
                {
                    var el = (now - dlEnd).TotalSeconds;
                    metrics($"上传测速中… ↑ {(Interlocked.Read(ref upBytes) * 8.0 / 1e6 / Math.Max(1, el)):F1} Mbps");
                }
                else break;
            }
        });

        // —— 下载 ——
        log($"[SPEED] 下载测速：{conns} 并发 × {seconds}s（{cfg.SpeedDownloadUrl}）");
        var dlTasks = Enumerable.Range(0, conns).Select(_ => Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            int consecutiveErrors = 0;
            while (DateTime.UtcNow < dlEnd && !ct.IsCancellationRequested)
            {
                try
                {
                    using var resp = await http.GetAsync(cfg.SpeedDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref downErrors);
                        if (++consecutiveErrors >= 3) break; // 连续失败说明端点拒绝，放弃
                        await Task.Delay(300, ct).ContinueWith(_ => { });
                        continue;
                    }
                    consecutiveErrors = 0;
                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    int n;
                    while (DateTime.UtcNow < dlEnd && (n = await stream.ReadAsync(buf.AsMemory(), ct)) > 0)
                        Interlocked.Add(ref downBytes, n);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    Interlocked.Increment(ref downErrors);
                    if (++consecutiveErrors >= 3) break;
                    try { await Task.Delay(300, ct); } catch { break; }
                }
            }
        })).ToArray();
        await Task.WhenAll(dlTasks);
        var dlSecs = (DateTime.UtcNow - dlStart).TotalSeconds;
        var dlMbps = downBytes * 8.0 / 1e6 / dlSecs;
        log($"[SPEED] 下载完成：{dlMbps:F1} Mbps（{downBytes / 1048576.0:F1} MB）");

        // —— 上传 ——
        var payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);
        var ulStart = DateTime.UtcNow;
        var ulEnd = ulStart.AddSeconds(seconds);
        log($"[SPEED] 上传测速：{conns} 并发 × {seconds}s（{cfg.SpeedUploadUrl}）");
        var ulTasks = Enumerable.Range(0, conns).Select(_ => Task.Run(async () =>
        {
            while (DateTime.UtcNow < ulEnd && !ct.IsCancellationRequested)
            {
                try
                {
                    using var content = new ByteArrayContent(payload);
                    using var resp = await http.PostAsync(cfg.SpeedUploadUrl, content, ct);
                    if (resp.IsSuccessStatusCode) Interlocked.Add(ref upBytes, payload.Length);
                    else { Interlocked.Increment(ref upErrors); break; }
                }
                catch (OperationCanceledException) { break; }
                catch { Interlocked.Increment(ref upErrors); break; }
            }
        })).ToArray();
        await Task.WhenAll(ulTasks);
        var ulSecs = (DateTime.UtcNow - ulStart).TotalSeconds;
        var ulMbps = upBytes * 8.0 / 1e6 / ulSecs;
        log($"[SPEED] 上传完成：{ulMbps:F1} Mbps（{upBytes / 1048576.0:F1} MB）");

        await reporter;

        // —— 汇总 ——
        if (downBytes == 0 && upBytes == 0)
        {
            r.Skipped = true;
            log("[SPEED] 测速服务器不可达，跳过");
            r.Findings.Add("测速服务器不可达（可能被网络封锁），请更换测速地址后重试");
            return r;
        }

        r.TotalCount = conns * 2;
        r.SuccessCount = (downBytes > 0 ? conns : 0) + (upBytes > 0 ? conns : 0);
        r.Extras.Add(("下载带宽", downBytes > 0 ? $"{dlMbps:F1} Mbps（{downBytes / 1048576.0:F1} MB / {dlSecs:F0}s）" : "不可达"));
        r.Extras.Add(("上传带宽", upBytes > 0 ? $"{ulMbps:F1} Mbps（{upBytes / 1048576.0:F1} MB / {ulSecs:F0}s）" : "不可达"));
        r.Extras.Add(("测速端点", cfg.SpeedDownloadUrl.Replace("https://", "").Replace("http://", "")));
        if (downErrors > 0 || upErrors > 0)
            r.Extras.Add(("连接异常", $"下载 {downErrors} 次 / 上传 {upErrors} 次"));

        r.Findings.Add($"实测带宽：下载 {dlMbps:F1} Mbps / 上传 {ulMbps:F1} Mbps（{conns} 并发）");
        if (dlMbps > 0 && ulMbps > 0 && dlMbps / Math.Max(0.1, ulMbps) > 5)
            r.Findings.Add("上下行明显不对称：家用宽带属正常现象（下行大、上行小）；若业务依赖上行（视频回传、文件上传）需关注");

        double DlScore(double m) => m >= 500 ? 100 : m >= 200 ? 95 : m >= 100 ? 88 : m >= 50 ? 75 : m >= 20 ? 55 : m >= 10 ? 40 : m >= 1 ? 25 : 10;
        double UlScore(double m) => m >= 100 ? 100 : m >= 50 ? 92 : m >= 30 ? 82 : m >= 10 ? 65 : m >= 5 ? 50 : m >= 1 ? 35 : 10;
        var dlScore = downBytes > 0 ? DlScore(dlMbps) : 0;
        var ulScore = upBytes > 0 ? UlScore(ulMbps) : 0;
        r.Score = Math.Round(downBytes > 0 && upBytes > 0 ? 0.6 * dlScore + 0.4 * ulScore
            : downBytes > 0 ? dlScore : ulScore, 1);
        r.Grade = ReportData.GradeOf(r.Score);

        if (dlMbps > 0 && dlMbps < 20)
            r.Suggestions.Add($"下载带宽仅 {dlMbps:F1} Mbps，低于常见家宽水平：建议检查 Wi-Fi 信号（优先 5GHz/有线）、光猫协商速率、套餐速率，或分时段对比是否高峰期拥塞。");
        if (ulMbps > 0 && ulMbps < 5)
            r.Suggestions.Add($"上传带宽仅 {ulMbps:F1} Mbps：物联网网关数据回传一般够用，但若有视频/大文件上行需求会受限，可向运营商确认上行限速。");
        if (dlMbps >= 100)
            r.Findings.Add("下行带宽充足，满足绝大多数物联网/办公场景");

        log($"[SPEED] 完成：↓ {dlMbps:F1} Mbps / ↑ {ulMbps:F1} Mbps");
        return r;
    }
}
