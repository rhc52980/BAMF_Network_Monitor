using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// Slack, Telegram and email as places alerts go; an alert when the public address changes; one when BAMF is signed into
/// from an address that never has; and the DNS watch and the line's quality counting toward the network score.
/// </summary>
[Collection("Endpoints")]
public class MoreAlertsTests
{
    // ---------- Slack and Telegram ----------

    [Fact]
    public void Slack_and_telegram_urls_are_recognised_and_post_what_they_expect()
    {
        Assert.Equal("slack", ScannerService.ResolveFormat("https://hooks.slack.com/services/T000/B000/XXXX", "auto"));
        Assert.Equal("telegram", ScannerService.ResolveFormat("https://api.telegram.org/bot123:ABC/sendMessage?chat_id=-100", "auto"));
        Assert.Equal("discord", ScannerService.ResolveFormat("https://discord.com/api/webhooks/1/x", "auto"));
        Assert.Equal("json", ScannerService.ResolveFormat("https://example.com/hook", "auto"));
        Assert.Equal("slack", ScannerService.ResolveFormat("https://example.com/hook", "slack"));

        using var slack = ScannerService.BuildAlertRequest("https://hooks.slack.com/services/T/B/X", "slack", "Garage cam is down", "garage-cam stopped answering", 4, "red_circle", "", "");
        var body = JsonDocument.Parse(slack.Content!.ReadAsStringAsync().Result).RootElement;
        Assert.Equal("*Garage cam is down*\ngarage-cam stopped answering", body.GetProperty("text").GetString());

        using var tg = ScannerService.BuildAlertRequest("https://api.telegram.org/bot1:A/sendMessage?chat_id=5", "telegram", "Garage cam is down", new string('x', 5000), 4, "", "", "");
        var tgBody = JsonDocument.Parse(tg.Content!.ReadAsStringAsync().Result).RootElement;
        Assert.StartsWith("Garage cam is down\nxxx", tgBody.GetProperty("text").GetString());
        Assert.Equal(4000, tgBody.GetProperty("text").GetString()!.Length);          // Telegram refuses over 4096
        Assert.Equal("https://api.telegram.org/bot1:A/sendMessage?chat_id=5", tg.RequestUri!.ToString());
    }

    // ---------- email ----------

    [Fact]
    public void An_email_target_is_built_from_its_boxes_and_read_back()
    {
        var url = EmailTarget.Build("smtp.example.com", 587, true, "me@example.com", "p@ss:w/rd", "bamf@example.com", new[] { "a@example.com", "b@example.com" });
        var t = EmailTarget.Parse(url)!;
        Assert.Equal("smtp.example.com", t.Host);
        Assert.Equal(587, t.Port);
        Assert.True(t.Tls);
        Assert.Equal("me@example.com", t.User);
        Assert.Equal("p@ss:w/rd", t.Password);               // survives being a URL
        Assert.Equal("bamf@example.com", t.From);
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, t.To);

        // Defaults: TLS on at 587; off at 25; the from address from the user, else bamf@host.
        Assert.Equal(587, EmailTarget.Parse("smtp://mail.example.com/?to=a@example.com")!.Port);
        var plain = EmailTarget.Parse("smtp://relay.lan/?to=a@example.com&tls=0")!;
        Assert.Equal((25, false, "bamf@relay.lan", (string?)null), (plain.Port, plain.Tls, plain.From, plain.User));

