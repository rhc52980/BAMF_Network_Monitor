using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BAMF.Tests;

/// <summary>The Problems card's log: only BAMF's own warnings and errors, repeats counted once, web addresses cut back so no secret shows, and a refused alert listed.</summary>
[Collection("Endpoints")]
public class ProblemLogTests
{
    private static readonly DateTime T = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_problem_that_repeats_is_one_row_with_a_count_and_goes_to_the_top()
    {
        var log = new ProblemLog();
        log.Add("RemoteService", "warning", "Remote Cabin: HTTP 401", T);
        log.Add("ScannerService", "warning", "A scan was slow", T.AddMinutes(1));
        log.Add("RemoteService", "warning", "Remote Cabin: HTTP 401", T.AddMinutes(2));
        var rows = log.Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Remote Cabin: HTTP 401", rows[0].Message);
        Assert.Equal(2, rows[0].Count);
        Assert.Equal(T, rows[0].FirstUtc);
        Assert.Equal(T.AddMinutes(2), rows[0].LastUtc);
    }

    [Fact]
    public void An_error_stays_an_error_once_one_has_been_seen()
    {
        var log = new ProblemLog();
        log.Add("X", "warning", "it broke", T);
        log.Add("X", "error", "it broke", T);
        log.Add("X", "warning", "it broke", T);
        Assert.Equal("error", log.Rows().Single().Level);
    }

    [Theory]
    [InlineData("It answered https://discord.com/api/webhooks/123/SECRET-TOKEN badly", "https://discord.com/…")]
    [InlineData("Couldn't reach http://10.0.0.5:3001/api/push/SECRET?status=up.", "http://10.0.0.5:3001/…")]
    public void A_web_address_is_cut_back_to_where_it_goes(string text, string shown)
    {
        var log = new ProblemLog();
        log.Add("X", "warning", text, T);
        var m = log.Rows().Single().Message;
        Assert.Contains(shown, m);
        Assert.DoesNotContain("SECRET", m);
    }

    [Fact]
    public void Only_BAMFs_own_warnings_and_errors_are_kept()
    {
        var log = new ProblemLog();
        var own = log.CreateLogger("LanWatch.Services.RemoteService");
        var other = log.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");
        own.LogInformation("fine");
        own.LogWarning("Remote {Name}: {Error}", "Cabin", "HTTP 401");
        own.LogError(new InvalidOperationException("the real reason"), "It failed");
        other.LogWarning("not ours");
        var rows = log.Rows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("RemoteService", r.Source));
        Assert.Contains(rows, r => r.Message == "Remote Cabin: HTTP 401" && r.Level == "warning");
        Assert.Contains(rows, r => r.Message == "It failed: the real reason" && r.Level == "error");
    }

    [Fact]
    public void It_keeps_at_most_a_hundred_rows_and_drops_the_oldest()
    {
        var log = new ProblemLog();
        for (var i = 0; i < ProblemLog.MaxRows + 20; i++) log.Add("X", "warning", "problem " + i, T.AddSeconds(i));
        var rows = log.Rows();
        Assert.Equal(ProblemLog.MaxRows, rows.Count);
        Assert.Equal("problem " + (ProblemLog.MaxRows + 19), rows[0].Message);
        Assert.DoesNotContain(rows, r => r.Message == "problem 0");
    }

    [Fact]
    public async Task An_alert_a_destination_refuses_shows_up_without_its_secret_address_and_can_be_cleared()
    {
        var factory = new RefusingFactory();
        using var app = new BamfApp().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(factory)));
        var store = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        store.SetSetting("webhookUrl", "https://hooks.example/api/webhooks/777/SECRET-PATH");
        await scanner.SendGenericAlert("The NAS is down", "It stopped answering.", "rule", default);

        var c = app.CreateClient();
        var body = await c.GetStringAsync("/api/problems");
        Assert.DoesNotContain("SECRET-PATH", body);
        var j = JsonDocument.Parse(body).RootElement;
        var row = j.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("message").GetString()!.Contains("wasn't taken"));
        Assert.Contains("HTTP 403", row.GetProperty("message").GetString());
        Assert.Equal("warning", row.GetProperty("level").GetString());

        Assert.True((await c.PostAsync("/api/problems/clear", null)).IsSuccessStatusCode);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/problems")).GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("message").GetString()!.Contains("wasn't taken")));
    }

    private sealed class RefusingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
}
