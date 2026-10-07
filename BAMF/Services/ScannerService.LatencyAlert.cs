namespace LanWatch.Services;

/// <summary>
/// A watched device that is up but slow to answer. The latency probe already pings every device after each scan;
/// this looks at the watched ones' readings and says when one has been at or over the limit for five probes in a
/// row, and again when it has been under it for three. Watched devices only: they are the ones whose going down
/// already alerts, and a phone or a laptop on Wi-Fi is often slow for no reason worth hearing about.
/// </summary>
public partial class ScannerService
{
    private readonly LatencyEpisodes _latencyEpisodes = new();

    public bool LatencyAlertEnabled => _store.GetSetting("latencyAlert") != "false";

    /// <summary>The round-trip time, in ms, a watched device must reach for five probes running to be called slow. 250 unless changed.</summary>
    public int LatencyAlertMs => int.TryParse(_store.GetSetting("latencyAlertMs"), out var v) ? Math.Clamp(v, 20, 5000) : 250;

    /// <summary>Which watched devices are in a slow spell now, for the dashboard.</summary>
    public IReadOnlyCollection<long> SlowWatched => _store.GetAll().Where(h => h.Watched && _latencyEpisodes.IsHigh(h.Id)).Select(h => h.Id).ToList();

    private async Task LatencyAlerts(IReadOnlyCollection<(long HostId, int? Ms)> samples, CancellationToken ct)
    {
        if (!LatencyAlertEnabled) return;
        var limit = LatencyAlertMs;
        var hosts = _store.GetAll().ToDictionary(h => h.Id);
        foreach (var (id, ms) in samples)
        {
            if (!hosts.TryGetValue(id, out var h)) continue;
            if (!h.Watched || h.Ignored || h.Forgotten) { _latencyEpisodes.Forget(id); continue; }
            var what = _latencyEpisodes.Observe(id, ms, limit);
            if (what is null || _store.IsSnoozed(id)) continue;
            var name = h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;
            if (what == "high")
                await SendGenericAlert($"{name} is slow to answer: {ms} ms",
                    $"{name} ({h.Ip}) has taken {limit} ms or more to answer the last {LatencyEpisodes.HighProbes} pings, {ms} ms this time. It's up, but anything that talks to it will feel it: a weak Wi-Fi signal, a busy device, or a struggling switch between here and there. BAMF will say when it's back under {limit} ms.", "status", ct);
            else
                await SendGenericAlert($"{name} is answering normally again: {ms} ms",
                    $"{name} ({h.Ip}) has answered the last {LatencyEpisodes.BackProbes} pings in under {limit} ms, after a slow spell that reached {_latencyEpisodes.Worst(id)} ms.", "status-up", ct);
        }
    }
}
