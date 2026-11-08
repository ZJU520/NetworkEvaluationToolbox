using NetToolbox.Models;
using NetToolbox.Testers;

namespace NetToolbox.Engine;

/// <summary>测试编排引擎：并行跑 DNS/TCP/UDP（持续压测时长），再串行跑 NAT 类型与容量。</summary>
public class TestEngine
{
    private readonly TestConfig _cfg;

    public event Action<string>? Log;
    public event Action<string, string>? Metrics;  // (维度Key, 指标文本)
    public event Action<string, string>? Status;   // (维度Key, 状态文本)
    public event Action<double>? Progress;         // 0-100

    private static readonly Dictionary<string, double> Weights = new()
    {
        ["dns"] = 0.25, ["tcp"] = 0.30, ["udp"] = 0.20, ["nat"] = 0.25, ["mqtt"] = 0.30, ["speed"] = 0.20
    };

    public TestEngine(TestConfig cfg) => _cfg = cfg;

    public async Task<ReportData> RunAsync(CancellationToken ct)
    {
        var report = new ReportData { Config = _cfg, StartTime = DateTime.Now };

        // 估算总时长用于进度条：压测时长 + NAT 阶段预估
        var natEnabled = _cfg.EnableNatType || _cfg.EnableNatCapacity;
        var natEstimate = 0.0;
        if (_cfg.EnableNatType) natEstimate += 8;
        if (_cfg.EnableNatCapacity) natEstimate += new[] { 100, 250, 500, 1000, 2000 }.Count(x => x <= _cfg.NatMaxSessions) * 5.0 + 5;
        var phase1Weight = _cfg.EnableDns || _cfg.EnableTcp || _cfg.EnableUdp || _cfg.EnableMqtt ? _cfg.DurationSeconds : 0.0;
        var speedEstimate = _cfg.EnableSpeedTest ? Math.Clamp(_cfg.SpeedTestDurationSeconds, 5, 120) * 2.0 + 5 : 0.0;
        var totalEstimate = Math.Max(5, phase1Weight + (natEnabled ? natEstimate : 0) + speedEstimate);

        var done = false;
        var progressTask = Task.Run(async () =>
        {
            var start = DateTime.UtcNow;
            while (!done && !ct.IsCancellationRequested)
            {
                var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                Progress?.Invoke(Math.Min(99, 100 * elapsed / totalEstimate));
                await Task.Delay(500);
            }
        });

        try
        {
            // —— 阶段一：压测时长内并行运行 DNS / TCP / UDP ——
            var testers = new List<ITester>();
            if (_cfg.EnableDns) testers.Add(new DnsStabilityTester());
            if (_cfg.EnableTcp) testers.Add(new TcpStabilityTester());
            if (_cfg.EnableUdp) testers.Add(new UdpStabilityTester());
            if (_cfg.EnableMqtt) testers.Add(new MqttStabilityTester());

            var phase1 = testers.Select(t => Task.Run(async () =>
            {
                Status?.Invoke(t.Key, "运行中");
                try
                {
                    var res = await t.RunAsync(_cfg, s => Log?.Invoke(s), m => Metrics?.Invoke(t.Key, m), ct);
                    lock (report.Dimensions) report.Dimensions.Add(res);
                    Status?.Invoke(t.Key, res.Skipped ? "跳过" : "完成");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[{t.Name}] 异常: {ex.Message}");
                    Status?.Invoke(t.Key, "错误");
                }
            })).ToArray();
            await Task.WhenAll(phase1);

            // —— 阶段二：NAT 评估（类型 + 容量）——
            if (natEnabled && !ct.IsCancellationRequested)
            {
                var nat = new NatTester();
                Status?.Invoke("nat", "运行中");
                try
                {
                    var res = await nat.RunAsync(_cfg, s => Log?.Invoke(s), m => Metrics?.Invoke("nat", m), ct);
                    lock (report.Dimensions) report.Dimensions.Add(res);
                    Status?.Invoke("nat", res.Skipped ? "跳过" : "完成");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[NAT] 异常: {ex.Message}");
                    Status?.Invoke("nat", "错误");
                }
            }

            // —— 阶段三：带宽测速（会打满链路，必须在压测结束后单独运行）——
            if (_cfg.EnableSpeedTest && !ct.IsCancellationRequested)
            {
                var speed = new SpeedTester();
                Status?.Invoke("speed", "运行中");
                try
                {
                    var res = await speed.RunAsync(_cfg, s => Log?.Invoke(s), m => Metrics?.Invoke("speed", m), ct);
                    lock (report.Dimensions) report.Dimensions.Add(res);
                    Status?.Invoke("speed", res.Skipped ? "跳过" : "完成");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[SPEED] 异常: {ex.Message}");
                    Status?.Invoke("speed", "错误");
                }
            }
        }
        finally
        {
            done = true;
            await progressTask;
        }

        report.EndTime = DateTime.Now;
        report.Cancelled = ct.IsCancellationRequested;

        // 综合得分（按维度权重，未启用/跳过的不参与）
        var scored = report.Dimensions.Where(d => !d.Skipped && d.Score > 0).ToList();
        var weightSum = scored.Sum(d => Weights.GetValueOrDefault(d.Key, 0.1));
        report.TotalScore = weightSum <= 0 ? 0
            : Math.Round(scored.Sum(d => d.Score * Weights.GetValueOrDefault(d.Key, 0.1)) / weightSum, 1);
        report.Grade = ReportData.GradeOf(report.TotalScore);

        Progress?.Invoke(100);
        return report;
    }
}
