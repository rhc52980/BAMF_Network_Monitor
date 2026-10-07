namespace LanWatch.Services;

/// <summary>
/// When a device's ping has been high for a while, and when it has come back: five probes in a row at or over the
/// limit opens an episode, three in a row under it closes one. A probe with no reply says nothing either way, since
/// plenty of devices drop pings while working perfectly.
/// </summary>
public sealed class LatencyEpisodes
{
    public const int HighProbes = 5, BackProbes = 3;
    private readonly Dictionary<long, int> _over = new(), _under = new();
    private readonly Dictionary<long, int> _worst = new();
    private readonly HashSet<long> _high = new();

    public bool IsHigh(long hostId) => _high.Contains(hostId);
    public int Worst(long hostId) => _worst.GetValueOrDefault(hostId);

    /// <summary>"high" the probe an episode opens, "back" the probe it closes, otherwise null.</summary>
    public string? Observe(long hostId, int? ms, int limitMs)
    {
        if (ms is null) return null;
        if (ms >= limitMs)
        {
            _under[hostId] = 0;
            _over[hostId] = _over.GetValueOrDefault(hostId) + 1;
            if (_high.Contains(hostId)) { if (ms > _worst.GetValueOrDefault(hostId)) _worst[hostId] = ms.Value; return null; }
            if (_over[hostId] < HighProbes) return null;
            _high.Add(hostId); _worst[hostId] = ms.Value;
            return "high";
        }
        _over[hostId] = 0;
        if (!_high.Contains(hostId)) return null;
        _under[hostId] = _under.GetValueOrDefault(hostId) + 1;
        if (_under[hostId] < BackProbes) return null;
        _high.Remove(hostId); _under[hostId] = 0;
        return "back";
    }

    /// <summary>A device that's gone, or no longer watched, is forgotten so it doesn't pick up where it left off.</summary>
    public void Forget(long hostId) { _over.Remove(hostId); _under.Remove(hostId); _high.Remove(hostId); _worst.Remove(hostId); }
}
