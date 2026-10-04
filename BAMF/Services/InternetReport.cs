using System.Globalization;
using System.Net;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// The internet report: one printable page of what BAMF recorded about the line out of the house, for sending
/// to the provider. Outages where the router kept answering are the line's (the provider's to explain); the
/// ones where the router went quiet too are in the house, and are listed apart so they don't count against the
/// provider. Built as plain HTML with its own light print style, so it prints or saves as a PDF from any browser.
/// </summary>
public static class InternetReport
{
    public sealed record Input(DateTime NowUtc, int Days, string Version, string Target, int IntervalSeconds, string? PublicIp,
        IReadOnlyList<HostStore.WanOutage> Outages, IReadOnlyList<HostStore.WanSlowSpell> Slow, IReadOnlyList<HostStore.SpeedResult> Tests);

    public sealed record Summary(int LineOutages, int LineMinutes, int LongestLineMinutes, int HomeOutages, int HomeMinutes, int SlowSpells,
        int GoodTests, int FailedTests, double? MedianDown, double? MedianUp, double? MinDown, double? MaxDown, double? MedianPing);

    private static bool In(string iso, DateTime from) => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && t.ToUniversalTime() >= from;

    public static Summary Summarize(Input i)
    {
        var from = i.NowUtc.AddDays(-i.Days);
        var line = i.Outages.Where(o => !o.Local && In(o.Start, from)).ToList();
        var home = i.Outages.Where(o => o.Local && In(o.Start, from)).ToList();
        var tests = i.Tests.Where(t => In(t.At, from)).ToList();
        var good = tests.Where(t => t.Error is null && t.DownMbps > 0).ToList();
        return new Summary(line.Count, line.Sum(o => o.Minutes), line.Count == 0 ? 0 : line.Max(o => o.Minutes), home.Count, home.Sum(o => o.Minutes),
            i.Slow.Count(s => In(s.Start, from)), good.Count, tests.Count - good.Count,
            Median(good.Select(t => t.DownMbps)), Median(good.Select(t => t.UpMbps)),
            good.Count == 0 ? null : good.Min(t => t.DownMbps), good.Count == 0 ? null : good.Max(t => t.DownMbps),
            Median(good.Select(t => (double)t.PingMs)));
    }

