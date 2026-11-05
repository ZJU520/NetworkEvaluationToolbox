using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetToolbox.Engine;
using NetToolbox.Models;
using NetToolbox.Net;

namespace NetToolbox.Testers;

/// <summary>
/// MQTT 长连接稳定性：用极简 MQTT 3.1.1 客户端真实连接 broker，
/// 按 KeepAlive 周期发送 PINGREQ 心跳，统计断线次数 / 存活时长 / 心跳 RTT / 重连耗时。
/// 直接回答"该网络是否具备 MQTT 长连接稳定在线的能力"。
/// 注：仅支持 TCP 明文端口（1883）；8883/TLS 的认证判定请以 TCP 连通性为准。
/// </summary>
public class MqttStabilityTester : ITester
{
    public string Key => "mqtt";
    public string Name => "MQTT 长连接稳定性";

    public async Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct)
    {
        var r = new DimensionResult { Key = Key, Name = Name };

        // 解析 broker
        var broker = string.IsNullOrWhiteSpace(cfg.MqttBroker) ? "broker.emqx.io:1883" : cfg.MqttBroker.Trim();
        var idx = broker.LastIndexOf(':');
        var host = idx > 0 ? broker[..idx] : broker;
        var port = idx > 0 && int.TryParse(broker[(idx + 1)..], out var p) ? p : 1883;

        IPAddress ip;
        var resolved = await AddrResolver.ResolveIpAsync(host, ct); // 系统解析失败自动走公共 DNS 兜底
        if (resolved == null) { log($"[MQTT] 无法解析 {host}"); r.Skipped = true; return r; }
        ip = resolved;

        var endpoint = new IPEndPoint(ip, port);
        var keepAlive = Math.Clamp(cfg.MqttKeepAliveSeconds, 5, 600);
        var pingInterval = Math.Clamp(keepAlive / 2, 5, 60); // 在 keepalive×1.5 容忍期内
        var deadline = DateTime.UtcNow.AddSeconds(cfg.DurationSeconds);

        log($"[MQTT] 开始：{host}:{port}（{ip}），KeepAlive={keepAlive}s，心跳间隔 {pingInterval}s，持续 {cfg.DurationSeconds}s");

        int connectCount = 0, drops = 0, connackRc = -1;
        var lifetimes = new List<double>();
        var reconnectMs = new List<double>();
        double? lastDropAt = null;
        var startTime = DateTime.UtcNow;

        var reporter = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
                lock (r.Samples)
                {
                    var ok = r.Samples.Count(x => x.Success);
                    var avg = ok > 0 ? r.Samples.Where(x => x.Success).Average(x => x.LatencyMs) : 0;
                    metrics($"断线 {drops} 次｜心跳成功率 {(r.Samples.Count == 0 ? 100 : 100.0 * ok / r.Samples.Count):F1}%｜平均 RTT {avg:F0}ms｜心跳 {r.Samples.Count} 次");
                }
            }
        });

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            NetworkStream? ns = null;
            var connStart = DateTime.UtcNow;
            try
            {
                // —— 建立 TCP + MQTT CONNECT ——
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var cto = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cto.CancelAfter(5000);
                    await tcp.ConnectAsync(endpoint, cto.Token);
                }
                ns = tcp.GetStream();
                var cid = $"nettoolbox-{Guid.NewGuid():N}"[..23];
                var connect = BuildConnect(cid, (ushort)keepAlive);
                await ns.WriteAsync(connect, ct);
                var connack = await ReadPacketAsync(ns, 5000, ct);
                sw.Stop();

                if (connack == null || connack.Value.Header != 0x20 || connack.Value.Body.Length < 2)
                {
                    log("[MQTT] CONNACK 无效或未收到");
                    drops++;
                    lifetimes.Add((DateTime.UtcNow - connStart).TotalSeconds);
                    continue;
                }
                connackRc = connack.Value.Body[1];
                if (connackRc != 0)
                {
                    log($"[MQTT] 平台拒绝登录，CONNACK rc={connackRc}（{ConnackText(connackRc)}）——这是认证/权限问题，不是网络问题");
                    break; // 认证问题重试无意义
                }

                connectCount++;
                if (lastDropAt.HasValue)
                {
                    var reconMs = sw.Elapsed.TotalMilliseconds;
                    reconnectMs.Add(reconMs);
                    log($"[MQTT] 重连成功，耗时 {reconMs:F0}ms（第 {connectCount} 次连接）");
                    lastDropAt = null;
                }
                else log($"[MQTT] 已连接并登录成功（建连+CONNACK {sw.Elapsed.TotalMilliseconds:F0}ms）");

                // —— 心跳保活循环 ——
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    var wait = pingInterval * 1000;
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { break; }
                    if (DateTime.UtcNow >= deadline) break;

                    var pingSw = System.Diagnostics.Stopwatch.StartNew();
                    await ns.WriteAsync(new byte[] { 0xC0, 0x00 }, ct); // PINGREQ
                    var resp = await ReadPacketAsync(ns, Math.Max(5000, (int)(pingInterval * 1500)), ct);
                    pingSw.Stop();

                    if (resp != null && resp.Value.Header == 0xD0)
                    {
                        lock (r.Samples) r.Samples.Add(new MetricSample(DateTime.Now, pingSw.Elapsed.TotalMilliseconds));
                    }
                    else
                    {
                        // 区分断开方式
                        string reason = tcp.Connected ? "心跳超时（疑似 NAT 会话被回收或链路丢包）" : "连接被对端/中间设备关闭";
                        log($"[MQTT] 断线：{reason}，本次存活 {(DateTime.UtcNow - connStart).TotalSeconds:F0}s");
                        lock (r.Samples) r.Samples.Add(new MetricSample(DateTime.Now, -1));
                        drops++;
                        lifetimes.Add((DateTime.UtcNow - connStart).TotalSeconds);
                        lastDropAt = 0;
                        break; // 进入重连
                    }
                }

                if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested)
                {
                    lifetimes.Add((DateTime.UtcNow - connStart).TotalSeconds);
                    try { await ns.WriteAsync(new byte[] { 0xE0, 0x00 }, CancellationToken.None); } catch { } // DISCONNECT
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                drops++;
                lifetimes.Add((DateTime.UtcNow - connStart).TotalSeconds);
                log($"[MQTT] 连接异常：{ex.Message}");
                lastDropAt = 0;
            }
        }

        await reporter;

        // —— 汇总 ——
        var okLat = r.Samples.Where(x => x.Success).Select(x => x.LatencyMs).ToList();
        r.TotalCount = r.Samples.Count;
        r.SuccessCount = okLat.Count;
        (r.AvgMs, r.P50Ms, r.P95Ms, r.P99Ms, r.JitterMs) = StatsCalc.Compute(okLat);

        var pingRate = r.TotalCount == 0 ? 100.0 : 100.0 * r.SuccessCount / r.TotalCount;
        var avgLife = lifetimes.Count > 0 ? lifetimes.Average() : 0;
        var maxLife = lifetimes.Count > 0 ? lifetimes.Max() : 0;
        var avgRecon = reconnectMs.Count > 0 ? reconnectMs.Average() : 0;

        r.Extras.Add(("Broker", $"{host}:{port}"));
        r.Extras.Add(("KeepAlive / 心跳间隔", $"{keepAlive}s / {pingInterval}s"));
        r.Extras.Add(("连接次数 / 断线次数", $"{connectCount} / {drops}"));
        r.Extras.Add(("最长存活 / 平均存活", $"{maxLife:F0}s / {avgLife:F0}s"));
        if (reconnectMs.Count > 0) r.Extras.Add(("平均重连耗时", $"{avgRecon:F0}ms"));
        if (connackRc >= 0) r.Extras.Add(("CONNACK", connackRc == 0 ? "接受（登录成功）" : $"rc={connackRc} {ConnackText(connackRc)}"));

        if (connackRc > 0)
        {
            r.Findings.Add($"平台拒绝登录（CONNACK rc={connackRc}：{ConnackText(connackRc)}）——" + "网络链路是通的，请检查网关的 ClientID/用户名/密码或平台侧的设备注册状态");
            r.Score = 20; // 网络通但被拒，责任不在网络
        }
        else if (connectCount == 0)
        {
            r.Findings.Add("整个测试期间未能成功登录 broker 一次——建连失败或无响应，该网络不具备 MQTT 接入条件");
            r.Score = 5;
        }
        else
        {
            r.Findings.Add($"测试 {cfg.DurationSeconds}s：连接 {connectCount} 次、断线 {drops} 次、最长存活 {maxLife:F0}s，心跳成功率 {pingRate:F1}%、平均 RTT {r.AvgMs:F0}ms");

            // 评分：连接稳定 60 + 心跳成功率 25 + RTT 15
            double score = drops switch { 0 => 60, 1 => 45, 2 => 32, 3 => 20, _ => 8 };
            score += 25.0 * pingRate / 100;
            score += r.AvgMs switch { < 50 => 15, < 150 => 12, < 300 => 8, _ => 4 };
            r.Score = Math.Round(Math.Max(0, score), 1);

            if (drops == 0 && pingRate >= 99)
                r.Findings.Add("长连接全程无中断，该网络具备 MQTT/网关长在线能力");
            if (drops >= 1)
                r.Suggestions.Add($"长连接中断 {drops} 次：最常见原因是 NAT/防火墙空闲会话回收或链路丢包导致心跳超时被 broker 踢下线。建议：① 网关侧把 MQTT KeepAlive 调小到 30s 以内；② 开启 TCP keepalive（30s 级）；③ 断线自动重连 + 指数退避；④ 条件允许时联系运营商/网管确认会话超时策略。");
            if (pingRate < 95)
                r.Suggestions.Add("心跳丢包明显：链路存在丢包，实时性业务会感知卡顿/掉线，建议排查无线质量、联系 ISP。");
            if (r.AvgMs > 200)
                r.Suggestions.Add("心跳 RTT 偏高：broker 距离远或跨运营商/跨境，建议选用就近接入点或专线。");
            if (port == 8883)
                r.Suggestions.Add("注意：本测试走 TCP 明文模拟 MQTT 交互，8883/TLS 场景请以本工具 TCP 维度 + 本结果的建连部分为准。");
        }
        r.Grade = ReportData.GradeOf(r.Score);
        log($"[MQTT] 完成：连接 {connectCount} 次，断线 {drops} 次，心跳成功率 {pingRate:F1}%");
        return r;
    }

    private static string ConnackText(int rc) => rc switch
    {
        1 => "协议版本不接受",
        2 => "ClientID 被拒",
        3 => "服务端不可用",
        4 => "用户名或密码错误",
        5 => "未授权",
        _ => "未知"
    };

    private static byte[] BuildConnect(string clientId, ushort keepAlive)
    {
        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v & 0xFF)); }
        var name = Encoding.ASCII.GetBytes("MQTT");
        W16(name.Length); ms.Write(name, 0, name.Length);
        ms.WriteByte(4);     // MQTT 3.1.1
        ms.WriteByte(0x02);  // Clean Session
        W16(keepAlive);
        var cid = Encoding.ASCII.GetBytes(clientId);
        W16(cid.Length); ms.Write(cid, 0, cid.Length);
        var body = ms.ToArray();

        using var pkt = new MemoryStream();
        pkt.WriteByte(0x10);
        // remaining length varint
        int rl = body.Length;
        do
        {
            var d = (byte)(rl % 128);
            rl /= 128;
            if (rl > 0) d |= 0x80;
            pkt.WriteByte(d);
        } while (rl > 0);
        pkt.Write(body, 0, body.Length);
        return pkt.ToArray();
    }

    private static async Task<(byte Header, byte[] Body)?> ReadPacketAsync(NetworkStream ns, int timeoutMs, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(timeoutMs);
        try
        {
            var h = new byte[1];
            if (await ReadExact(ns, h, 1, t.Token) < 1) return null;

            // remaining length varint
            int multiplier = 1, len = 0, rlBytes = 0;
            byte digit;
            do
            {
                var b = new byte[1];
                if (await ReadExact(ns, b, 1, t.Token) < 1) return null;
                digit = b[0];
                len += (digit & 0x7F) * multiplier;
                multiplier *= 128;
                if (++rlBytes > 4) return null;
            } while ((digit & 0x80) != 0);

            var body = new byte[len];
            if (len > 0 && await ReadExact(ns, body, len, t.Token) < len) return null;
            return (h[0], body);
        }
        catch { return null; }
    }

    private static async Task<int> ReadExact(NetworkStream ns, byte[] buf, int n, CancellationToken ct)
    {
        int got = 0;
        while (got < n)
        {
            var r = await ns.ReadAsync(buf.AsMemory(got, n - got), ct);
            if (r == 0) return got;
            got += r;
        }
        return got;
    }
}
