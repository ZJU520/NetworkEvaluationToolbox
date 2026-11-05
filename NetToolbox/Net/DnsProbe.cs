using System.IO;
using System.Net;
using System.Net.Sockets;

namespace NetToolbox.Net;

/// <summary>
/// 手写 DNS over UDP 查询探针（A 记录），可指定 DNS 服务器并精确计时。
/// </summary>
public static class DnsProbe
{
    /// <summary>返回解析延迟（毫秒）；失败或超时返回 -1。</summary>
    public static async Task<double> QueryAsync(IPAddress server, string domain, int timeoutMs, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(0, 65536);
        var query = BuildQuery(id, domain);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var ep = new IPEndPoint(server, 53);
            await udp.SendAsync(query, ep, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var resp = await udp.ReceiveAsync(timeoutCts.Token);
            sw.Stop();
            return Validate(id, resp.Buffer) ? sw.Elapsed.TotalMilliseconds : -1;
        }
        catch { return -1; }
    }

    /// <summary>查询域名 A 记录（带系统解析失败的兜底用途），失败返回 null。</summary>
    public static async Task<IPAddress?> QueryAAsync(IPAddress server, string domain, int timeoutMs, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(0, 65536);
        var query = BuildQuery(id, domain);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var ep = new IPEndPoint(server, 53);
            await udp.SendAsync(query, ep, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var resp = await udp.ReceiveAsync(timeoutCts.Token);
            if (!Validate(id, resp.Buffer)) return null;
            var ips = ParseARecords(resp.Buffer);
            return ips.Count > 0 ? ips[0] : null;
        }
        catch { return null; }
    }

    /// <summary>解析响应中的 A 记录列表。</summary>
    private static List<IPAddress> ParseARecords(byte[] d)
    {
        var result = new List<IPAddress>();
        if (d.Length < 12) return result;
        int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7];
        int off = 12;
        for (int i = 0; i < qd; i++) { off = SkipName(d, off); off += 4; }
        for (int i = 0; i < an; i++)
        {
            off = SkipName(d, off);
            if (off + 10 > d.Length) break;
            int type = (d[off] << 8) | d[off + 1];
            int rdlen = (d[off + 8] << 8) | d[off + 9];
            off += 10;
            if (type == 1 && rdlen == 4 && off + 4 <= d.Length)
                result.Add(new IPAddress(d[off..(off + 4)]));
            off += rdlen;
        }
        return result;
    }

    private static int SkipName(byte[] d, int off)
    {
        while (off < d.Length)
        {
            int len = d[off];
            if (len == 0) return off + 1;
            if ((len & 0xC0) == 0xC0) return off + 2; // 域名压缩指针
            off += len + 1;
        }
        return off;
    }

    private static byte[] BuildQuery(ushort id, string domain)
    {
        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v & 0xFF)); }

        W16(id);          // Transaction ID
        W16(0x0100);      // Flags: RD=1
        W16(1);           // QDCOUNT
        W16(0); W16(0); W16(0);

        foreach (var label in domain.TrimEnd('.').Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            if (b.Length > 63) throw new ArgumentException("域名标签过长");
            ms.WriteByte((byte)b.Length);
            ms.Write(b, 0, b.Length);
        }
        ms.WriteByte(0);  // root
        W16(1);           // QTYPE = A
        W16(1);           // QCLASS = IN
        return ms.ToArray();
    }

    private static bool Validate(ushort id, byte[] resp)
    {
        if (resp.Length < 12) return false;
        int respId = (resp[0] << 8) | resp[1];
        if (respId != id) return false;
        int rcode = resp[3] & 0x0F;
        if (rcode != 0) return false;
        int anCount = (resp[6] << 8) | resp[7];
        return anCount > 0;
    }
}
