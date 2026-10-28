using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>This week against last: the numbers for a period, two periods side by side, the sentence for a report, and the endpoint.</summary>
[Collection("Endpoints")]
public class CompareTests
{
    private static void Sql(HostStore store, string sql, params (string, object)[] args)
    {
        using var conn = new SqliteConnection($"Data Source={store.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
    private static string Hour(DateTime t) => t.ToString("yyyy-MM-ddTHH:00:00Z");

    /// <summary>A fortnight: two devices, one that arrived this week; three drops this week and one last; an outage each; more data this week; an alert last week.</summary>
    private static (long Old, long New, DateTime Now) Seed(HostStore store)
    {
        var now = DateTime.UtcNow;
        store.UpsertSeen("ee:00:00:00:60:01", "192.168.30.221", "old", "Acme", "192.168.30.0/24");
        store.UpsertSeen("ee:00:00:00:60:02", "192.168.30.222", "fresh", "Acme", "192.168.30.0/24");
        var old = store.GetAll().Single(h => h.Mac == "ee:00:00:00:60:01").Id;
        var fresh = store.GetAll().Single(h => h.Mac == "ee:00:00:00:60:02").Id;
        Sql(store, "UPDATE hosts SET first_seen = $a WHERE id = $id", ("$a", now.AddDays(-20).ToString("o")), ("$id", old));
        Sql(store, "UPDATE hosts SET first_seen = $a WHERE id = $id", ("$a", now.AddDays(-2).ToString("o")), ("$id", fresh));
        Sql(store, "DELETE FROM events");
        foreach (var d in new[] { 1, 3, 5 }) Sql(store, "INSERT INTO events (host_id, type, at) VALUES ($h, 'offline', $at)", ("$h", old), ("$at", now.AddDays(-d).ToString("o")));
        Sql(store, "INSERT INTO events (host_id, type, at) VALUES ($h, 'offline', $at)", ("$h", old), ("$at", now.AddDays(-10).ToString("o")));
        store.AddWanOutage(now.AddDays(-2), now.AddDays(-2).AddMinutes(7), false);
        store.AddWanOutage(now.AddDays(-9), now.AddDays(-9).AddMinutes(3), true);
        store.AddTraffic(new[] { ("ee:00:00:00:60:01", Hour(now.AddDays(-1)), 3_000_000_000L, 0L), ("ee:00:00:00:60:01", Hour(now.AddDays(-8)), 1_000_000_000L, 0L) });
        Sql(store, "INSERT INTO alerts (at, kind, title, detail) VALUES ($at, 'security', 'x', '')", ("$at", now.AddDays(-9).ToString("o")));
        return (old, fresh, now);
    }

    [Fact]
    public void A_period_counts_what_happened_in_it_and_nothing_outside()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var (_, _, now) = Seed(store);
        var week = store.Period(now.AddDays(-7), now);
        Assert.Equal(2, week.Devices);
        Assert.Equal(1, week.Arrived);
        Assert.Equal(3, week.Drops);
        Assert.Equal(1, week.Outages);
        Assert.Equal(7, week.OutageMinutes);
        Assert.Equal(3_000_000_000L, week.Bytes);
        Assert.Equal(0, week.Alerts);
        var before = store.Period(now.AddDays(-14), now.AddDays(-7));
        Assert.Equal(1, before.Devices);
        Assert.Equal(0, before.Arrived);
        Assert.Equal(1, before.Drops);
        Assert.Equal(1, before.Outages);
        Assert.Equal(3, before.OutageMinutes);
        Assert.Equal(1_000_000_000L, before.Bytes);
        Assert.Equal(1, before.Alerts);
        Assert.Equal(1, before.SecurityAlerts);
    }

    [Fact]
    public void Compared_metrics_say_which_way_is_good_and_the_sentence_says_only_what_moved()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var (_, _, now) = Seed(store);
        var today = now.ToString("yyyy-MM-dd");
        var m = store.Compare(7, now, new[] { (now.AddDays(-10).ToString("yyyy-MM-dd"), 70), (now.AddDays(-3).ToString("yyyy-MM-dd"), 80), (today, 90) },
            new[] { ("dhcp", now.AddDays(-1).ToString("o")) });
        var by = m.ToDictionary(x => x.Key);
        Assert.Equal(85, by["score"].Now); Assert.Equal(70, by["score"].Before); Assert.Equal("up", by["score"].Better);
        Assert.Equal(3, by["drops"].Now); Assert.Equal(1, by["drops"].Before); Assert.Equal("down", by["drops"].Better);
        Assert.Equal(7, by["outageMinutes"].Now); Assert.Equal(3, by["outageMinutes"].Before);
        Assert.Equal(1, by["alerts"].Now);                     // the traffic monitor's DHCP alert, kept outside the table, counts
        Assert.Equal(1, by["security"].Now); Assert.Equal(1, by["security"].Before);
        Assert.Null(by["bytes"].Better);
        Assert.Equal("", m.Single(x => x.Key == "drops").Unit);
        var s = HostStore.CompareSentence(m);
        Assert.Contains("15 points more on the network score", s);
        Assert.Contains("1 more new device", s);
        Assert.Contains("2 more device drops", s);
        Assert.DoesNotContain("security", s);                  // the same both weeks
        Assert.Equal("", HostStore.CompareSentence(new[] { new HostStore.CompareMetric("drops", "Drops", 2, 2, "", "down") }));
        Assert.Equal("4 minutes less without internet, 2.0 GB less of data moved",
            HostStore.CompareSentence(new[] { new HostStore.CompareMetric("outageMinutes", "m", 3, 7, "min", "down"), new HostStore.CompareMetric("bytes", "d", 1e9, 3e9, "bytes", null) }));
    }

    [Fact]
    public async Task The_endpoint_and_the_weekly_report_carry_the_comparison()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        Seed(store);
        var c = await app.Client().GetFromJsonAsync<JsonElement>("/api/compare?days=7");
        Assert.Equal(7, c.GetProperty("days").GetInt32());
        var drops = c.GetProperty("metrics").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "drops");
        Assert.Equal(3, drops.GetProperty("now").GetDouble());
        Assert.Equal(1, drops.GetProperty("before").GetDouble());
        Assert.Contains("2 more device drops", c.GetProperty("summary").GetString());
        var wide = await app.Client().GetFromJsonAsync<JsonElement>("/api/compare?days=400");
        Assert.Equal(45, wide.GetProperty("days").GetInt32());

        var reports = app.Services.GetRequiredService<ReportService>();
        var (_, text, fields) = reports.Compose(TimeSpan.FromDays(7));
        Assert.Contains("Compared with the week before: ", text);
        Assert.Contains("2 more device drops", text);
        Assert.Contains(fields, f => f.Name == "Compared with the week before");
        var (_, dayText, _) = reports.Compose(TimeSpan.FromDays(1));
        Assert.DoesNotContain("Compared with", dayText);        // a daily report has nothing worth comparing
    }
}
