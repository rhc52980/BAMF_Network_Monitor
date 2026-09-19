using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>
/// Other BAMF servers, read-only. Each remote in Bamf:Remotes is polled for
/// its /api/hosts once a minute, and its devices show in this dashboard under
/// networks named after the site ("Cabin · 10.0.0.0/24"). Nothing is written
/// to a remote, and a remote that can't be reached keeps its last answer,
/// marked as stale.
/// </summary>
public sealed class RemoteService : BackgroundService
{
    public sealed record Remote(string Name, string Url, string? Password);
    public sealed record Status(string Name, string Url, bool Ok, string? Error, string? FetchedUtc, int Hosts, IReadOnlyList<string> Subnets, string? Version, string? LastScan);

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RemoteService> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, (Status Status, JsonArray Hosts)> _state = new();

    public RemoteService(IConfiguration config, IHttpClientFactory http, ILogger<RemoteService> log)
    {
        _config = config; _http = http; _log = log;
    }

    public List<Remote> Remotes =>
        _config.GetSection("Bamf:Remotes").GetChildren()
        .Select(c => new Remote((c["Name"] ?? "").Trim(), (c["Url"] ?? "").Trim(), c["Password"]))
        .Where(r => r.Name != "" && r.Url != "").ToList();

    public bool Configured => Remotes.Count > 0;

    public List<Status> Statuses { get { lock (_lock) return Remotes.Select(r => _state.TryGetValue(r.Name, out var s) ? s.Status : new Status(r.Name, r.Url, false, "not fetched yet", null, 0, Array.Empty<string>(), null, null)).ToList(); } }

    /// <summary>Every remote's devices, each carrying its site and its network renamed after it.</summary>
    public JsonArray Hosts()
    {
        var all = new JsonArray();
        lock (_lock)
            foreach (var (_, s) in _state)
                foreach (var h in s.Hosts) all.Add(h!.DeepClone());
        return all;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Configured) return;
        while (!ct.IsCancellationRequested)
        {
            foreach (var r in Remotes)
            {
                try { await Fetch(r, ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        var was = _state.TryGetValue(r.Name, out var s) ? s : (Status: new Status(r.Name, r.Url, false, null, null, 0, Array.Empty<string>(), null, null), Hosts: new JsonArray());
                        _state[r.Name] = (was.Status with { Ok = false, Error = ex.Message }, was.Hosts);
                    }
                    _log.LogWarning("Remote {Name}: {Error}", r.Name, ex.Message);
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task Fetch(Remote r, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Get, r.Url.TrimEnd('/') + "/api/hosts");
        if (!string.IsNullOrEmpty(r.Password))
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bamf:" + r.Password)));
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new InvalidDataException("not a BAMF answer");
        var hosts = new JsonArray();
        var subnets = new List<string>();
        foreach (var s in root["subnets"] as JsonArray ?? new JsonArray()) if (s is not null) subnets.Add(r.Name + " · " + s);
        foreach (var node in root["hosts"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject h) continue;
            var copy = (JsonObject)h.DeepClone();
            copy["remote"] = r.Name;
            copy["id"] = "r:" + r.Name + ":" + (h["id"]?.ToString() ?? "");
            copy["subnet"] = r.Name + " · " + (h["subnet"]?.ToString() ?? "");
            if (copy["addresses"] is JsonArray addrs)
                foreach (var a in addrs) if (a is JsonObject ao) ao["subnet"] = r.Name + " · " + (ao["subnet"]?.ToString() ?? "");
            copy["switchId"] = 0; copy["switchPort"] = 0; copy["interfaceOf"] = 0;
            hosts.Add(copy);
        }
        var status = new Status(r.Name, r.Url, true, null, DateTime.UtcNow.ToString("o"), hosts.Count, subnets,
            root["version"]?.ToString(), root["lastScan"]?.ToString());
        lock (_lock) _state[r.Name] = (status, hosts);
    }
}
