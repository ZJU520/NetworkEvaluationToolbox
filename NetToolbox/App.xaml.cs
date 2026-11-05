using System.IO;
using System.Windows;
using NetToolbox.Engine;
using NetToolbox.Models;
using NetToolbox.Report;

namespace NetToolbox;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // 端到端自检模式：NetToolbox.exe --smoke [秒数]
        if (e.Args.Contains("--smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            var seconds = e.Args.Length > 1 && int.TryParse(e.Args[1], out var s) ? s : 8;
            var cfg = new TestConfig
            {
                DurationSeconds = seconds,
                Concurrency = 4,
                NatMaxSessions = 250,
                MqttKeepAliveSeconds = 8, // 冒烟测试里更快触发心跳
            };
            var logFile = Path.Combine(AppContext.BaseDirectory, "smoke.log");
            var log = new List<string>();
            var engine = new TestEngine(cfg);
            engine.Log += s2 => log.Add(s2);
            engine.Metrics += (k, m) => log.Add($"  [{k}] {m}");

            var report = await engine.RunAsync(CancellationToken.None);
            var path = ReportGenerator.Write(report);
            log.Add($"报告: {path}");
            log.Add($"总分: {report.TotalScore}（{report.Grade}）");
            foreach (var d in report.Dimensions)
                log.Add($"  {d.Name}: {(d.Skipped ? "跳过" : $"{d.Score} 分 / {d.Grade}")}");
            await File.WriteAllLinesAsync(logFile, log);
            Shutdown(0);
            return;
        }
        base.OnStartup(e);
    }
}
