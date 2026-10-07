using System.Net;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Is the DNS this network uses telling the truth? Every hour, three things are asked of the DNS servers this
/// machine is set to use (usually the router, which passes the question on to your provider or whatever it was
/// pointed at):
///
/// - The addresses of a few names whose answers never change: dns.google is 8.8.8.8 and 8.8.4.4, and so on. A
///   resolver that answers with anything else is sending traffic for those names somewhere it shouldn't go. That
///   is what a hijacked router, or malware that changed the DNS setting, looks like from inside.
/// - A name that doesn't exist. The right answer is "no such name". A resolver that gives an address instead is
///   making answers up: some providers do it to show adverts on typos, and so do some kinds of malware.
/// - Which servers this machine is using at all. A new one appearing, that was never seen before, is an alert:
///   expected if you changed the router's DNS, worth a look if you didn't.
///
/// Off by default, because the lookups leave the house (three small DNS questions an hour). The health check runs
/// it too while it's on. What it last found is on the hygiene card and under Settings.
/// </summary>
public sealed class DnsWatch : BackgroundService
{
    /// <summary>Names whose addresses are fixed and published by their owners, so a different answer is a wrong one.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Canaries = new Dictionary<string, string[]>
    {
        ["dns.google"] = new[] { "8.8.8.8", "8.8.4.4" },
        ["one.one.one.one"] = new[] { "1.1.1.1", "1.0.0.1" },
        ["dns.quad9.net"] = new[] { "9.9.9.9", "149.112.112.112" },
    };

    public sealed record Wrong(string Name, string Got, string Expected);
    public sealed record Result(string CheckedAt, List<string> Servers, string? Server, List<Wrong> WrongAnswers, bool InventsAnswers, string? Invented, List<string> NewServers, string? Error);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly ILogger<DnsWatch> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>How a question is asked and where the servers come from. Swapped out by tests.</summary>
    internal Func<IPAddress, string, CancellationToken, Task<DnsClient.Answer>> Query { get; set; } = (s, n, ct) => DnsClient.QueryA(s, n, 3000, ct);
    internal Func<List<IPAddress>> Servers { get; set; } = DnsClient.SystemServers;

    public DnsWatch(HostStore store, ScannerService scanner, ILogger<DnsWatch> log)
    {
        _store = store; _scanner = scanner; _log = log;
    }

    public bool Enabled => _store.GetSetting("dnsWatch") == "true";

    public Result? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Result>(_store.GetSetting("dnsWatchResult") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Whether an answer for a canary is wrong: it has addresses, and none of them is one the name is known to have.</summary>
    public static bool IsWrong(string name, IReadOnlyCollection<IPAddress> got) =>
        got.Count > 0 && Canaries.TryGetValue(name, out var expected) && !got.Any(a => expected.Contains(a.ToString()));

    /// <summary>A name nobody has registered, under a zone that exists, so the only honest answer is "no such name".</summary>
    public static string NonsenseName() => $"bamf-{Guid.NewGuid():N}".Substring(0, 22) + ".example.com";

    /// <summary>Asks the questions, writes the result down, and says what needs saying. Null if a check was already running.</summary>
    public async Task<Result?> Check(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var servers = Servers();
            var seen = (_store.GetSetting("dnsServersSeen") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var current = servers.Select(s => s.ToString()).ToList();
            var fresh = seen.Count == 0 ? new List<string>() : current.Where(s => !seen.Contains(s)).ToList();
            if (seen.Count == 0 || fresh.Count > 0) _store.SetSetting("dnsServersSeen", string.Join(",", seen.Union(current)));

            Result result;
            if (servers.Count == 0)
                result = new Result(now.ToString("o"), current, null, new(), false, null, fresh, "This machine has no DNS server set.");
            else
            {
                var server = servers[0];
                var wrong = new List<Wrong>();
                string? error = null;
                foreach (var (name, expected) in Canaries)
                {
                    var a = await Query(server, name, ct);
                    if (!a.Ok) { error ??= $"{server} didn't answer for {name}: {a.Error}"; continue; }
                    if (IsWrong(name, a.Addresses)) wrong.Add(new Wrong(name, string.Join(", ", a.Addresses.Select(x => x.ToString())), string.Join(" or ", expected)));
                }
                var nonsense = NonsenseName();
                var n = await Query(server, nonsense, ct);
                var invents = n.Ok && n.Addresses.Count > 0;
                result = new Result(now.ToString("o"), current, server.ToString(), wrong, invents, invents ? string.Join(", ", n.Addresses.Select(x => x.ToString())) : null, fresh, error);
            }
            _store.SetSetting("dnsWatchResult", JsonSerializer.Serialize(result));
            await Say(result, now, ct);
            return result;
        }
        finally { _busy.Release(); }
    }

    private async Task Say(Result r, DateTime now, CancellationToken ct)
    {
        if (r.WrongAnswers.Count > 0 && Due("dnsWrongAlertAt", TimeSpan.FromDays(1), now))
        {
            var w = r.WrongAnswers[0];
            var more = r.WrongAnswers.Count > 1 ? $" and {r.WrongAnswers.Count - 1} other name{(r.WrongAnswers.Count > 2 ? "s" : "")} with fixed addresses" : "";
            await _scanner.RaiseSecurity("Your DNS is giving wrong answers",
                $"The DNS server this network uses ({r.Server}) says {w.Name} is at {w.Got}; it is {w.Expected}, and always has been{more}. A resolver that answers wrongly for a name like that can send any site's traffic wherever it likes: " +
                "check the DNS setting in your router and on this machine, and run a malware scan here. If you set up a filtering DNS service on purpose, this may be it doing its job.", ct);
        }
        if (r.InventsAnswers && Due("dnsInventsAlertAt", TimeSpan.FromDays(7), now))
            await _scanner.RaiseSecurity("Your DNS makes up answers for names that don't exist",
                $"Asked about a name nobody has registered, the DNS server this network uses ({r.Server}) answered with {r.Invented} instead of \"no such name\". Some providers do this to put adverts on mistyped addresses; it also breaks software that relies on an honest answer, and it is one of the things malware changes a resolver to do. " +
                "Public resolvers such as 1.1.1.1 or 9.9.9.9 don't do it.", ct);
        if (r.NewServers.Count > 0)
        {
            var outside = r.NewServers.Where(s => IPAddress.TryParse(s, out var ip) && !IsPrivate(ip)).ToList();
            await _scanner.RaiseSecurity("Your DNS servers changed",
                $"This machine is now using {string.Join(" and ", r.NewServers)} for DNS, which it never had before." +
                (outside.Count > 0 ? " That is an address on the internet, not your router: if you didn't set it, something else did, and every name this machine looks up goes through it." : " If you changed the router's DNS setting, that's this.") , ct);
        }
    }

    private bool Due(string key, TimeSpan every, DateTime now)
    {
        if (DateTime.TryParse(_store.GetSetting(key), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && now - at.ToUniversalTime() < every) return false;
        _store.SetSetting(key, now.ToString("o"));
        return true;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length != 4 || b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Check(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The DNS watch failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
