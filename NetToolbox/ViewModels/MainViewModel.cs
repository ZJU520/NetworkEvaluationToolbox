using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using NetToolbox.Engine;
using NetToolbox.Models;

namespace NetToolbox.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _exec(p);
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class DimensionStatus : INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    private string _status = "待开始";
    public string Status { get => _status; set { _status = value; OnChanged(); } }

    private string _metrics = "";
    public string Metrics { get => _metrics; set { _metrics = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string n = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class MainViewModel : INotifyPropertyChanged
{
    // —— 配置项（TextBox 绑定） ——
    public string DnsServers { get; set; } = "223.5.5.5, 119.29.29.29, 114.114.114.114";
    public string TestDomains { get; set; } = "www.baidu.com, www.qq.com, www.aliyun.com";
    public string TcpTargets { get; set; } = "www.baidu.com:443, www.qq.com:443, www.taobao.com:443";
    public string StunServers { get; set; } = "stun.chat.bilibili.com:3478, stun.l.google.com:19302, stun.cloudflare.com:3478, global.stun.twilio.com:3478";
    public string MqttBroker { get; set; } = "broker-cn.emqx.io:1883";
    public string MqttKeepAliveText { get; set; } = "10";
    public string DurationText { get; set; } = "600";
    public string ConcurrencyText { get; set; } = "8";
    public string NatMaxText { get; set; } = "2000";

    public bool EnableDns { get; set; } = true;
    public bool EnableTcp { get; set; } = true;
    public bool EnableUdp { get; set; } = true;
    public bool EnableNatType { get; set; } = true;
    public bool EnableNatCapacity { get; set; } = true;
    public bool EnableMqtt { get; set; } = true;
    public bool EnableSpeedTest { get; set; } = true;
    public string SpeedDurationText { get; set; } = "10";
    public string SpeedDownloadUrl { get; set; } =
        "https://mirrors.cloud.tencent.com/ubuntu/ls-lR.gz, " +
        "https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz, " +
        "https://mirrors.aliyun.com/ubuntu/ls-lR.gz";
    public string SpeedUploadUrl { get; set; } = "https://mirrors.huaweicloud.com/, https://speed.cloudflare.com/__up";

    // —— 运行状态 ——
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnChanged(); OnChanged(nameof(IsIdle)); StartCmd.RaiseCanExecuteChanged(); StopCmd.RaiseCanExecuteChanged(); }
    }
    public bool IsIdle => !IsRunning;

    private double _progress;
    public double Progress { get => _progress; set { _progress = value; OnChanged(); } }

    private string _progressText = "就绪";
    public string ProgressText { get => _progressText; set { _progressText = value; OnChanged(); } }

    private string _logText = "";
    public string LogText { get => _logText; set { _logText = value; OnChanged(); } }

    private string _reportPath = "";
    public string ReportPath { get => _reportPath; set { _reportPath = value; OnChanged(); OnChanged(nameof(HasReport)); } }
    public bool HasReport => !string.IsNullOrEmpty(_reportPath);

    public ObservableCollection<DimensionStatus> Dimensions { get; } = new()
    {
        new DimensionStatus { Key = "dns", Name = "DNS 解析稳定性" },
        new DimensionStatus { Key = "tcp", Name = "TCP 连接稳定性" },
        new DimensionStatus { Key = "udp", Name = "UDP 连通性稳定性" },
        new DimensionStatus { Key = "nat", Name = "NAT 评估" },
        new DimensionStatus { Key = "mqtt", Name = "MQTT 长连接稳定性" },
        new DimensionStatus { Key = "speed", Name = "带宽测速" },
    };

    public RelayCommand StartCmd { get; }
    public RelayCommand StopCmd { get; }
    public RelayCommand OpenReportCmd { get; }

    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        StartCmd = new RelayCommand(_ => _ = StartAsync(), _ => !IsRunning);
        StopCmd = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        OpenReportCmd = new RelayCommand(_ =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReportPath) { UseShellExecute = true }); }
            catch (Exception ex) { AppendLog($"打开报告失败：{ex.Message}"); }
        }, _ => HasReport);
    }

    private static List<string> SplitList(string s) =>
        s.Split(new[] { ',', ';', '，', '；', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
         .Select(x => x.Trim()).ToList();

    private TestConfig BuildConfig()
    {
        var cfg = new TestConfig
        {
            DnsServers = SplitList(DnsServers),
            TestDomains = SplitList(TestDomains),
            TcpTargets = SplitList(TcpTargets),
            StunServers = SplitList(StunServers),
            DurationSeconds = int.TryParse(DurationText, out var d) ? Math.Clamp(d, 5, 3600) : 600,
            Concurrency = int.TryParse(ConcurrencyText, out var c) ? Math.Clamp(c, 1, 32) : 8,
            NatMaxSessions = int.TryParse(NatMaxText, out var n) ? Math.Clamp(n, 100, 2000) : 2000,
            EnableDns = EnableDns, EnableTcp = EnableTcp, EnableUdp = EnableUdp,
            EnableNatType = EnableNatType, EnableNatCapacity = EnableNatCapacity,
            EnableMqtt = EnableMqtt,
            MqttBroker = MqttBroker.Trim(),
            MqttKeepAliveSeconds = int.TryParse(MqttKeepAliveText, out var ka) ? Math.Clamp(ka, 5, 600) : 10,
            EnableSpeedTest = EnableSpeedTest,
            SpeedTestDurationSeconds = int.TryParse(SpeedDurationText, out var sd) ? Math.Clamp(sd, 5, 120) : 10,
            SpeedDownloadUrl = SpeedDownloadUrl.Trim(),
            SpeedUploadUrl = SpeedUploadUrl.Trim(),
        };
        if (cfg.TestDomains.Count == 0) cfg.TestDomains = new TestConfig().TestDomains;
        return cfg;
    }

    private async Task StartAsync()
    {
        foreach (var dim in Dimensions) { dim.Status = "待开始"; dim.Metrics = ""; }
        ReportPath = "";
        Progress = 0;
        LogText = "";
        IsRunning = true;

        var cfg = BuildConfig();
        AppendLog($"配置：压测 {cfg.DurationSeconds}s，并发 {cfg.Concurrency}");

        _cts = new CancellationTokenSource();
        var engine = new TestEngine(cfg);
        engine.Log += s => Application.Current.Dispatcher.Invoke(() => AppendLog(s));
        engine.Metrics += (k, m) => Application.Current.Dispatcher.Invoke(() =>
        {
            var dim = Dimensions.FirstOrDefault(x => x.Key == k);
            if (dim != null) dim.Metrics = m;
        });
        engine.Status += (k, st) => Application.Current.Dispatcher.Invoke(() =>
        {
            var dim = Dimensions.FirstOrDefault(x => x.Key == k);
            if (dim != null) dim.Status = st;
        });
        engine.Progress += p => Application.Current.Dispatcher.Invoke(() =>
        {
            Progress = p;
            ProgressText = $"进行中… {p:F0}%";
        });

        try
        {
            var report = await engine.RunAsync(_cts.Token);
            var path = NetToolbox.Report.ReportGenerator.Write(report);
            ReportPath = path;
            Progress = 100;
            ProgressText = report.Cancelled
                ? $"已中断 · 综合 {report.TotalScore:F0} 分（{report.Grade}）"
                : $"完成 · 综合 {report.TotalScore:F0} 分（{report.Grade}）";
            AppendLog($"报告已生成：{path}");
            foreach (var dim in Dimensions)
                if (dim.Status is "待开始" or "运行中") dim.Status = report.Cancelled ? "已中断" : dim.Status;
        }
        catch (Exception ex)
        {
            ProgressText = "执行出错";
            AppendLog($"错误：{ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogText += $"[{stamp}] {line}{Environment.NewLine}";
        if (LogText.Length > 50_000) LogText = LogText[^40_000..];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string n = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
