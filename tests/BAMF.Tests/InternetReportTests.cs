using System.Net;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The internet report: what counts against the provider, what doesn't, what falls outside the period, and that nothing is injected.</summary>
[Collection("Endpoints")]
public class InternetReportTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static string Ago(double days) => Now.AddDays(-days).ToString("o");
    private static HostStore.WanOutage Out(double daysAgo, int minutes, bool local) => new(Ago(daysAgo), Now.AddDays(-daysAgo).AddMinutes(minutes).ToString("o"), minutes, local);
    private static HostStore.SpeedResult Test(double daysAgo, double down, double up, string? error = null) => new(Ago(daysAgo), down, up, 12, 2, "ATL", 100, false, error);

    private static InternetReport.Input Input(IEnumerable<HostStore.WanOutage>? outages = null, IEnumerable<HostStore.WanSlowSpell>? slow = null,
        IEnumerable<HostStore.SpeedResult>? tests = null, int days = 30) =>
        new(Now, days, "2.3.0", "8.8.8.8", 60, "203.0.113.42", (outages ?? []).ToList(), (slow ?? []).ToList(), (tests ?? []).ToList());

    [Fact]
    public void Only_outages_where_the_router_kept_answering_count_against_the_line()
    {
        var s = InternetReport.Summarize(Input([Out(2, 7, false), Out(5, 20, false), Out(6, 3, true)]));
        Assert.Equal(2, s.LineOutages);
        Assert.Equal(27, s.LineMinutes);
        Assert.Equal(20, s.LongestLineMinutes);
        Assert.Equal(1, s.HomeOutages);
        Assert.Equal(3, s.HomeMinutes);
    }

    [Fact]
    public void Whatever_is_older_than_the_period_is_left_out()
    {
        var outages = new[] { Out(2, 7, false), Out(45, 60, false) };
        Assert.Equal(1, InternetReport.Summarize(Input(outages, days: 30)).LineOutages);
        Assert.Equal(2, InternetReport.Summarize(Input(outages, days: 90)).LineOutages);
    }

    [Fact]
    public void Speed_is_the_median_of_the_tests_that_worked_and_failed_ones_are_counted_apart()
    {
        var s = InternetReport.Summarize(Input(tests: [Test(1, 300, 30), Test(2, 100, 10), Test(3, 200, 20), Test(4, 0, 0, "Cloudflare answered HTTP 503")]));
        Assert.Equal(3, s.GoodTests);
        Assert.Equal(1, s.FailedTests);
        Assert.Equal(200, s.MedianDown);
        Assert.Equal(20, s.MedianUp);
        Assert.Equal(100, s.MinDown);
        Assert.Equal(300, s.MaxDown);
    }

    [Fact]
    public void A_period_with_nothing_in_it_says_so_rather_than_inventing_numbers()
    {
        var html = InternetReport.Build(Input());
        Assert.Contains("None recorded in this period", html);
        Assert.Contains("No speed tests in this period", html);
        Assert.Contains("n/a", html);
    }

    [Fact]
    public void The_page_names_the_period_the_public_address_and_how_it_was_measured()
    {
        var html = InternetReport.Build(Input([Out(2, 7, false)], days: 7));
        Assert.Contains("The last 7 days", html);
        Assert.Contains("203.0.113.42", html);
        Assert.Contains("8.8.8.8", html);
        Assert.Contains("every 60 seconds", html);
        Assert.Contains("<time datetime=\"", html);
        Assert.Contains("window.print()", html);
        Assert.DoesNotContain("href=\"?days=7\"", html);   // the period it's already showing isn't offered again
        Assert.Contains("href=\"?days=30\"", html);
    }

    [Fact]
    public void Outages_that_took_the_router_down_are_listed_apart_and_not_called_the_lines()
    {
        var html = InternetReport.Build(Input([Out(1, 9, true)]));
        Assert.Contains("also took the router down", html);
        Assert.Contains("<b>0</b><span>outages on the line", html);
    }

    [Fact]
    public void A_server_name_or_error_with_markup_in_it_cant_inject_any()
    {
        var html = InternetReport.Build(Input(tests: [new HostStore.SpeedResult(Ago(1), 100, 10, 12, 2, "<script>alert(1)</script>", 100, false, null),
            Test(2, 0, 0, "<img src=x onerror=alert(1)>")]));
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task The_page_is_served_with_the_recorded_outages_and_the_days_are_limited()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.AddWanOutage(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-2).AddMinutes(12), false);
        var c = app.Client();
        var r = await c.GetAsync("/report/internet");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/html", r.Content.Headers.ContentType!.ToString());
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("12 min", html);
        Assert.Contains("The last 30 days", html);
        Assert.Contains("The last 365 days", await c.GetStringAsync("/report/internet?days=9999"));
        Assert.Contains("The last day", await c.GetStringAsync("/report/internet?days=0"));
    }
}