        Assert.Null(EmailTarget.Parse("smtp://mail.example.com/"));                           // nobody to send to
        Assert.Null(EmailTarget.Parse("smtp://mail.example.com/?to=not-an-address"));
        Assert.Null(EmailTarget.Parse("https://mail.example.com/?to=a@example.com"));          // not smtp
        Assert.Null(EmailTarget.Parse(""));
    }

    private static string Smtp(string to = "me@example.com") => EmailTarget.Build("smtp.example.com", 587, true, "user", "secret", "bamf@example.com", new[] { to });

    [Fact]
    public async Task An_email_destination_is_saved_masked_tested_and_gets_its_kinds_only()
    {
        using var app = new BamfApp();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        var sent = new List<(EmailTarget Target, string Subject, string Body)>();
        scanner.EmailSender = (t, s, b, _) => { sent.Add((t, s, b)); return Task.CompletedTask; };

        Assert.NotNull(scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Mail", "smtp://mail.example.com/", "email", null) }));      // no recipient
        Assert.Null(scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Mail", Smtp(), "email", new() { "security" }) }));

        // The dashboard sees the host only, never the password.
        var c = app.Client();
        var d = (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("destinations")[0];
        Assert.Equal("email", d.GetProperty("format").GetString());
        Assert.Equal("smtp://smtp.example.com", d.GetProperty("masked").GetString());
        Assert.DoesNotContain("secret", d.ToString());

        // The Test button sends whatever kinds it takes.
        var id = d.GetProperty("id").GetString()!;
        var test = await c.PostAsync($"/api/destinations/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var one = Assert.Single(sent);
        Assert.StartsWith("[BAMF] ", one.Subject);
        Assert.Equal("me@example.com", one.Target.To[0]);
        Assert.Equal("secret", one.Target.Password);

        // A security alert goes by email; a new-device alert, which it doesn't take, doesn't.
        sent.Clear();
        await scanner.SendGenericAlert("Telnet opened on nas", "Port 23 is open.", "security", CancellationToken.None);
        await scanner.SendGenericAlert("New host", "x", "devices", CancellationToken.None);
        var mail = Assert.Single(sent);
        Assert.Equal("[BAMF] Telnet opened on nas", mail.Subject);
        Assert.Contains("Port 23 is open.", mail.Body);

        // Editing it and leaving the URL empty keeps what was saved.
        Assert.Null(scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(id, "Mail 2", "", "email", new() { "security" }) }));
        Assert.Equal("smtp.example.com", EmailTarget.Parse(scanner.ExtraDestinations[0].Url)!.Host);
    }

    [Fact]
    public async Task A_mail_server_that_is_down_is_tried_again()
    {
        using var app = new BamfApp();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        var tries = 0;
        scanner.EmailSender = (_, _, _, _) => ++tries == 1 ? throw new InvalidOperationException("Connection refused") : Task.CompletedTask;
        scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Mail", Smtp(), "email", new() { "security" }) });

        await scanner.SendGenericAlert("The internet is down", "x", "security", CancellationToken.None);
        Assert.Equal(1, tries);
        Assert.Equal(1, scanner.RetryQueued);
        Assert.Equal(0, await scanner.RetryFailedAlerts(CancellationToken.None));                                    // too soon
        Assert.Equal(1, await scanner.RetryFailedAlerts(CancellationToken.None, DateTime.UtcNow.AddMinutes(2)));     // a minute on
        Assert.Equal(2, tries);
        Assert.Equal(0, scanner.RetryQueued);
    }

    // ---------- the public address ----------

    [Fact]
    public async Task A_changed_public_address_is_one_alert_and_the_first_one_learned_is_not()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<PublicAddressWatch>();
        var address = "203.0.113.10";
        watch.Lookup = _ => Task.FromResult<(string?, string?)>((address, null));

        Assert.Null(await watch.Check(CancellationToken.None));         // learned, nothing said
        Assert.Empty(store.GetAlerts(20));
        Assert.Null(await watch.Check(CancellationToken.None));         // the same: still nothing
        Assert.Empty(store.GetAlerts(20));

        address = "198.51.100.77";
        await watch.Check(CancellationToken.None);
        var a = Assert.Single(store.GetAlerts(20));
        Assert.Equal("Your public IP address changed", a.Title);
        Assert.Contains("198.51.100.77", a.Detail);
        Assert.Contains("203.0.113.10", a.Detail);

        await watch.Check(CancellationToken.None);                      // announced once
        Assert.Single(store.GetAlerts(20));

        // A change the speed test noticed is announced by the next check too.
        store.RecordExternalIp("192.0.2.9", "speedtest");
        await watch.Check(CancellationToken.None);
        Assert.Equal(2, store.GetAlerts(20).Count);
    }

    [Fact]
    public async Task The_address_watch_switch_starts_from_todays_address_and_looks_straight_away()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<PublicAddressWatch>();
        watch.Lookup = _ => Task.FromResult<(string?, string?)>(("203.0.113.5", null));
        store.RecordExternalIp("203.0.113.1", "speedtest");
        store.RecordExternalIp("203.0.113.2", "speedtest");              // a change from before the watch was on

        var c = app.Client();
        Assert.False(watch.Enabled);
        var on = await c.PostAsJsonAsync("/api/settings/address-watch", new { enabled = true });
        var d = await on.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(d.GetProperty("enabled").GetBoolean());
        // The old change isn't announced; the one the first look finds is.
        var a = Assert.Single(store.GetAlerts(20));
        Assert.Contains("203.0.113.5", a.Detail);
        Assert.Equal("203.0.113.5", d.GetProperty("externalIp").GetProperty("ip").GetString());

        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("addressWatch").GetBoolean());
        await c.PostAsJsonAsync("/api/settings/address-watch", new { enabled = false });
        Assert.False(watch.Enabled);
    }

    // ---------- sign-in from a new address ----------

    private static AuthService Auth(string dir, out HostStore store)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(dir, "bamf.db") }).Build();
        store = new HostStore(config);
        return new AuthService(store, config, NullLogger<AuthService>.Instance);
    }

    [Fact]
    public void An_address_is_new_once_and_remembered_across_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamf-signin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var auth = Auth(dir, out _);
            var seen = new List<string>();
            auth.NewAddress = seen.Add;
            auth.Succeeded("192.168.1.50");
            auth.Succeeded("192.168.1.50");
            auth.Succeeded("192.168.1.51");
            auth.Succeeded("127.0.0.1");                     // this machine isn't news
            auth.Succeeded("::1");
            Assert.Equal(new[] { "192.168.1.50", "192.168.1.51" }, seen);

            var again = Auth(dir, out _);                    // a restart: it still knows them
            var seen2 = new List<string>();
            again.NewAddress = seen2.Add;
            again.Succeeded("192.168.1.50");
            again.Succeeded("192.168.1.52");
            Assert.Equal(new[] { "192.168.1.52" }, seen2);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void The_remembered_addresses_stop_at_a_couple_of_hundred()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamf-signin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var auth = Auth(dir, out var store);
            for (var i = 0; i < AuthService.MaxKnownAddresses + 20; i++) auth.Succeeded($"10.1.{i / 250}.{i % 250 + 1}");
            var kept = JsonSerializer.Deserialize<List<string>>(store.GetSetting("signInAddresses")!)!;
            Assert.Equal(AuthService.MaxKnownAddresses, kept.Count);
            Assert.DoesNotContain("10.1.0.1", kept);          // the oldest went
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_sign_in_alert_switch_round_trips_and_is_off_by_default()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var e = (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable");
        Assert.False(e.GetProperty("signInAlert").GetBoolean());
        var on = await c.PostAsJsonAsync("/api/settings/sign-in-alert", new { enabled = true });
        Assert.True((await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("signInAlert").GetBoolean());
    }

    // ---------- the score ----------

    private static readonly HealthScore.Inputs Clean = new(0, 0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0, false, false, false);

    [Fact]
    public void A_lying_resolver_and_a_poor_line_take_points_off()
    {
        var s = HealthScore.Compute(Clean with { DnsWrong = 1, DnsInvents = true, QualityProblems = 2 });
        Assert.Equal(100 - 15 - 3 - 6, s.Value);
        Assert.Contains(s.Reasons, r => r.Text == "Your DNS gives wrong answers for a name with a fixed address" && r.Points == 15);
        Assert.Contains(s.Reasons, r => r.Text == "Your DNS makes up answers for names that don't exist" && r.Points == 3);
        Assert.Contains(s.Reasons, r => r.Text == "The internet connection has 2 quality problems now" && r.Points == 6);
        Assert.Equal(100, HealthScore.Compute(Clean).Value);
    }

    [Fact]
    public async Task The_score_reads_the_dns_watchs_last_answer()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var dns = app.Services.GetRequiredService<DnsWatch>();
        var score = app.Services.GetRequiredService<HealthScore>();
        store.SetSetting("dnsWatch", "true");
        dns.Servers = () => new List<IPAddress> { IPAddress.Parse("192.168.1.1") };
        dns.Query = (_, name, _) => Task.FromResult(name == "dns.google"
            ? new DnsClient.Answer(new List<IPAddress> { IPAddress.Parse("185.220.101.4") }, 10, 0, null)
            : new DnsClient.Answer(DnsWatch.Canaries.TryGetValue(name, out var ips) ? ips.Select(IPAddress.Parse).ToList() : new(), 10, DnsWatch.Canaries.ContainsKey(name) ? 0 : 3, null));
        await dns.Check(CancellationToken.None);
        Assert.Equal(1, score.Gather().DnsWrong);
        Assert.Contains(score.Now().Reasons, r => r.Text.StartsWith("Your DNS gives wrong answers"));

        store.SetSetting("dnsWatch", "false");                         // off: not counted
        Assert.Equal(0, score.Gather().DnsWrong);
    }
}
