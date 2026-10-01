namespace LanWatch.Services;

/// <summary>
/// What's normal for each device, learned from the history BAMF already keeps,
/// and what isn't. Three things are noticed:
///
/// Off longer than usual: a device that's almost always on (95% of the time,
/// seen over at least two weeks before it went off) has been off at least
/// three times longer than it ever was in that time, and at least half an
/// hour. Watched devices are left out, since they already alert the moment
/// they drop.
///
/// On at an unusual hour: a device that comes and goes came online at an hour
/// it hasn't been on, or within an hour either side of it, on any day in the
/// last four weeks. Once a day at most for each device.
///
/// Slower than usual: a device's last five pings are four times its usual
/// time over the last week, and at least 50 ms more.
///
/// Nothing is said about a device until it has two weeks of history. Pure
/// functions, so they're tested on made-up histories.
/// </summary>
public static class Unusual
{
    public const int LearnDays = 14, WindowDays = 28;
    public const double AlwaysOn = 0.95, ComesAndGoes = 0.90;
    public static readonly TimeSpan MinOff = TimeSpan.FromMinutes(30);

    public sealed record History(long Id, string Name, DateTime FirstSeenUtc, bool Online, bool Watched,
        IReadOnlyList<(DateTime At, bool Online)> Events);

    /// <summary>
    /// Something unusual. Key names the occurrence, so it's recorded once;
    /// Ongoing ones (off, slow) stay open until they're over, the others are
    /// a note of something that happened.
    /// </summary>
    public sealed record Finding(long HostId, string Kind, string Key, string Title, string Detail, bool Ongoing);

    /// <summary>The device's online stretches between start and now, from its events.</summary>
    public static List<(DateTime From, DateTime To)> OnlineStretches(History d, DateTime start, DateTime now)
    {
        var evs = d.Events.Where(e => e.At <= now).OrderBy(e => e.At).ToList();
        var before = evs.LastOrDefault(e => e.At <= start);
        var first = evs.FirstOrDefault(e => e.At > start);
        bool state = before != default ? before.Online : first != default ? !first.Online : d.Online;
        var list = new List<(DateTime, DateTime)>();
        var t = start;
        foreach (var e in evs.Where(e => e.At > start))
        {
            if (state && e.At > t) list.Add((t, e.At));
            t = e.At; state = e.Online;
        }
        if (state && now > t) list.Add((t, now));
        return list;
    }

    private static DateTime WindowStart(History d, DateTime now)
    {
        var start = now.AddDays(-WindowDays);
        return d.FirstSeenUtc > start ? d.FirstSeenUtc : start;
    }

    private static bool Learned(History d, DateTime now) => now - d.FirstSeenUtc >= TimeSpan.FromDays(LearnDays);

    /// <summary>Off for far longer than this always-on device ever is.</summary>
    public static Finding? OffTooLong(History d, DateTime now)
    {
        if (d.Online || d.Watched || !Learned(d, now)) return null;
        var start = WindowStart(d, now);
        var on = OnlineStretches(d, start, now);
        if (on.Count == 0) return null;
        var offSince = on[^1].To;   // off since its last stretch ended
        var current = now - offSince;
        // How much it's on, and its longest break, before this one: over at
        // least two weeks, or a device that went off just as the window began
        // would look as if it were always on.
        if (offSince - start < TimeSpan.FromDays(LearnDays)) return null;
        var span = (offSince - start).TotalSeconds;
        var share = on.Sum(s => (s.To - s.From).TotalSeconds) / span;
        if (share < AlwaysOn) return null;
        var longest = TimeSpan.Zero;
        for (var i = 1; i < on.Count; i++)
            if (on[i].From - on[i - 1].To > longest) longest = on[i].From - on[i - 1].To;
        if (current < MinOff || current < longest * 3) return null;
        var days = (int)Math.Round((now - start).TotalDays);
        var was = longest == TimeSpan.Zero ? "it was never off at all" : $"it was never off for more than {Span(longest)}";
        return new Finding(d.Id, "offline", $"offline:{d.Id}:{offSince:o}",
            $"{d.Name} has been off for {Span(current)}",
            $"It's almost always on: in the last {days} days {was}.", Ongoing: true);
    }

