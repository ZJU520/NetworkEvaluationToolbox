using System.IO;
using System.Text;
using NetToolbox.Models;

namespace NetToolbox.Report;

/// <summary>生成自包含 HTML 评估报告（零外部依赖，图表为内联 SVG）。</summary>
public static class ReportGenerator
{
    public static string Write(ReportData data)
    {
        var html = BuildHtml(data);
        var dir = Path.Combine(AppContext.BaseDirectory, "Reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"网络评估报告_{data.StartTime:yyyyMMdd_HHmmss}.html");
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    private static string BuildHtml(ReportData d)
    {
        var sb = new StringBuilder(64 * 1024);
        var scoreColor = ScoreColor(d.TotalScore);

        sb.Append("""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>网络稳定性评估报告</title>
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: "Microsoft YaHei", "Segoe UI", sans-serif; background: #f5f7fa; color: #1f2933; padding: 32px; }
  .wrap { max-width: 960px; margin: 0 auto; }
  .card { background: #fff; border-radius: 12px; box-shadow: 0 1px 4px rgba(31,41,51,.08); padding: 24px; margin-bottom: 20px; }
  h1 { font-size: 22px; }
  .muted { color: #6b7280; }
  .meta { color: #6b7280; font-size: 13px; margin-top: 6px; line-height: 1.8; }
  .hero { display: flex; gap: 40px; align-items: center; flex-wrap: wrap; }
  .grade-big { font-size: 34px; font-weight: 700; }
  .dim-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
  @media (max-width: 800px){ .dim-grid { grid-template-columns: 1fr; } }
  .dim { border: 1px solid #e5e7eb; border-radius: 10px; padding: 18px; }
  .dim h3 { font-size: 16px; display: flex; justify-content: space-between; align-items: center; }
  .badge { font-size: 12px; padding: 2px 10px; border-radius: 999px; color: #fff; }
  .bar { height: 8px; background: #eef0f3; border-radius: 4px; margin: 12px 0; overflow: hidden; }
  .bar > div { height: 100%; border-radius: 4px; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  td, th { padding: 6px 10px; border-bottom: 1px solid #f0f1f3; text-align: left; }
  th { color: #6b7280; font-weight: 600; }
  ul { margin: 8px 0 0 20px; font-size: 13px; line-height: 1.9; }
  .findings li { color: #1f2933; }
  .sug li { color: #8a5a00; }
  .chart-title { font-size: 14px; font-weight: 600; margin: 0 0 8px 0; }
  .skipped { color: #9ca3af; }
  footer { text-align: center; color: #9ca3af; font-size: 12px; padding: 12px; }
</style>
</head>
<body><div class="wrap">
""");

        // —— 总览卡片 ——
        sb.Append("""
<section class="card hero">
  <div>
""");
        sb.Append(Gauge(d.TotalScore, scoreColor));
        sb.Append($"""
  </div>
  <div style="flex:1; min-width:260px;">
    <h1>网络稳定性评估报告</h1>
    <div class="grade-big" style="color:{scoreColor};">{d.Grade} · {d.TotalScore:F0} 分</div>
    <div class="meta">
      测试时间：{d.StartTime:yyyy-MM-dd HH:mm:ss} ~ {d.EndTime:HH:mm:ss}（共 {(d.EndTime - d.StartTime).TotalSeconds:F0} 秒）<br>
      压测时长：{d.Config.DurationSeconds} 秒｜并发：{d.Config.Concurrency}<br>
      综合评分 = DNS 25% + TCP 30% + UDP 20% + NAT 25% + MQTT 30% + 带宽 20%（按实际启用维度归一化）{(d.Cancelled ? "｜<b style='color:#b45309'>测试被提前中断，结果基于部分数据</b>" : "")}
    </div>
  </div>
</section>
""");

        // —— 维度卡片 ——
        sb.Append("<section class=\"card\"><h1 style=\"margin-bottom:16px;\">维度评分</h1><div class=\"dim-grid\">");
        foreach (var dim in Order(d))
        {
            var c = ScoreColor(dim.Score);
            sb.Append($"""
<div class="dim">
  <h3>{dim.Name}{(dim.Skipped ? "" : $"<span class=\"badge\" style=\"background:{c}\">{dim.Score:F0} 分</span>")}</h3>
""");
            if (dim.Skipped)
            {
                sb.Append("<p class=\"skipped\" style=\"font-size:13px;margin-top:8px;\">未启用或目标不可用，已跳过</p>");
            }
            else
            {
                sb.Append($"<div class=\"bar\"><div style=\"width:{Math.Clamp(dim.Score, 0, 100):F0}%;background:{c};\"></div></div>");
                sb.Append("<table>");
                if (dim.TotalCount > 0)
                {
                    sb.Append($"<tr><th>成功率</th><td>{dim.SuccessRate:F1}%（{dim.SuccessCount}/{dim.TotalCount}）</td></tr>");
                    sb.Append($"<tr><th>平均延迟</th><td>{dim.AvgMs:F0} ms</td></tr>");
                    sb.Append($"<tr><th>P50 / P95 / P99</th><td>{dim.P50Ms:F0} / {dim.P95Ms:F0} / {dim.P99Ms:F0} ms</td></tr>");
                    sb.Append($"<tr><th>抖动</th><td>{dim.JitterMs:F1} ms</td></tr>");
                }
                foreach (var (k, v) in dim.Extras)
                    sb.Append($"<tr><th>{Escape(k)}</th><td>{Escape(v)}</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("</div>");
        }
        sb.Append("</div></section>");

        // —— 结论与建议 ——
        sb.Append("<section class=\"card\"><h1 style=\"margin-bottom:12px;\">结论与建议</h1>");
        foreach (var dim in Order(d))
        {
            if (dim.Skipped || (dim.Findings.Count == 0 && dim.Suggestions.Count == 0)) continue;
            sb.Append($"<h3 style=\"margin-top:10px;\">{dim.Name}</h3>");
            if (dim.Findings.Count > 0)
            {
                sb.Append("<ul class=\"findings\">");
                foreach (var f in dim.Findings) sb.Append($"<li>{Escape(f)}</li>");
                sb.Append("</ul>");
            }
            if (dim.Suggestions.Count > 0)
            {
                sb.Append("<ul class=\"sug\">");
                foreach (var s in dim.Suggestions) sb.Append($"<li>💡 {Escape(s)}</li>");
                sb.Append("</ul>");
            }
        }
        sb.Append("</section>");

        // —— 延迟时序图 ——
        var chartDims = d.Dimensions.Where(x => !x.Skipped && x.Samples.Count > 1).ToList();
        if (chartDims.Count > 0)
        {
            sb.Append("<section class=\"card\"><h1 style=\"margin-bottom:16px;\">延迟时序</h1>");
            foreach (var dim in chartDims)
                sb.Append($"<p class=\"chart-title\">{dim.Name}</p>{LatencyChart(dim)}<div style=\"height:18px;\"></div>");
            sb.Append("</section>");
        }

        sb.Append($"""
<footer>NetToolbox 网络测试工具箱 · 本报告由本机探测数据生成，结论仅供网络质量评估参考</footer>
</div></body></html>
""");
        return sb.ToString();
    }

    private static IEnumerable<DimensionResult> Order(ReportData d)
    {
        var order = new[] { "dns", "tcp", "udp", "nat", "mqtt", "speed" };
        return d.Dimensions.OrderBy(x => Array.IndexOf(order, x.Key));
    }

    private static string ScoreColor(double score) =>
        score >= 90 ? "#0e9f6e" : score >= 75 ? "#3b82f6" : score >= 60 ? "#d97706" : "#dc2626";

    /// <summary>总分环形仪表（SVG）</summary>
    private static string Gauge(double score, string color)
    {
        const double r = 52;
        const double c = 2 * Math.PI * r;
        var dash = Math.Clamp(score, 0, 100) / 100 * c;
        return $"""
<svg width="140" height="140" viewBox="0 0 140 140">
  <circle cx="70" cy="70" r="{r}" fill="none" stroke="#eef0f3" stroke-width="12"/>
  <circle cx="70" cy="70" r="{r}" fill="none" stroke="{color}" stroke-width="12" stroke-linecap="round"
          stroke-dasharray="{dash:F1} {c - dash:F1}" transform="rotate(-90 70 70)"/>
  <text x="70" y="66" text-anchor="middle" font-size="30" font-weight="700" fill="#1f2933">{score:F0}</text>
  <text x="70" y="90" text-anchor="middle" font-size="13" fill="#6b7280">综合得分</text>
</svg>
""";
    }

    /// <summary>延迟时序 SVG 折线图：成功样本为折线，失败样本为底部红色刻点。</summary>
    private static string LatencyChart(DimensionResult dim)
    {
        const int w = 880, h = 190, padL = 46, padR = 10, padT = 14, padB = 24;
        var samples = dim.Samples;
        var n = samples.Count;

        var okMax = samples.Where(x => x.Success).Select(x => x.LatencyMs).DefaultIfEmpty(1).Max();
        var yMax = Math.Max(10, Math.Ceiling(okMax * 1.15 / 10) * 10);

        double X(int i) => padL + (n == 1 ? 0 : (double)i / (n - 1) * (w - padL - padR));
        double Y(double ms) => padT + (h - padT - padB) * (1 - Math.Min(ms, yMax) / yMax);

        var sb = new StringBuilder(16 * 1024);
        sb.Append($"""<svg width="100%" viewBox="0 0 {w} {h}" style="max-width:{w}px;font-family:inherit;">""");

        // 网格与 Y 轴
        for (int g = 0; g <= 4; g++)
        {
            var y = padT + (h - padT - padB) * g / 4.0;
            var val = yMax * (1 - (double)g / 4);
            sb.Append($"""<line x1="{padL}" y1="{y:F1}" x2="{w - padR}" y2="{y:F1}" stroke="#f0f1f3"/>""");
            sb.Append($"""<text x="{padL - 6}" y="{y + 4:F1}" text-anchor="end" font-size="11" fill="#9ca3af">{val:F0}</text>""");
        }
        sb.Append($"""<text x="{padL - 6}" y="{padT - 3}" text-anchor="end" font-size="10" fill="#9ca3af">ms</text>""");

        // 成功样本折线
        var sbPts = new StringBuilder();
        bool first = true;
        for (int i = 0; i < n; i++)
        {
            if (!samples[i].Success) continue;
            sbPts.Append($"{(first ? "M" : "L")}{X(i):F1},{Y(samples[i].LatencyMs):F1} ");
            first = false;
        }
        if (!first)
            sb.Append($"""<path d="{sbPts}" fill="none" stroke="#3b82f6" stroke-width="1.5" stroke-linejoin="round"/>""");

        // 失败刻点
        int failures = 0;
        for (int i = 0; i < n; i++)
        {
            if (samples[i].Success) continue;
            failures++;
            sb.Append($"""<rect x="{Math.Max(0, X(i) - 0.8):F1}" y="{h - padB}" width="1.6" height="8" fill="#dc2626"/>""");
        }

        // X 轴注释
        sb.Append($"""<line x1="{padL}" y1="{h - padB}" x2="{w - padR}" y2="{h - padB}" stroke="#d1d5db"/>""");
        sb.Append($"""<text x="{padL}" y="{h - 6}" font-size="11" fill="#9ca3af">开始</text>""");
        sb.Append($"""<text x="{w - padR}" y="{h - 6}" text-anchor="end" font-size="11" fill="#9ca3af">结束（{n} 样本）</text>""");
        if (failures > 0)
            sb.Append($"""<text x="{(w + padL) / 2}" y="{h - 6}" text-anchor="middle" font-size="11" fill="#dc2626">■ {failures} 次失败/超时</text>""");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
