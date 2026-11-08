namespace NetToolbox.Models;

/// <summary>测试配置</summary>
public class TestConfig
{
    public List<string> DnsServers { get; set; } = new() { "223.5.5.5", "119.29.29.29", "114.114.114.114" };
    public List<string> TestDomains { get; set; } = new() { "www.baidu.com", "www.qq.com", "www.aliyun.com" };
    public List<string> TcpTargets { get; set; } = new() { "www.baidu.com:443", "www.qq.com:443", "www.taobao.com:443" };
    // 国内优先：bilibili 的多个节点 IP 即可完成 NAT 类型比对；境外仅作最后兜底
    public List<string> StunServers { get; set; } = new()
    {
        "stun.chat.bilibili.com:3478",
        "stun.l.google.com:19302", "stun.cloudflare.com:3478", "global.stun.twilio.com:3478"
    };

    /// <summary>压测时长（秒）</summary>
    public int DurationSeconds { get; set; } = 600;

    /// <summary>并发工作线程数（DNS/TCP 各维度）</summary>
    public int Concurrency { get; set; } = 8;

    /// <summary>NAT 容量探测的最大并发会话数</summary>
    public int NatMaxSessions { get; set; } = 2000;

    public bool EnableDns { get; set; } = true;
    public bool EnableTcp { get; set; } = true;
    public bool EnableUdp { get; set; } = true;
    public bool EnableNatType { get; set; } = true;
    public bool EnableNatCapacity { get; set; } = true;

    /// <summary>MQTT broker 地址 host:port（TCP 明文，如你们的 IoT 平台接入地址）</summary>
    public string MqttBroker { get; set; } = "broker-cn.emqx.io:1883";

    /// <summary>MQTT KeepAlive 秒数（模拟网关心跳周期）</summary>
    public int MqttKeepAliveSeconds { get; set; } = 10;

    public bool EnableMqtt { get; set; } = true;

    // —— 带宽测速 ——
    public bool EnableSpeedTest { get; set; } = true;

    /// <summary>下载/上传各测多久（秒）</summary>
    public int SpeedTestDurationSeconds { get; set; } = 10;

    /// <summary>测速并发连接数</summary>
    public int SpeedTestConnections { get; set; } = 6;

    /// <summary>下载测速候选源（逗号分隔，运行时自动选最快的一个）。默认全部为国内镜像。</summary>
    public string SpeedDownloadUrl { get; set; } =
        "https://mirrors.cloud.tencent.com/ubuntu/ls-lR.gz, " +
        "https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz, " +
        "https://mirrors.aliyun.com/ubuntu/ls-lR.gz, " +
        "https://registry.npmmirror.com/-/binary/node/v20.11.0/node-v20.11.0-linux-x64.tar.gz";

    /// <summary>上传测速候选端点（逗号分隔，运行时自动选可用的）。国内镜像在前，Cloudflare 兜底。</summary>
    public string SpeedUploadUrl { get; set; } =
        "https://mirrors.huaweicloud.com/, https://speed.cloudflare.com/__up";
}
