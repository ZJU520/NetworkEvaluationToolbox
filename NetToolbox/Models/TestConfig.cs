namespace NetToolbox.Models;

/// <summary>测试配置</summary>
public class TestConfig
{
    public List<string> DnsServers { get; set; } = new() { "223.5.5.5", "119.29.29.29", "114.114.114.114" };
    public List<string> TestDomains { get; set; } = new() { "www.baidu.com", "www.qq.com", "www.aliyun.com" };
    public List<string> TcpTargets { get; set; } = new() { "www.baidu.com:443", "www.qq.com:443" };
    public List<string> StunServers { get; set; } = new()
    {
        "stun.chat.bilibili.com:3478", "stun.l.google.com:19302",
        "stun.miwifi.com:3478", "stun.qq.com:3478"
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
    public string MqttBroker { get; set; } = "broker.emqx.io:1883";

    /// <summary>MQTT KeepAlive 秒数（模拟网关心跳周期）</summary>
    public int MqttKeepAliveSeconds { get; set; } = 10;

    public bool EnableMqtt { get; set; } = true;

    // —— 带宽测速 ——
    public bool EnableSpeedTest { get; set; } = true;

    /// <summary>下载/上传各测多久（秒）</summary>
    public int SpeedTestDurationSeconds { get; set; } = 10;

    /// <summary>测速并发连接数</summary>
    public int SpeedTestConnections { get; set; } = 6;

    public string SpeedDownloadUrl { get; set; } = "https://speed.cloudflare.com/__down?bytes=25000000";
    public string SpeedUploadUrl { get; set; } = "https://speed.cloudflare.com/__up";
}
