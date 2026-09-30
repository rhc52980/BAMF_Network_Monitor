using System.Text;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>The main webhook, and sending alerts to it.</summary>
public partial class ScannerService
{
    /// <summary>
    /// Webhook endpoint. A URL saved from the dashboard wins over
    /// appsettings.json, matching how the other runtime settings behave, so
    /// changing it doesn't need a config edit or a restart.
    /// </summary>
    public string? WebhookUrl
    {
        get
        {
            var db = _store.GetSetting("webhookUrl");
            if (db is not null) return db.Length == 0 ? null : db;
            var cfg = _config["Bamf:WebhookUrl"];
            return string.IsNullOrWhiteSpace(cfg) ? null : cfg;
        }
    }

    /// <summary>
    /// How alerts are delivered to the webhook URL. "auto" keeps the original
    /// behaviour: a Discord URL gets a rich embed, anything else a generic JSON
    /// body. "ntfy" posts plain text with Title/Priority/Tags headers the way
    /// ntfy expects; "gotify" posts its {title, message, priority} JSON; "json"
    /// forces the generic body even for a Discord URL. Dashboard value wins over
    /// appsettings.json, like the URL itself.
    /// </summary>
    public string WebhookFormat
    {
        get
        {
            var v = (_store.GetSetting("webhookFormat") ?? _config["Bamf:WebhookFormat"] ?? "auto")
                .Trim().ToLowerInvariant();
            return v is "ntfy" or "gotify" or "json" or "discord" ? v : "auto";
        }
    }

    private static bool IsDiscordUrl(string url) =>
        url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("discordapp.com/api/webhooks", StringComparison.OrdinalIgnoreCase);

    /// <summary>The concrete format to use for a URL once "auto" is resolved.</summary>
    private static string ResolveFormat(string url, string format) =>
        format == "auto" ? (IsDiscordUrl(url) ? "discord" : "json") : format;

    /// <summary>
    /// One alert as an HTTP request in the resolved format. ntfy carries the
    /// title in a header, and headers are ASCII, so the title is stripped to
    /// ASCII and the device name - which may not be - lives in the body.
    /// Priority is on ntfy's 1-5 scale; Gotify's 0-10 gets double.
    /// </summary>
    private static HttpRequestMessage BuildAlertRequest(string url, string format, string title, string message,
        int priority, string tags, string discordPayload, string genericPayload)
    {
        switch (format)
        {
            case "ntfy":
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(message, Encoding.UTF8, "text/plain"),
                };
                var ascii = new string(title.Where(c => c < 128).ToArray()).Trim();
                if (ascii != "") req.Headers.TryAddWithoutValidation("Title", ascii);
                req.Headers.TryAddWithoutValidation("Priority", priority.ToString());
                if (tags != "") req.Headers.TryAddWithoutValidation("Tags", tags);
                return req;
            }
            case "gotify":
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { title, message, priority = priority * 2 }),
                        Encoding.UTF8, "application/json"),
                };
            case "discord":
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(discordPayload, Encoding.UTF8, "application/json"),
                };
            default:
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(genericPayload, Encoding.UTF8, "application/json"),
                };
        }
    }

    // ---------------- notifications ----------------

    private async Task Notify(string mac, string ip, string hostname, string vendor, string subnet, CancellationToken ct)
        => await SendWebhook(mac, ip, hostname, vendor, subnet, test: false, ct);

    /// <summary>
    /// Sends a test notification to one destination ("main" for the main
    /// webhook, else an extra's id), whatever kinds it takes. Returns null on
    /// success, else an error description.
    /// </summary>
    public async Task<string?> SendTestNotification(CancellationToken ct, string destination = "main")
    {
        if (!Destinations().Any(d => d.Id == destination))
            return destination == "main" ? "No webhook URL saved. Add one under Tools → Notifications." : "That destination isn't saved.";
        try
        {
            var ok = await SendWebhook("AA:BB:CC:DD:EE:FF", "192.0.2.123", "test-device",
                "BAMF Test", SubnetLabels.FirstOrDefault() ?? "192.0.2.0/24", test: true, ct, destination);
            return ok ? null : _lastDeliveryError ?? "The webhook endpoint returned a non-success status. Check the URL.";
        }
        catch (Exception ex)
        {
            return $"Webhook call failed: {ex.Message}";
        }
    }

    private async Task<bool> SendWebhook(string mac, string ip, string hostname, string vendor, string subnet,
        bool test, CancellationToken ct, string? only = null)
    {
        var title = test ? "BAMF webhook test" : "New host detected";
        var text = $"BAMF: {(test ? "webhook test - " : "")}new host {mac} at {ip} on {subnet}" +
                   (hostname != "" ? $" ({hostname})" : "") +
                   (vendor != "" ? $" [{vendor}]" : "");

        HttpRequestMessage Build(string url, string format)
        {
            var payload = "";
            if (format == "discord")
            {
                // Rich Discord embed. Amber for real alerts, green for tests.
                payload = JsonSerializer.Serialize(new
                {
                    username = "BAMF",
                    embeds = new[]
                    {
                        new
                        {
                            title,
                            description = test
                                ? "If you can read this, notifications are wired up correctly."
                                : "An unknown device appeared on the network.",
                            color = test ? 0x3FDB7F : 0xFFB454,
                            fields = new object[]
                            {
                                new { name = "MAC",      value = $"`{mac}`", inline = true },
                                new { name = "IP",       value = $"`{ip}`",  inline = true },
                                new { name = "Network",  value = subnet,      inline = true },
                                new { name = "Hostname", value = hostname == "" ? "—" : hostname, inline = true },
                                new { name = "Vendor",   value = vendor == "" ? "—" : vendor,     inline = true },
                            },
                            timestamp = DateTime.UtcNow.ToString("o"),
                            footer = new { text = "Basic ARP Monitoring Framework" },
                        }
                    }
                });
            }
            // Generic JSON: "content" and "message" keep simple endpoints working.
            var generic = JsonSerializer.Serialize(new
            {
                content = text,
                message = text,
                mac, ip, hostname, vendor, subnet, test,
            });
            return BuildAlertRequest(url, format,
                title: title,
                message: text,
                priority: test ? 3 : 4,
                tags: test ? "test_tube" : "warning",
                discordPayload: payload, genericPayload: generic);
        }

        try { return await Deliver("devices", title, text, Build, ct, holdable: !test, only: only); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Webhook notification failed");
            if (test) throw;
            return false;
        }
    }
}
