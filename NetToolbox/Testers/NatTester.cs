using System.Net;
using System.Net.Sockets;
using NetToolbox.Models;
using NetToolbox.Net;

namespace NetToolbox.Testers;

/// <summary>
/// NAT 评估：
/// 1) 类型探测 —— 同一本地 socket 向两个不同 STUN 服务器请求，比较公网映射地址；
/// 2) 会话容量测试 —— 按梯度并发建立 UDP 会话（每条 socket = 一条 NAT 映射），
///    观察响应成功率骤降的位置，估计 NAT 映射表容量是否充足。
/// </summary>
public class NatTester : ITester
{
    public string Key => "nat";
    public string Name => "NAT 评估";

    public async Task<DimensionResult> RunAsync(TestConfig cfg, Action<string> log, Action<string> metrics, CancellationToken ct)
    {
        var r = new DimensionResult { Key = Key, Name = Name };

        // ---------- 类型探测 ----------
        string natType = "";
        if (cfg.EnableNatType)
        {
            metrics("NAT 类型探测中…");
            natType = await DetectNatType(cfg, log, ct);
            r.Findings.Add($"NAT 类型：{natType}");
        }

        // ---------- 会话容量 ----------
        int estimatedCapacity = -1;
        if (cfg.EnableNatCapacity)
        {
            // 容量测试也逐个尝试 STUN，选第一个可响应的
            IPEndPoint? stun = null;
            foreach (var s in cfg.StunServers)
            {
                var ep = await StunClient.ResolveAsync(s, ct);
                if (ep == null) continue;
                using var probe = new UdpClient(AddressFamily.InterNetwork);
                var (m, _) = await StunClient.BindingAsync(probe, ep, 1500, ct);
                if (m != null) { stun = ep; log($"[NAT] 容量测试使用 STUN {s}（{ep.Address}）"); break; }
            }
            if (stun == null)
            {
                log("[NAT] 所有 STUN 服务器均无响应，容量测试跳过");
                r.Findings.Add("NAT 会话容量：无法评估（STUN 全部不可用，UDP 可能被网络封锁）");
            }
            else
            {
                var levels = new[] { 100, 250, 500, 1000, 2000 }.Where(x => x <= cfg.NatMaxSessions).ToList();
                if (levels.Count == 0) levels.Add(cfg.NatMaxSessions);

                estimatedCapacity = -1;
                foreach (var level in levels)
                {
                    if (ct.IsCancellationRequested) break;
                    metrics($"NAT 容量探测：{level} 并发会话…");
                    log($"[NAT] 容量探测：{level} 并发 UDP 会话");
                    var ok = await ProbeCapacity(stun, level, ct);
                    var rate = 100.0 * ok / level;
                    log($"[NAT] {level} 会话档：响应 {ok}/{level}（{rate:F1}%）");
                    r.Extras.Add(($"{level} 并发会话", $"{rate:F1}%（{ok}/{level}）"));

                    if (rate >= 90) { estimatedCapacity = level; continue; }
                    if (level == levels[0] && rate < 30)
                        log("[NAT] STUN 基础档即几乎无响应（UDP 可能被阻断或服务器限速），容量测试结果不可信");
                    break; // 成功率骤降，认为超过 NAT 表容量
                }
                if (estimatedCapacity > 0)
                    r.Findings.Add($"NAT 会话容量：{(estimatedCapacity == levels[^1] && estimatedCapacity >= cfg.NatMaxSessions ? $"≥ {estimatedCapacity}（充足）" : $"约 {estimatedCapacity}")}");
            }
        }

        if (r.Skipped == false && r.Findings.Count == 0)
            r.Skipped = true;

        // ---------- 评分 ----------
        double typeScore = natType switch
        {
            var s when s.Contains("公网") || s.Contains("无 NAT") => 100,
            var s when s.Contains("Full Cone") => 95,
            var s when s.Contains("Restricted") => 75,
            var s when s.Contains("Symmetric") => 55,
            var s when s.Contains("无法精确判定") => 45,
            var s when s.Contains("不可用") => 30,
            _ => 0
        };
        double capScore = estimatedCapacity switch
        {
            var v when v >= cfg.NatMaxSessions => 100,
            var v when v >= 1000 => 90,
            var v when v >= 500 => 70,
            var v when v >= 250 => 50,
            var v when v > 0 => 30,
            _ => 0
        };
        if (!cfg.EnableNatType && !cfg.EnableNatCapacity) { r.Score = 0; }
        else if (cfg.EnableNatType && cfg.EnableNatCapacity && estimatedCapacity >= 0)
            r.Score = Math.Round(0.6 * typeScore + 0.4 * capScore, 1);
        else if (cfg.EnableNatType) r.Score = Math.Round(typeScore, 1);
        else r.Score = Math.Round(capScore, 1);
        r.Grade = ReportData.GradeOf(r.Score);

        if (natType.Contains("不可用"))
            r.Suggestions.Add("STUN 服务器均无响应：当前网络可能封锁了 UDP 3478 出站流量（企业网/校园网常见）。可换手机热点对比，或在路由器/防火墙上放行 UDP。");
        if (natType.Contains("Symmetric"))
            r.Suggestions.Add("对称型 NAT：P2P 打洞成功率低，游戏联机 / 点对点传输类应用会明显受限；可通过开启路由器 UPnP、使用中继（TURN）或更换路由器缓解。");
        if (natType.Contains("Restricted"))
            r.Suggestions.Add("限制型 NAT：外部主动入站流量受限，需要端口映射（Port Forwarding）才能提供对外服务。");
        if (estimatedCapacity is > 0 and < 500)
            r.Suggestions.Add($"NAT 映射表容量约 {estimatedCapacity} 条，偏小：多设备并发（BT 下载、多开游戏）易出现连接异常，建议重启路由器释放陈旧映射，或更换 NAT 性能更强的路由器。");
        if (estimatedCapacity >= cfg.NatMaxSessions)
            r.Findings.Add($"并发 {estimatedCapacity} 会话仍稳定，NAT 表容量对本机场景充足");

        log($"[NAT] 完成：{natType}{(estimatedCapacity >= 0 ? $"，容量约 {estimatedCapacity}" : "")}");
        return r;
    }

