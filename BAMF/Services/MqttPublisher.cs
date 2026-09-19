using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Presence per device over MQTT, for Home Assistant and anything else that
/// listens: each device is a retained "home" or "not_home" on its own topic,
/// with its details on an attributes topic, and Home Assistant's MQTT
/// discovery makes each one a device_tracker without any YAML. A small MQTT
/// 3.1.1 client of its own (connect, publish at QoS 0, ping), so BAMF carries
/// no extra dependency for it. Configured in appsettings.json only, since the
/// broker password has no business in the dashboard.
/// </summary>
public sealed class MqttPublisher : BackgroundService
{
    private readonly HostStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<MqttPublisher> _log;
    private readonly SemaphoreSlim _send = new(1, 1);
    private Stream? _stream;
    private TcpClient? _tcp;
    private readonly Dictionary<string, string> _published = new();   // mac id -> last state/ip/name fingerprint

    public bool Configured => !string.IsNullOrWhiteSpace(_config["Bamf:Mqtt:Server"]);
    public bool Connected { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastPublishUtc { get; private set; }
    public long Published { get; private set; }
    public string Server => $"{_config["Bamf:Mqtt:Server"]}:{_config.GetValue("Bamf:Mqtt:Port", 1883)}";
    private string Prefix => (_config["Bamf:Mqtt:TopicPrefix"] ?? "bamf").Trim('/');
    private string DiscoveryPrefix => (_config["Bamf:Mqtt:DiscoveryPrefix"] ?? "homeassistant").Trim('/');
    private bool Discovery => _config.GetValue("Bamf:Mqtt:Discovery", true);

    public MqttPublisher(HostStore store, IConfiguration config, ILogger<MqttPublisher> log)
    {
        _store = store; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Configured) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                _log.LogInformation("MQTT connected to {Server}; publishing under {Prefix}/", Server, Prefix);
                _published.Clear();
                await PublishAsync($"{Prefix}/status", "online", true, ct);
                var lastPing = DateTime.UtcNow;
                while (!ct.IsCancellationRequested && Connected)
                {
                    await PublishDevicesAsync(ct);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    if ((DateTime.UtcNow - lastPing).TotalSeconds >= 30)
                    {
                        await SendAsync(new byte[] { 0xC0, 0x00 }, ct);
                        lastPing = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.LogWarning("MQTT: {Error}; retrying in 30 s", ex.Message);
            }
            Close();
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
        try { if (Connected) { await PublishAsync($"{Prefix}/status", "offline", true, CancellationToken.None); await SendAsync(new byte[] { 0xE0, 0x00 }, CancellationToken.None); } } catch { }
        Close();
    }

    // ------------------------------------------------------------ devices

    private async Task PublishDevicesAsync(CancellationToken ct)
    {
        var latency = _store.LatestLatency();
        var devices = _store.GetAll().Where(h => !h.Ignored && !h.Forgotten).ToList();
        var seen = new HashSet<string>();
        foreach (var h in devices)
        {
            var id = h.Mac.Replace(":", "").ToLowerInvariant();
            seen.Add(id);
            var name = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
            var print = $"{h.Online}|{h.Ip}|{name}|{h.Subnet}";
            if (_published.TryGetValue(id, out var was) && was == print) continue;
            if (Discovery && was is null)
            {
                var config = JsonSerializer.Serialize(new
                {
                    name,
                    unique_id = $"{Prefix}_{id}",
                    state_topic = $"{Prefix}/{id}/state",
                    payload_home = "home", payload_not_home = "not_home",
                    json_attributes_topic = $"{Prefix}/{id}/attributes",
                    source_type = "router",
                    availability_topic = $"{Prefix}/status",
                    device = new { identifiers = new[] { $"{Prefix}_{id}" }, name, manufacturer = h.Vendor == "" ? "Unknown" : h.Vendor, model = "Seen by BAMF", via_device = $"{Prefix}_server" },
                });
                await PublishAsync($"{DiscoveryPrefix}/device_tracker/{Prefix}_{id}/config", config, true, ct);
            }
            await PublishAsync($"{Prefix}/{id}/state", h.Online ? "home" : "not_home", true, ct);
            await PublishAsync($"{Prefix}/{id}/attributes", JsonSerializer.Serialize(new
            {
                ip = h.Ip, mac = h.Mac, name, vendor = h.Vendor, network = h.Subnet, hostname = h.Hostname,
                last_seen = h.LastSeen, latency_ms = latency.TryGetValue(h.Id, out var ms) ? ms : null, known = h.Known, watched = h.Watched,
            }), true, ct);
            _published[id] = print;
        }
        // A device that is no longer tracked (ignored, forgotten, deleted) is taken back.
        foreach (var id in _published.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (Discovery) await PublishAsync($"{DiscoveryPrefix}/device_tracker/{Prefix}_{id}/config", "", true, ct);
            await PublishAsync($"{Prefix}/{id}/state", "", true, ct);
            await PublishAsync($"{Prefix}/{id}/attributes", "", true, ct);
            _published.Remove(id);
        }
    }

    // ------------------------------------------------------------ the wire

    private async Task ConnectAsync(CancellationToken ct)
    {
        var host = _config["Bamf:Mqtt:Server"]!.Trim();
        var port = _config.GetValue("Bamf:Mqtt:Port", 1883);
        var tcp = new TcpClient();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeout.Token);
        }
        Stream stream = tcp.GetStream();
        if (_config.GetValue("Bamf:Mqtt:Tls", false))
        {
            var ssl = new SslStream(stream, false);
            await ssl.AuthenticateAsClientAsync(host);
            stream = ssl;
        }
        _tcp = tcp; _stream = stream;

