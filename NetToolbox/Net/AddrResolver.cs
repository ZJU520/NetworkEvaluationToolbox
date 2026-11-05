using System.Net;
using System.Net.Sockets;
using NetToolbox.Models;

namespace NetToolbox.Net;

/// <summary>
/// 域名解析（带兜底）：先走 Windows 系统解析，
/// 失败则直连公共 DNS（223.5.5.5 / 119.29.29.29）查询 A 记录。
/// 保证系统 DNS 配置异常（如被设为 8.8.8.8 导致超时）时测试仍能运行。
/// </summary>
public static class AddrResolver
{
    public static async Task<IPAddress?> ResolveIpAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (IPAddress.TryParse(host.Trim(), out var direct)) return direct;

        // 1) 系统 DNS
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host.Trim(), AddressFamily.InterNetwork, ct);
            if (addrs.Length > 0) return addrs[0];
        }
        catch { /* 走兜底 */ }

        // 2) 直连公共 DNS
        foreach (var s in new[] { "223.5.5.5", "119.29.29.29" })
        {
            if (IPAddress.TryParse(s, out var dns))
            {
                var ip = await DnsProbe.QueryAAsync(dns, host.Trim(), 1500, ct);
                if (ip != null) return ip;
            }
        }
        return null;
    }

    /// <summary>解析 host:port（兼容纯 IP）。</summary>
    public static async Task<IPEndPoint?> ResolveHostPortAsync(string hostPort, CancellationToken ct)
    {
        var idx = hostPort.LastIndexOf(':');
        if (idx <= 0) return null;
        var host = hostPort[..idx];
        if (!int.TryParse(hostPort[(idx + 1)..], out var port)) return null;
        var ip = await ResolveIpAsync(host, ct);
        return ip == null ? null : new IPEndPoint(ip, port);
    }
}
