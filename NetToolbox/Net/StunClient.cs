using System.Net;
using System.Net.Sockets;

namespace NetToolbox.Net;

/// <summary>
/// 简化版 STUN (RFC 5389) Binding Request 客户端。
/// 用于 UDP 连通性测试、NAT 类型探测与 NAT 会话容量测试。
/// </summary>
public static class StunClient
{
    private const int MagicCookie = 0x2112A442;

    /// <summary>解析 STUN 服务器地址 host:port 为 IPEndPoint（系统解析失败时自动走公共 DNS 兜底）。</summary>
    public static Task<IPEndPoint?> ResolveAsync(string hostPort, CancellationToken ct)
        => AddrResolver.ResolveHostPortAsync(hostPort, ct);

    /// <summary>构造 20 字节 Binding Request，返回消息体与事务 ID（12 字节）。</summary>
    public static (byte[] Message, byte[] TxId) BuildBindingRequest()
    {
        var txId = new byte[12];
        Random.Shared.NextBytes(txId);
        var msg = new byte[20];
        msg[0] = 0x00; msg[1] = 0x01;                    // Binding Request
        msg[2] = 0x00; msg[3] = 0x00;                    // Length = 0
        msg[4] = 0x21; msg[5] = 0x12; msg[6] = 0xA4; msg[7] = 0x42; // Magic Cookie
        Array.Copy(txId, 0, msg, 8, 12);
        return (msg, txId);
    }

    /// <summary>
    /// 在指定 UdpClient 上向服务器发送 Binding Request 并等待响应。
    /// 返回 (映射地址, RTT毫秒)；失败返回 (null, -1)。
    /// </summary>
    public static async Task<(IPEndPoint? Mapped, double RttMs)> BindingAsync(
        UdpClient udp, IPEndPoint server, int timeoutMs, CancellationToken ct)
    {
        var (msg, txId) = BuildBindingRequest();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(msg, server, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            // 循环读取，丢弃不匹配的响应（同一 socket 上可能有杂散包）
            while (true)
            {
                var resp = await udp.ReceiveAsync(timeoutCts.Token);
                var mapped = ParseBindingResponse(resp.Buffer, txId);
                if (mapped != null)
                {
                    sw.Stop();
                    return (mapped, sw.Elapsed.TotalMilliseconds);
                }
            }
        }
        catch { return (null, -1); }
    }

    /// <summary>解析 Binding Response，返回 XOR-MAPPED-ADDRESS 或 MAPPED-ADDRESS。</summary>
    public static IPEndPoint? ParseBindingResponse(byte[] data, byte[] expectedTxId)
    {
        if (data.Length < 20) return null;
        if (data[0] != 0x01 || data[1] != 0x01) return null;   // Binding Response
        for (int i = 0; i < 12; i++)
            if (data[8 + i] != expectedTxId[i]) return null;
        int msgLen = (data[2] << 8) | data[3];

        int offset = 20;
        IPEndPoint? fallback = null;
        while (offset + 4 <= Math.Min(data.Length, 20 + msgLen))
        {
            int attrType = (data[offset] << 8) | data[offset + 1];
            int attrLen = (data[offset + 2] << 8) | data[offset + 3];
            int valStart = offset + 4;
            if (valStart + attrLen > data.Length) break;

            if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8 && data[valStart + 1] == 0x01)
            {
                int port = (data[valStart + 2] << 8) | data[valStart + 3];
                var ipBytes = new byte[4];
                Array.Copy(data, valStart + 4, ipBytes, 0, 4);
                if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                {
                    port ^= MagicCookie >> 16;
                    for (int i = 0; i < 4; i++)
                        ipBytes[i] ^= (byte)(MagicCookie >> (24 - i * 8));
                    return new IPEndPoint(new IPAddress(ipBytes), port);
                }
                fallback = new IPEndPoint(new IPAddress(ipBytes), port);
            }
            offset = valStart + ((attrLen + 3) & ~3); // 4 字节对齐
        }
        return fallback;
    }

    /// <summary>获取本机出口 IP（UDP connect 技巧，不产生真实流量）。</summary>
    public static IPAddress? GetLocalOutboundIp()
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(new IPAddress(new byte[] { 8, 8, 8, 8 }), 80);
            return ((IPEndPoint)udp.Client.LocalEndPoint!).Address;
        }
        catch { return null; }
    }
}