        var user = _config["Bamf:Mqtt:Username"];
        var pass = _config["Bamf:Mqtt:Password"];
        var clientId = _config["Bamf:Mqtt:ClientId"] ?? "bamf";
        var flags = 0x02 | 0x04 | 0x20;   // clean session, will, will retain
        if (!string.IsNullOrEmpty(user)) flags |= 0x80;
        if (!string.IsNullOrEmpty(pass)) flags |= 0x40;
        var v = new List<byte>();
        v.AddRange(Str("MQTT")); v.Add(4); v.Add((byte)flags); v.Add(0); v.Add(60);
        v.AddRange(Str(clientId));
        v.AddRange(Str($"{Prefix}/status")); v.AddRange(Str("offline"));
        if (!string.IsNullOrEmpty(user)) v.AddRange(Str(user));
        if (!string.IsNullOrEmpty(pass)) v.AddRange(Str(pass));
        await SendAsync(Packet(0x10, v.ToArray()), ct);

        var ack = new byte[4];
        var got = 0;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (got < 4) { var n = await stream.ReadAsync(ack.AsMemory(got, 4 - got), timeout.Token); if (n == 0) throw new IOException("the broker closed the connection"); got += n; }
        }
        if (ack[0] != 0x20 || ack[3] != 0)
            throw new IOException(ack[3] switch { 1 => "the broker doesn't speak MQTT 3.1.1", 4 => "bad username or password", 5 => "not authorised", _ => $"connect refused (code {ack[3]})" });
        Connected = true;
        LastError = null;
        _ = Task.Run(() => DrainAsync(stream, ct), ct);
    }

    // Whatever the broker sends (ping responses, mostly) is read and dropped;
    // the read failing is how a lost connection is noticed.
    private async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await stream.ReadAsync(one, ct) == 0) break;
                var len = 0; var mult = 1;
                for (var i = 0; i < 4; i++)
                {
                    if (await stream.ReadAsync(one, ct) == 0) throw new IOException("closed");
                    len += (one[0] & 0x7F) * mult; mult *= 128;
                    if ((one[0] & 0x80) == 0) break;
                }
                var skip = new byte[Math.Min(len, 65536)];
                while (len > 0) { var n = await stream.ReadAsync(skip.AsMemory(0, Math.Min(len, skip.Length)), ct); if (n == 0) throw new IOException("closed"); len -= n; }
            }
        }
        catch { }
        Connected = false;
    }

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        var v = new List<byte>();
        v.AddRange(Str(topic)); v.AddRange(Encoding.UTF8.GetBytes(payload));
        await SendAsync(Packet((byte)(0x30 | (retain ? 1 : 0)), v.ToArray()), ct);
        Published++;
        LastPublishUtc = DateTime.UtcNow;
    }

    private async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        var s = _stream ?? throw new IOException("not connected");
        await _send.WaitAsync(ct);
        try { await s.WriteAsync(packet, ct); await s.FlushAsync(ct); }
        finally { _send.Release(); }
    }

    private void Close()
    {
        Connected = false;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null; _tcp = null;
    }

    private static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new byte[] { (byte)(b.Length >> 8), (byte)b.Length }.Concat(b).ToArray();
    }
    private static byte[] Packet(byte type, byte[] body)
    {
        var out_ = new List<byte> { type };
        var len = body.Length;
        do { var d = (byte)(len % 128); len /= 128; if (len > 0) d |= 0x80; out_.Add(d); } while (len > 0);
        out_.AddRange(body);
        return out_.ToArray();
    }
}
