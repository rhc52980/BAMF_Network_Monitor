using System.Globalization;

namespace LanWatch.Services;

/// <summary>
/// A period of the network's life as numbers, so two of them can be set side by side: this week against last, this month against the
/// one before. Everything comes from what BAMF already keeps: when devices arrived and dropped, the internet's outages and slow spells,
/// the bytes moved, the alerts raised, the unusual findings, the ports that opened, and the score's daily readings.
/// </summary>
public partial class HostStore
{
    public sealed record PeriodStats(string From, string To, int Devices, int Arrived, int Drops, int Outages, int OutageMinutes, int SlowSpells, int SlowMinutes,
        long Bytes, int Alerts, int SecurityAlerts, int Unusual, int PortsOpened);

    /// <summary>One number compared: what it was over the period and the one before, and which direction is the good one (up, down, or neither).</summary>
    public sealed record CompareMetric(string Key, string Label, double Now, double Before, string Unit, string? Better);

    /// <summary>What happened between two moments. Ignored and forgotten devices are left out.</summary>
    public PeriodStats Period(DateTime fromUtc, DateTime toUtc)
    {
        var from = fromUtc.ToString("o"); var to = toUtc.ToString("o");
        var fromHour = fromUtc.ToString("yyyy-MM-ddTHH:00:00Z"); var toHour = toUtc.ToString("yyyy-MM-ddTHH:00:00Z");
        var all = GetAll().Where(h => !h.Forgotten && !h.Ignored).ToList();
        var ids = all.Select(h => h.Id).ToHashSet();
        var devices = all.Count(h => string.CompareOrdinal(h.FirstSeen, to) < 0);
        var arrived = all.Count(h => string.CompareOrdinal(h.FirstSeen, from) >= 0 && string.CompareOrdinal(h.FirstSeen, to) < 0);
        int drops = 0, unusual = 0, portsOpened = 0; long bytes = 0;
        lock (_lock)
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT e.host_id FROM events e WHERE e.type = 'offline' AND e.at >= $from AND e.at < $to";
                cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (ids.Contains(r.GetInt64(0))) drops++;
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(rx + tx), 0) FROM traffic_hourly WHERE hour >= $from AND hour < $to";
                cmd.Parameters.AddWithValue("$from", fromHour); cmd.Parameters.AddWithValue("$to", toHour);
                bytes = Convert.ToInt64(cmd.ExecuteScalar());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT host_id FROM unusual WHERE at >= $from AND at < $to";
                cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (ids.Contains(r.GetInt64(0))) unusual++;
            }
            using (var cmd = conn.CreateCommand())
            {
                // a port that opened: first found in the period, and not on the device's very first scan
                cmd.CommandText = """
                    SELECT p.host_id FROM host_ports p
                    WHERE p.first_seen >= $from AND p.first_seen < $to AND p.first_seen > (SELECT MIN(q.first_seen) FROM host_ports q WHERE q.host_id = p.host_id)
                    """;
                cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (ids.Contains(r.GetInt64(0))) portsOpened++;
            }
        }
        bool In(string iso) => string.CompareOrdinal(iso, from) >= 0 && string.CompareOrdinal(iso, to) < 0;
        var outages = GetWanOutageLog(500).Where(o => In(o.Start)).ToList();
        var slow = GetWanSlowLog(500).Where(o => In(o.Start)).ToList();
        var alerts = GetAlerts(2000, fromUtc).Where(a => In(a.At)).ToList();
        return new PeriodStats(from, to, devices, arrived, drops, outages.Count, outages.Sum(o => o.Minutes), slow.Count, slow.Sum(o => o.Minutes),
            bytes, alerts.Count, alerts.Count(a => a.Kind is "security" or "cert" or "port" or "dhcp" or "dns" or "ra"), unusual, portsOpened);
    }

    /// <summary>
    /// The last so many days against the same number of days before them, as metrics with the good direction marked. `scoreDays` is the
    /// score's daily readings (date, value), if the score is on; `extraAlerts` are alerts kept outside the alerts table (the traffic
    /// monitor's), as (kind, at).
    /// </summary>
    public List<CompareMetric> Compare(int days, DateTime? nowUtc = null, IEnumerable<(string Date, int Value)>? scoreDays = null, IEnumerable<(string Kind, string At)>? extraAlerts = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var mid = now.AddDays(-days); var start = mid.AddDays(-days);
        var a = Period(mid, now); var b = Period(start, mid);
        int aExtra = 0, bExtra = 0, aExtraSec = 0, bExtraSec = 0;
        foreach (var (kind, at) in extraAlerts ?? Array.Empty<(string, string)>())
        {
            var sec = kind is "dhcp" or "dns" or "ra";
            if (string.CompareOrdinal(at, a.From) >= 0 && string.CompareOrdinal(at, a.To) < 0) { aExtra++; if (sec) aExtraSec++; }
            else if (string.CompareOrdinal(at, b.From) >= 0 && string.CompareOrdinal(at, b.To) < 0) { bExtra++; if (sec) bExtraSec++; }
        }
        var list = new List<CompareMetric>
        {
            new("devices", "Devices known", a.Devices, b.Devices, "", null),
            new("arrived", "New devices", a.Arrived, b.Arrived, "", null),
            new("drops", "Times a device went offline", a.Drops, b.Drops, "", "down"),
            new("outages", "Internet outages", a.Outages, b.Outages, "", "down"),
            new("outageMinutes", "Minutes without internet", a.OutageMinutes, b.OutageMinutes, "min", "down"),
            new("slow", "Slow internet spells", a.SlowSpells, b.SlowSpells, "", "down"),
            new("bytes", "Data moved", a.Bytes, b.Bytes, "bytes", null),
            new("alerts", "Alerts raised", a.Alerts + aExtra, b.Alerts + bExtra, "", "down"),
            new("security", "Security alerts", a.SecurityAlerts + aExtraSec, b.SecurityAlerts + bExtraSec, "", "down"),
            new("unusual", "Unusual findings", a.Unusual, b.Unusual, "", "down"),
            new("ports", "Ports that opened", a.PortsOpened, b.PortsOpened, "", "down"),
        };
        if (scoreDays is not null)
        {
            var sd = scoreDays.ToList();
            double? Avg(DateTime f, DateTime t)
            {
                var fs = f.ToString("yyyy-MM-dd"); var ts = t.ToString("yyyy-MM-dd");
                var vals = sd.Where(d => string.CompareOrdinal(d.Date, fs) >= 0 && string.CompareOrdinal(d.Date, ts) <= 0).Select(d => (double)d.Value).ToList();
                return vals.Count == 0 ? null : Math.Round(vals.Average(), 1);
            }
            var an = Avg(mid, now); var bn = Avg(start, mid.AddDays(-1));
            if (an is not null && bn is not null) list.Insert(0, new CompareMetric("score", "Network score, on average", an.Value, bn.Value, "", "up"));
        }
        return list;
    }

    /// <summary>The metrics that moved, as one sentence for a report: "two more outages, 1.3 GB less data moved".</summary>
    public static string CompareSentence(IEnumerable<CompareMetric> metrics, int max = 5)
    {
        var bits = new List<string>();
        foreach (var m in metrics)
        {
            if (m.Now == m.Before || m.Key == "devices") continue;
            var up = m.Now > m.Before;
            var diff = Math.Abs(m.Now - m.Before);
            string amount = m.Unit == "bytes" ? Bytes(diff) : m.Unit == "min" ? $"{diff:0} minute{(diff == 1 ? "" : "s")}" : m.Key == "score" ? $"{diff:0.#} point{(diff == 1 ? "" : "s")}" : diff.ToString("0", CultureInfo.InvariantCulture);
            var what = m.Key switch
            {
                "score" => "on the network score",
                "arrived" => "new device" + (diff == 1 ? "" : "s"),
                "drops" => "device drop" + (diff == 1 ? "" : "s"),
                "outages" => "internet outage" + (diff == 1 ? "" : "s"),
                "outageMinutes" => "without internet",
                "slow" => "slow spell" + (diff == 1 ? "" : "s"),
                "bytes" => "of data moved",
                "alerts" => "alert" + (diff == 1 ? "" : "s"),
                "security" => "security alert" + (diff == 1 ? "" : "s"),
                "unusual" => "unusual finding" + (diff == 1 ? "" : "s"),
                "ports" => "port" + (diff == 1 ? "" : "s") + " opened",
                _ => m.Label.ToLowerInvariant(),
            };
            bits.Add(m.Unit == "bytes" || m.Key == "score" || m.Unit == "min" ? $"{amount} {(up ? "more" : "less")} {what}" : $"{amount} {(up ? "more" : "fewer")} {what}");
            if (bits.Count >= max) break;
        }
        return bits.Count == 0 ? "" : string.Join(", ", bits);
    }

    private static string Bytes(double b) =>
        b >= 1e12 ? $"{b / 1e12:0.0} TB" : b >= 1e9 ? $"{b / 1e9:0.0} GB" : b >= 1e6 ? $"{b / 1e6:0} MB" : b >= 1e3 ? $"{b / 1e3:0} kB" : $"{b:0} B";
}
