using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Where alerts go. The webhook saved in Settings (or Bamf:WebhookUrl) is the
/// main destination; more can be added alongside it: the phone and Discord both,
/// or security alerts to one place and comings and goings to another. Each has
/// its own format and its own choice of which kinds of alert it gets.
///
/// Every alert BAMF sends is one of five kinds, and goes to every destination
/// that takes that kind:
///   devices    a new device on the network
///   status     a watched device going offline or coming back, alert rules, snooze catch-ups
///   security   ARP spoofing and IP conflicts, new DHCP or DNS servers, newly open ports,
///              certificates, GreyNoise
///   internet   the internet watch: down, back, slow, back to normal
///   reports    the scheduled report
/// Quiet hours hold alerts once, not once per destination, and the digest at
/// the end goes to each destination with just the held alerts of its kinds.
/// </summary>
public partial class ScannerService
{
    public sealed record Destination(string Id, string Name, string Url, string Format, IReadOnlyList<string> Kinds);

    /// <summary>The kinds of alert, in the order the dashboard lists them.</summary>
    public static readonly string[] AlertKinds = { "devices", "status", "security", "internet", "reports" };

    /// <summary>How many destinations besides the main one.</summary>
    public const int MaxExtraDestinations = 8;

    private sealed record StoredDestination(string Id, string Name, string Url, string Format, List<string> Kinds);

    private static string CleanFormat(string? f)
    {
        var v = (f ?? "auto").Trim().ToLowerInvariant();
        return v is "ntfy" or "gotify" or "json" or "discord" ? v : "auto";
    }

    private static List<string> CleanKinds(IEnumerable<string>? kinds) =>
        (kinds ?? AlertKinds).Select(k => (k ?? "").Trim().ToLowerInvariant()).Where(AlertKinds.Contains).Distinct().ToList();

    /// <summary>The kinds the main webhook takes: every kind unless some were unticked.</summary>
    public IReadOnlyList<string> MainKinds
    {
        get
        {
            var raw = _store.GetSetting("webhookKinds");
            if (string.IsNullOrEmpty(raw)) return AlertKinds;
            try { return CleanKinds(JsonSerializer.Deserialize<List<string>>(raw)); }
            catch (JsonException) { return AlertKinds; }
        }
    }

    public void SetMainKinds(IEnumerable<string>? kinds) =>
        _store.SetSetting("webhookKinds", JsonSerializer.Serialize(CleanKinds(kinds)));

    /// <summary>The destinations besides the main one, as saved.</summary>
    public List<Destination> ExtraDestinations
    {
        get
        {
            try
            {
                return (JsonSerializer.Deserialize<List<StoredDestination>>(_store.GetSetting("alertDestinations") ?? "[]") ?? new())
                    .Where(d => !string.IsNullOrWhiteSpace(d.Url))
                    .Select(d => new Destination(d.Id, d.Name, d.Url, CleanFormat(d.Format), CleanKinds(d.Kinds)))
                    .ToList();
            }
            catch (JsonException) { return new(); }
        }
    }

    /// <summary>Every destination, the main webhook first.</summary>
    public List<Destination> Destinations()
    {
        var list = new List<Destination>();
        if (!string.IsNullOrWhiteSpace(WebhookUrl))
            list.Add(new Destination("main", "Main webhook", WebhookUrl!, WebhookFormat, MainKinds));
        list.AddRange(ExtraDestinations);
        return list;
    }

    /// <summary>True when anything at all would be sent anywhere.</summary>
    public bool AnyDestination => Destinations().Any(d => d.Kinds.Count > 0);

    /// <summary>True when some destination takes this kind of alert.</summary>
    public bool Takes(string kind) => Destinations().Any(d => d.Kinds.Contains(kind));

    public sealed record DestinationInput(string? Id, string? Name, string? Url, string? Format, List<string>? Kinds);

    /// <summary>
    /// Replaces the extra destinations. A destination that's already saved can
    /// leave its URL blank to keep the one it has: the dashboard never sees the
    /// full URL, so it can't send it back. Returns an error, or null.
    /// </summary>
    public string? SaveExtraDestinations(IEnumerable<DestinationInput> input)
    {
        var saved = ExtraDestinations.ToDictionary(d => d.Id);
        var outList = new List<StoredDestination>();
        foreach (var d in input)
        {
            var id = string.IsNullOrWhiteSpace(d.Id) || d.Id == "main" ? Guid.NewGuid().ToString("N")[..8] : d.Id.Trim();
            var url = (d.Url ?? "").Trim();
            if (url.Length == 0)
            {
                if (!saved.TryGetValue(id, out var was)) return "Give each new destination a URL.";
                url = was.Url;
            }
            if (url.Length > 500) return "That URL is implausibly long.";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "Enter a full http:// or https:// URL.";
            var name = (d.Name ?? "").Trim();
            if (name.Length == 0) name = uri.Host;
            if (name.Length > 40) name = name[..40];
            outList.Add(new StoredDestination(id, name, url, CleanFormat(d.Format), CleanKinds(d.Kinds)));
        }
        if (outList.Count > MaxExtraDestinations) return $"At most {MaxExtraDestinations} destinations besides the main one.";
        _store.SetSetting("alertDestinations", JsonSerializer.Serialize(outList));
        return null;
    }

    // Why the last delivery failed, for the Test button to say.
    private string? _lastDeliveryError;

    /// <summary>
    /// Sends one alert of one kind to every destination that takes that kind,
    /// or during quiet hours holds it once for the digest. The build function
    /// makes the request for one destination's URL and resolved format. Returns
    /// true if at least one destination accepted it (or it was held).
    /// </summary>
    private async Task<bool> Deliver(string kind, string title, string text, Func<string, string, HttpRequestMessage> build,
        CancellationToken ct, bool holdable = true, string? only = null)
    {
        var dests = Destinations().Where(d => only is not null ? d.Id == only : d.Kinds.Contains(kind)).ToList();
        if (dests.Count == 0) return false;
        if (holdable && only is null && IsQuietNow())
        {
            Hold(kind, title, text);
            return true;
        }
        var client = _httpFactory.CreateClient();
        var any = false;
        _lastDeliveryError = null;
        foreach (var d in dests)
        {
            try
            {
                var format = ResolveFormat(d.Url, d.Format);
                using var req = build(d.Url, format);
                using var resp = await client.SendAsync(req, ct);
                _log.LogInformation("Alert ({Kind}) to {Name} via {Format}: {Status}", kind, d.Name, format, (int)resp.StatusCode);
                any |= resp.IsSuccessStatusCode;
                if (!resp.IsSuccessStatusCode) _lastDeliveryError = $"{d.Name} answered HTTP {(int)resp.StatusCode}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Alert ({Kind}) to {Name} failed", kind, d.Name);
                _lastDeliveryError = $"{d.Name}: {ex.GetBaseException().Message}";
            }
        }
        return any;
    }
}
