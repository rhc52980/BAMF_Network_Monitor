namespace LanWatch.Services;

/// <summary>
/// Alerts that didn't go out. A destination that is briefly down (a Discord outage, an ntfy server restarting) used to cost the alert
/// for good: the failure was logged and that was that. Now a refused or unreachable delivery is kept and tried again, a minute later, then
/// five, fifteen and thirty minutes after that, and only then given up, with a line in the Problems card saying so. The queue is in memory,
/// so a restart forgets what was waiting, and it keeps at most a hundred. A test message is never retried.
/// </summary>
public partial class ScannerService
{
    /// <summary>On unless switched off in Settings.</summary>
    public bool AlertRetryEnabled => _store.GetSetting("alertRetry") != "false";

    /// <summary>Waits between tries: after the first failure, then after each retry that failed.</summary>
    public static readonly TimeSpan[] RetryDelays = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30) };
    public const int MaxRetryQueue = 100;
    public static readonly TimeSpan MaxRetryAge = TimeSpan.FromHours(2);

    private sealed class RetryItem
    {
        public string DestinationId = "";
        public string Kind = "", Title = "", Text = "";
        public Func<string, string, HttpRequestMessage> Build = null!;
        public int Tries;                    // retries done
        public DateTime NextAtUtc, FirstAtUtc;
    }

    private readonly List<RetryItem> _retry = new();
    private readonly object _retryLock = new();

    /// <summary>How many alerts are waiting to be tried again.</summary>
    public int RetryQueued { get { lock (_retryLock) return _retry.Count; } }

    private void QueueRetry(Destination d, string kind, string title, Func<string, string, HttpRequestMessage> build, DateTime nowUtc, string text = "")
    {
        lock (_retryLock)
        {
            _retry.Add(new RetryItem { DestinationId = d.Id, Kind = kind, Title = title, Text = text, Build = build, NextAtUtc = nowUtc + RetryDelays[0], FirstAtUtc = nowUtc });
            while (_retry.Count > MaxRetryQueue) _retry.RemoveAt(0);
        }
    }

    /// <summary>Tries again the alerts that are due. Returns how many went out. <paramref name="nowUtc"/> is for tests.</summary>
    public async Task<int> RetryFailedAlerts(CancellationToken ct, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        List<RetryItem> due;
        lock (_retryLock)
        {
            _retry.RemoveAll(i => now - i.FirstAtUtc > MaxRetryAge);
            due = _retry.Where(i => i.NextAtUtc <= now).ToList();
        }
        if (due.Count == 0) return 0;
        // Not into quiet hours or a pause: they were already past that gate, but a retry shouldn't ring a phone that has been silenced since.
        if (IsQuietNow())
        {
            lock (_retryLock) foreach (var i in due) i.NextAtUtc = now + TimeSpan.FromMinutes(5);
            return 0;
        }
        var sent = 0;
        var client = _httpFactory.CreateClient();
        foreach (var item in due)
        {
            var d = Destinations().FirstOrDefault(x => x.Id == item.DestinationId);
            if (d is null) { Remove(item); continue; }          // taken off the list since
            string? error = null;
            try
            {
                if (d.Format == "email") await SendEmail(d, item.Title, item.Text, ct);
                else
                {
                    var format = ResolveFormat(d.Url, d.Format);
                    using var req = item.Build(d.Url, format);
                    using var resp = await client.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) error = $"HTTP {(int)resp.StatusCode}";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = ex.GetBaseException().Message; }

            if (error is null)
            {
                sent++;
                _log.LogInformation("Alert ({Kind}) to {Name} went out on retry {N}", item.Kind, d.Name, item.Tries + 1);
                Remove(item);
                continue;
            }
            item.Tries++;
            if (item.Tries >= RetryDelays.Length)
            {
                _log.LogWarning("Gave up sending \"{Title}\" to {Name} after {N} more tries: {Error}", item.Title, d.Name, item.Tries, error);
                Remove(item);
            }
            else item.NextAtUtc = now + RetryDelays[item.Tries];
        }
        return sent;

        void Remove(RetryItem i) { lock (_retryLock) _retry.Remove(i); }
    }
}
