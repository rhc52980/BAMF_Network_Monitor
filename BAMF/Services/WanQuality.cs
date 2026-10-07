namespace LanWatch.Services;

/// <summary>
/// What a line is like between outages: how many pings it loses, how much the time varies from one to the next
/// (jitter: what makes calls and games stutter while pages still load), and how long a DNS lookup takes (what makes
/// every page start slowly while the speed test says the line is fine). All from the readings the internet watch
/// already takes, one a minute.
/// </summary>
public static class WanQuality
{
    /// <summary>One window of readings summed up. DNS figures are null until some have been measured.</summary>
    public sealed record Report(int Samples, double LossPercent, int? JitterMs, int? DnsMs, int DnsMeasured, int DnsFailed);

    public const int MinSamples = 10;
    public const double LossLimit = 10;        // percent of pings lost, the line not being down
    public const int JitterLimit = 50;         // ms between one ping and the next
    public const int DnsSlowLimit = 300;       // ms for a lookup, median
    public const double DnsFailLimit = 30;     // percent of lookups with no answer

    /// <summary>Internet ms (-1 lost) and DNS ms (-1 failed, -2 not measured) per reading, oldest first.</summary>
    public static Report Assess(IReadOnlyList<(int Internet, int Dns)> samples)
    {
        if (samples.Count == 0) return new Report(0, 0, null, null, 0, 0);
        var lost = samples.Count(s => s.Internet < 0);
        var answered = samples.Where(s => s.Internet >= 0).Select(s => s.Internet).ToList();
        int? jitter = null;
        if (answered.Count >= 2)
        {
            double sum = 0;
            for (var i = 1; i < answered.Count; i++) sum += Math.Abs(answered[i] - answered[i - 1]);
            jitter = (int)Math.Round(sum / (answered.Count - 1));
        }
        var measured = samples.Where(s => s.Dns != -2).Select(s => s.Dns).ToList();
        var good = measured.Where(ms => ms >= 0).OrderBy(ms => ms).ToList();
        int? dnsMs = good.Count > 0 ? good[good.Count / 2] : null;
        return new Report(samples.Count, Math.Round(100.0 * lost / samples.Count, 1), jitter, dnsMs, measured.Count, measured.Count - good.Count);
    }

    /// <summary>Which problems a window shows: "loss", "jitter", "dns". Nothing until there are enough readings.</summary>
    public static HashSet<string> Problems(Report r)
    {
        var set = new HashSet<string>();
        if (r.Samples < MinSamples) return set;
        if (r.LossPercent >= LossLimit) set.Add("loss");
        if (r.JitterMs is { } j && j >= JitterLimit) set.Add("jitter");
        if (r.DnsMeasured >= MinSamples && ((r.DnsMs is { } d && d >= DnsSlowLimit) || 100.0 * r.DnsFailed / r.DnsMeasured >= DnsFailLimit)) set.Add("dns");
        return set;
    }
}