    /// <summary>Came online at an hour it isn't usually on.</summary>
    public static Finding? OddHour(History d, DateTime now, TimeZoneInfo tz)
    {
        if (!d.Online || !Learned(d, now)) return null;
        // The moment it came on: its last online event, if that's recent.
        var came = d.Events.Where(e => e.Online && e.At <= now).Select(e => e.At).DefaultIfEmpty().Max();
        if (came == default || now - came > TimeSpan.FromMinutes(30)) return null;
        var start = WindowStart(d, now);
        var on = OnlineStretches(d, start, came);
        var span = (came - start).TotalSeconds;
        if (span <= 0 || on.Sum(s => (s.To - s.From).TotalSeconds) / span >= ComesAndGoes) return null;

        // Each whole local day before today: which hours it was on in.
        var today = TimeZoneInfo.ConvertTimeFromUtc(came, tz).Date;
        var firstDay = TimeZoneInfo.ConvertTimeFromUtc(start, tz).Date.AddDays(1);
        var hours = new int[24];
        int days = 0, daysOn = 0;
        for (var day = firstDay; day < today; day = day.AddDays(1))
        {
            days++;
            var seen = new bool[24];
            foreach (var (from, to) in on)
            {
                var a = TimeZoneInfo.ConvertTimeFromUtc(from, tz);
                var b = TimeZoneInfo.ConvertTimeFromUtc(to, tz);
                if (b <= day || a >= day.AddDays(1)) continue;
                var lo = a < day ? day : a;
                var hi = b > day.AddDays(1) ? day.AddDays(1) : b;
                for (var h = lo; h < hi; h = h.Date.AddHours(h.Hour + 1)) seen[h.Hour] = true;
            }
            if (seen.Any(x => x)) daysOn++;
            for (var h = 0; h < 24; h++) if (seen[h]) hours[h]++;
        }
        // Enough days, and on often enough, to know its hours.
        if (days < LearnDays - 1 || daysOn < 7) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(came, tz);
        var hr = local.Hour;
        if (hours[(hr + 23) % 24] + hours[hr] + hours[(hr + 1) % 24] > 0) return null;
        return new Finding(d.Id, "hour", $"hour:{d.Id}:{local:yyyy-MM-dd}",
            $"{d.Name} came online at {Clock(local)}",
            $"In the last {days} days it was never on between {Hour((hr + 23) % 24)} and {Hour((hr + 2) % 24)}.", Ongoing: false);
    }

    /// <summary>Answering far slower than it usually does.</summary>
    public static Finding? Slow(long id, string name, double? usualMs, IReadOnlyList<int> lastFive)
    {
        if (usualMs is not { } usual || usual <= 0 || lastFive.Count < 5) return null;
        var now = Median(lastFive)!.Value;
        if (now < Math.Max(usual * 4, usual + 50)) return null;
        return new Finding(id, "slow", $"slow:{id}", $"{name} is answering slowly",
            $"Its last five pings took {Math.Round(now)} ms, against a usual {Math.Round(usual)} ms.", Ongoing: true);
    }

    /// <summary>Whether a slow device is back to near its usual time.</summary>
    public static bool SlowOver(double? usualMs, IReadOnlyList<int> lastFive) =>
        usualMs is not { } usual || lastFive.Count < 5 || Median(lastFive)!.Value < usual * 2;

    public static double? Median(IEnumerable<int> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
    }

    public static string Span(TimeSpan s) =>
        s.TotalMinutes < 1 ? "under a minute"
        : s.TotalHours < 1 ? ((int)s.TotalMinutes == 1 ? "1 minute" : $"{(int)s.TotalMinutes} minutes")
        : s.TotalDays < 1 ? $"{(int)s.TotalHours} h {s.Minutes} min"
        : $"{(int)s.TotalDays} days {s.Hours} h";

    private static string Hour(int h) => h == 0 ? "midnight" : h == 12 ? "noon" : h < 12 ? $"{h} am" : $"{h - 12} pm";
    private static string Clock(DateTime t) => t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
}