    private static async Task<string> DetectNatType(TestConfig cfg, Action<string> log, CancellationToken ct)
    {
        // 逐个探测候选 STUN 服务器，收集最多 2 个可响应且 IP 不同的服务器
        var stuns = new List<IPEndPoint>();
        var names = new Dictionary<IPEndPoint, string>();
        foreach (var s in cfg.StunServers)
        {
            if (stuns.Count >= 2) break;
            var ep = await StunClient.ResolveAsync(s, ct);
            if (ep == null) { log($"[NAT] STUN {s}：DNS 解析失败"); continue; }
            if (stuns.Any(x => x.Address.Equals(ep.Address))) continue; // 需要不同 IP 才能对比映射行为
            using var probe = new UdpClient(AddressFamily.InterNetwork);
            var (m, _) = await StunClient.BindingAsync(probe, ep, 1500, ct);
            if (m != null) { log($"[NAT] STUN {s}（{ep.Address}）可用，公网映射 {m}"); stuns.Add(ep); names[ep] = s; }
            else log($"[NAT] STUN {s}（{ep.Address}）无响应，尝试下一个");
        }

        if (stuns.Count == 0)
            return "STUN 全部不可用（UDP 3478 可能被网络封锁）";

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);

            var (m1, _) = await StunClient.BindingAsync(udp, stuns[0], 2000, ct);
            if (m1 == null) return "STUN 不可用（响应不稳定）";

            var localIp = StunClient.GetLocalOutboundIp();

            // 仅一个可用 STUN：无法对比映射行为，只能判断有无 NAT
            if (stuns.Count == 1)
            {
                return localIp != null && m1.Address.Equals(localIp)
                    ? "无 NAT（公网直连）"
                    : "无法精确判定类型（仅一个可用 STUN，确认存在 NAT）";
            }

            var (m2, _) = await StunClient.BindingAsync(udp, stuns[1], 2000, ct);
            if (m2 == null) return "STUN 部分不可用（第二服务器无响应）";

            log($"[NAT] 本地出口 {localIp}｜经 {names[stuns[0]]} 映射为 {m1}｜经 {names[stuns[1]]} 映射为 {m2}");

            bool isPrivate = IsPrivateOrCgn(m1.Address);
            if (!isPrivate && localIp != null && m1.Address.Equals(localIp))
                return "无 NAT（公网直连）";

            if (m1.Equals(m2))
                return "Full Cone / Restricted Cone（端点无关映射）";
            return "Symmetric（对称型 NAT）";
        }
        catch (Exception ex)
        {
            log($"[NAT] 类型探测异常: {ex.Message}");
            return "探测失败";
        }
    }

    private static bool IsPrivateOrCgn(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return true;
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127; // CGNAT 100.64.0.0/10
    }

    /// <summary>并发建立 n 条 UDP 会话（各自独立 socket → 独立 NAT 映射），返回收到响应的条数。</summary>
    private static async Task<int> ProbeCapacity(IPEndPoint stun, int n, CancellationToken ct)
    {
        var clients = new List<UdpClient>(n);
        try
        {
            var tasks = new List<Task>(n);
            var okCount = 0;
            for (int i = 0; i < n; i++)
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                clients.Add(udp);
                tasks.Add(Task.Run(async () =>
                {
                    var (_, rtt) = await StunClient.BindingAsync(udp, stun, 3000, ct);
                    if (rtt >= 0) Interlocked.Increment(ref okCount);
                }));
            }
            await Task.WhenAll(tasks);
            return okCount;
        }
        catch { return 0; }
        finally
        {
            foreach (var c in clients) { try { c.Dispose(); } catch { } }
        }
    }
}