    private static double? Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Mbps(double? v) => v is { } d ? d.ToString(d >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " Mbps" : "n/a";

    // A time as UTC text that the page turns into the reader's own time zone; the text is what shows if scripts are off.
    private static string When(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? $"<time datetime=\"{E(t.ToUniversalTime().ToString("o"))}\">{E(t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} UTC</time>"
            : E(iso);

    private static string Mins(int m) => m < 60 ? $"{m} min" : $"{m / 60} h {m % 60:00} min";

    public static string Build(Input i)
    {
        var from = i.NowUtc.AddDays(-i.Days);
        var s = Summarize(i);
        var outages = i.Outages.Where(o => In(o.Start, from)).OrderByDescending(o => o.Start, StringComparer.Ordinal).ToList();
        var slow = i.Slow.Where(o => In(o.Start, from)).OrderByDescending(o => o.Start, StringComparer.Ordinal).ToList();
        var tests = i.Tests.Where(t => In(t.At, from)).OrderByDescending(t => t.At, StringComparer.Ordinal).Take(60).ToList();
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Internet connection report</title>
            <style>
              body { font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; color: #1a1f24; background: #fff; max-width: 860px; margin: 0 auto; padding: 24px 20px 48px; }
              h1 { font-size: 26px; margin: 0 0 2px; } h2 { font-size: 17px; margin: 28px 0 8px; border-bottom: 1px solid #cfd6dd; padding-bottom: 4px; }
              .sub { color: #5b6670; } .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 10px; margin: 14px 0; }
              .box { border: 1px solid #cfd6dd; border-radius: 8px; padding: 10px 12px; } .box b { display: block; font-size: 24px; line-height: 1.2; }
              .box span { color: #5b6670; font-size: 13px; } .bad b { color: #b3261e; }
              table { border-collapse: collapse; width: 100%; font-size: 14px; } th, td { text-align: left; padding: 5px 8px; border-bottom: 1px solid #e3e8ed; vertical-align: top; }
              th { color: #5b6670; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
              .none { color: #5b6670; font-style: italic; } .tools { margin: 14px 0 0; } .tools a, .tools button { font: inherit; margin-right: 12px; color: #0b5cad; background: none; border: 0; padding: 0; cursor: pointer; text-decoration: underline; }
              .method { font-size: 13px; color: #3c4650; } .foot { margin-top: 30px; font-size: 12px; color: #5b6670; }
              @media print { .tools { display: none; } body { padding: 0; } h2 { break-after: avoid; } tr { break-inside: avoid; } }
            </style></head><body>
            """);
        sb.Append($"<h1>Internet connection report</h1><div class=\"sub\">{(i.Days == 1 ? "The last day" : $"The last {i.Days} days")}, as recorded by BAMF on the home network{(i.PublicIp is { Length: > 0 } ? $" · public address {E(i.PublicIp)}" : "")}.");
        sb.Append($" <span id=\"gen\">Made {E(i.NowUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} UTC.</span></div>");
        sb.Append("<div class=\"tools\"><button type=\"button\" onclick=\"window.print()\">Print or save as PDF</button>");
        foreach (var d in new[] { 7, 30, 90 }.Where(d => d != i.Days)) sb.Append($"<a href=\"?days={d}\">Last {d} days</a>");
        sb.Append("</div>");

        sb.Append("<div class=\"grid\">");
        sb.Append($"<div class=\"box{(s.LineOutages > 0 ? " bad" : "")}\"><b>{s.LineOutages}</b><span>outage{(s.LineOutages == 1 ? "" : "s")} on the line, {Mins(s.LineMinutes)} in all{(s.LineOutages > 0 ? $", longest {Mins(s.LongestLineMinutes)}" : "")}</span></div>");
        sb.Append($"<div class=\"box\"><b>{s.SlowSpells}</b><span>slow spell{(s.SlowSpells == 1 ? "" : "s")}</span></div>");
        sb.Append($"<div class=\"box\"><b>{Mbps(s.MedianDown)}</b><span>median download, {s.GoodTests} test{(s.GoodTests == 1 ? "" : "s")}{(s.MinDown is { } lo && s.MaxDown is { } hi ? $" ({Mbps(lo)} to {Mbps(hi)})" : "")}</span></div>");
        sb.Append($"<div class=\"box\"><b>{Mbps(s.MedianUp)}</b><span>median upload</span></div>");
        sb.Append("</div>");

        sb.Append("<h2>Outages on the line</h2>");
        var line = outages.Where(o => !o.Local).ToList();
        if (line.Count == 0) sb.Append("<p class=\"none\">None recorded in this period.</p>");
        else
        {
            sb.Append("<p class=\"sub\">The internet stopped answering while the router in the house was still answering, so the break was beyond the house.</p><table><tr><th>Started</th><th>Ended</th><th>Lasted</th></tr>");
            foreach (var o in line) sb.Append($"<tr><td>{When(o.Start)}</td><td>{When(o.End)}</td><td>{Mins(o.Minutes)}</td></tr>");
            sb.Append("</table>");
        }

        var homeOut = outages.Where(o => o.Local).ToList();
        if (homeOut.Count > 0)
        {
            sb.Append("<h2>Outages that also took the router down</h2><p class=\"sub\">Listed for completeness. The router stopped answering too, so these point at the equipment in the house (power, router, cabling) and are not counted against the line.</p><table><tr><th>Started</th><th>Ended</th><th>Lasted</th></tr>");
            foreach (var o in homeOut) sb.Append($"<tr><td>{When(o.Start)}</td><td>{When(o.End)}</td><td>{Mins(o.Minutes)}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<h2>Slow spells</h2>");
        if (slow.Count == 0) sb.Append("<p class=\"none\">None recorded in this period.</p>");
        else
        {
            sb.Append("<p class=\"sub\">The internet answered, but far slower than usual.</p><table><tr><th>Started</th><th>Lasted</th><th>Worst</th><th>Usual</th></tr>");
            foreach (var o in slow) sb.Append($"<tr><td>{When(o.Start)}</td><td>{Mins(o.Minutes)}</td><td>{o.WorstMs} ms</td><td>{o.UsualMs} ms{(o.Local ? " (the router was slow too)" : "")}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<h2>Speed tests</h2>");
        if (tests.Count == 0) sb.Append("<p class=\"none\">No speed tests in this period. Switch them on under Settings → Internet.</p>");
        else
        {
            sb.Append($"<p class=\"sub\">{s.GoodTests} worked{(s.FailedTests > 0 ? $", {s.FailedTests} failed" : "")}. Median ping {(s.MedianPing is { } p ? $"{p:0} ms" : "n/a")}. The newest {tests.Count} are listed.</p>");
            sb.Append("<table><tr><th>When</th><th>Download</th><th>Upload</th><th>Ping</th><th>Server</th></tr>");
            foreach (var t in tests)
                sb.Append(t.Error is null
                    ? $"<tr><td>{When(t.At)}</td><td>{Mbps(t.DownMbps)}</td><td>{Mbps(t.UpMbps)}</td><td>{t.PingMs} ms</td><td>{E(t.Server)}</td></tr>"
                    : $"<tr><td>{When(t.At)}</td><td colspan=\"4\" class=\"none\">Test failed: {E(t.Error)}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append($"<h2>How this was measured</h2><p class=\"method\">BAMF, running on a computer inside the home, pings {E(i.Target)} and the home router about every {i.IntervalSeconds} seconds. ");
        sb.Append("An outage is called after three missed pings in a row and ended after three answers; a slow spell is five readings in a row far above the usual. ");
        sb.Append("Speed tests are run from the same computer to a public test server, so they measure the whole path from the computer, including the home network, and a busy home network can lower them.</p>");
        sb.Append($"<div class=\"foot\">Made by BAMF {E(i.Version)}.</div>");
        sb.Append("""
            <script>
            document.querySelectorAll("time[datetime]").forEach(function (t) {
              var d = new Date(t.getAttribute("datetime"));
              if (!isNaN(d)) t.textContent = d.toLocaleString([], { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
            });
            </script></body></html>
            """);
        return sb.ToString();
    }
}
