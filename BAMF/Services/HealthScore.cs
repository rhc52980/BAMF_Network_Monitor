using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// One number for the network, 0 to 100, from what BAMF already knows, with the reasons it isn't 100. It starts at
/// 100 and loses points for each thing worth fixing: unknown devices online, security alerts this week, Telnet and FTP
/// open, certificates run out, a router answering UPnP, the public address seen scanning, watched devices down,
/// unusual activity open, internet outages and slow spells this week, the internet down now, a disk running low, and
/// nobody set to hear alerts. Each kind of thing has a cap, so one bad category can't zero the score on its own.
///
/// 90 and up is healthy, 75 fine, 50 needs attention, under that in trouble. The number is on Activity, at the top
/// of scheduled reports and in the daily all-quiet note; one reading a day is kept for a month, so the card can say
/// which way it's going.
/// </summary>
public sealed class HealthScore : BackgroundService
{
    public sealed record Inputs(int UnknownOnline, int SecurityAlerts, int Telnet, int Ftp, int Vnc, int CertsExpired, int CertsExpiring,
        bool Upnp, bool Noise, int WatchedDown, int UnusualOpen, int Outages, int SlowSpells, bool InternetDown, bool DiskLow, bool NoDestination);
    public sealed record Reason(string Text, int Points);
    public sealed record Score(int Value, string Word, List<Reason> Reasons);
    public sealed record Day(string Date, int Value);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly SecurityCheck _security;
    private readonly GreyNoiseCheck _greynoise;
    private readonly WanWatch _wan;
    private readonly DiskHealth _disk;
    private readonly ILogger<HealthScore> _log;

    public HealthScore(HostStore store, ScannerService scanner, SecurityCheck security, GreyNoiseCheck greynoise, WanWatch wan, DiskHealth disk, ILogger<HealthScore> log)
    {
        _store = store; _scanner = scanner; _security = security; _greynoise = greynoise; _wan = wan; _disk = disk; _log = log;
    }

    public bool Enabled => _store.GetSetting("networkScore") != "false";

    public static string WordFor(int value) => value >= 90 ? "Healthy" : value >= 75 ? "Fine" : value >= 50 ? "Needs attention" : "In trouble";

    /// <summary>The score for a set of inputs. Pure, so it can be reasoned about.</summary>
    public static Score Compute(Inputs i)
    {
        var reasons = new List<Reason>();
        void Take(int count, int each, int cap, string one, string many)
        {
            if (count <= 0) return;
            reasons.Add(new Reason(count == 1 ? one : string.Format(many, count), Math.Min(cap, count * each)));
        }
        void Flag(bool on, int points, string text) { if (on) reasons.Add(new Reason(text, points)); }

        Flag(i.Noise, 20, "Your public address has been seen scanning the internet");
        Flag(i.InternetDown, 10, "The internet is down");
        Take(i.SecurityAlerts, 8, 24, "A security alert this week", "{0} security alerts this week");
        Take(i.Telnet, 8, 16, "Telnet is open on a device", "Telnet is open on {0} devices");
        Flag(i.Upnp, 8, "The router answers UPnP, so any device can open ports to the internet");
        Take(i.CertsExpired, 6, 12, "A certificate has expired", "{0} certificates have expired");
        Take(i.WatchedDown, 5, 15, "A watched device is down", "{0} watched devices are down");
        Flag(i.DiskLow, 10, "A disk BAMF writes to is running low");
        Flag(i.NoDestination, 5, "Nobody would hear an alert: no destination is set up");
        Take(i.Ftp, 4, 8, "FTP is open on a device", "FTP is open on {0} devices");
        Take(i.Vnc, 4, 8, "VNC is open on a device", "VNC is open on {0} devices");
        Take(i.UnknownOnline, 3, 15, "An unknown device is online", "{0} unknown devices are online");
        Take(i.UnusualOpen, 3, 9, "Something unusual is going on with a device", "Something unusual is going on with {0} devices");
        Take(i.Outages, 3, 12, "An internet outage this week", "{0} internet outages this week");
        Take(i.CertsExpiring, 2, 6, "A certificate expires within a fortnight", "{0} certificates expire within a fortnight");
        Take(i.SlowSpells, 2, 6, "A slow internet spell this week", "{0} slow internet spells this week");

        var value = Math.Clamp(100 - reasons.Sum(r => r.Points), 0, 100);
        return new Score(value, WordFor(value), reasons.OrderByDescending(r => r.Points).ToList());
    }

    /// <summary>What the network looks like now, from the store and the watches.</summary>
    public Inputs Gather(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var week = now.AddDays(-7);
        var weekIso = week.ToString("o");
        var hosts = _store.GetAll().Where(h => !h.Forgotten && !h.Ignored).ToList();
        var ids = hosts.Select(h => h.Id).ToHashSet();
        var open = _store.OpenPorts();
        int Port(int p) => open.Count(kv => ids.Contains(kv.Key) && kv.Value.Contains(p));
        var certs = _store.GetCerts().Where(c => ids.Contains(c.HostId) && c.NotAfter != "" && DateTime.TryParse(c.NotAfter, null, System.Globalization.DateTimeStyles.RoundtripKind, out _)).ToList();
        int CertDays(string iso) => (int)Math.Floor((DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() - now).TotalDays);
        bool diskLow;
        try { diskLow = _disk.Snapshot(now).Volumes.Any(v => v.Low); } catch (Exception ex) { _log.LogDebug(ex, "Disk snapshot for the score failed"); diskLow = false; }
        return new Inputs(
            UnknownOnline: hosts.Count(h => h.Online && !h.Known),
            SecurityAlerts: _store.GetAlerts(200, week).Count(a => a.Kind == "security"),
            Telnet: Port(23), Ftp: Port(21), Vnc: Port(5900),
            CertsExpired: certs.Count(c => CertDays(c.NotAfter) < 0),
            CertsExpiring: certs.Count(c => CertDays(c.NotAfter) is >= 0 and <= 14),
            Upnp: _security.Upnp is { Devices.Count: > 0 },
            Noise: _greynoise.Enabled && _greynoise.Last?.Noise == true,
            WatchedDown: hosts.Count(h => h.Watched && !h.Online),
            UnusualOpen: _store.RecentUnusual(24).Count(u => u.ResolvedAt is null),
            Outages: _wan.Enabled ? _store.GetWanOutageLog(200).Count(o => string.CompareOrdinal(o.Start, weekIso) >= 0) : 0,
            SlowSpells: _wan.Enabled ? _store.GetWanSlowLog(200).Count(o => string.CompareOrdinal(o.Start, weekIso) >= 0) : 0,
            InternetDown: _wan.Enabled && _wan.Current is not null,
            DiskLow: diskLow,
            NoDestination: !_scanner.AnyDestination);
    }

    public Score Now() => Compute(Gather());

    /// <summary>One reading a day for the last month, oldest first.</summary>
    public List<Day> History()
    {
        try { return JsonSerializer.Deserialize<List<Day>>(_store.GetSetting("scoreHistory") ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Writes today's reading, replacing one taken earlier today, and drops anything older than 30 days.</summary>
    public void Record(int value, DateTime? nowUtc = null)
    {
        var today = (nowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd");
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-30).ToString("yyyy-MM-dd");
        var days = History().Where(d => d.Date != today && string.CompareOrdinal(d.Date, cutoff) >= 0).ToList();
        days.Add(new Day(today, value));
        _store.SetSetting("scoreHistory", JsonSerializer.Serialize(days.OrderBy(d => d.Date).ToList()));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) Record(Now().Value); }
            catch (Exception ex) { _log.LogDebug(ex, "Recording the network score failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
