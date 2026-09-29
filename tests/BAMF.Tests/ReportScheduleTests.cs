using LanWatch.Services;

namespace BAMF.Tests;

/// <summary>
/// When a scheduled report is due. The clock and the time zone are fixed, so
/// these don't depend on when or where they run. Eastern time, as on the home
/// server: in 2026 the clocks go forward on March 8 and back on November 1.
/// </summary>
public class ReportScheduleTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Off_is_never_due()
    {
        Assert.Null(ReportService.NextDue("off", 8, 1, Utc(2026, 9, 29, 11), Eastern));
        Assert.False(ReportService.IsDue("off", 8, 1, Utc(2026, 10, 1, 12, 30), null, Eastern));
    }

    [Fact]
    public void Daily_before_the_hour_is_due_today()
    {
        // 7:00 am EDT; the report goes at 8.
        Assert.Equal(Utc(2026, 9, 29, 12), ReportService.NextDue("daily", 8, 1, Utc(2026, 9, 29, 11), Eastern));
    }

    [Fact]
    public void Daily_at_or_after_the_hour_is_due_tomorrow()
    {
        Assert.Equal(Utc(2026, 9, 30, 12), ReportService.NextDue("daily", 8, 1, Utc(2026, 9, 29, 12), Eastern));
        Assert.Equal(Utc(2026, 9, 30, 12), ReportService.NextDue("daily", 8, 1, Utc(2026, 9, 29, 13), Eastern));
    }

    [Fact]
    public void Weekly_is_due_on_its_day()
    {
        // From Tuesday September 29, Monday's report is on October 5.
        Assert.Equal(Utc(2026, 10, 5, 12), ReportService.NextDue("weekly", 8, 1, Utc(2026, 9, 29, 11), Eastern));
    }

    [Fact]
    public void Monthly_is_due_on_the_first()
    {
        // The server's schedule: 8 am on the 1st, which is 12:00 UTC in October.
        Assert.Equal(Utc(2026, 10, 1, 12), ReportService.NextDue("monthly", 8, 1, Utc(2026, 9, 29, 11), Eastern));
    }

    [Fact]
    public void Monthly_after_the_clocks_go_back_uses_standard_time()
    {
        // November 1 is the day the clocks go back; by 8 am it's EST, UTC-5.
        Assert.Equal(Utc(2026, 11, 1, 13), ReportService.NextDue("monthly", 8, 1, Utc(2026, 10, 15, 12), Eastern));
    }

    [Fact]
    public void An_hour_the_clocks_skip_moves_to_the_next_day()
    {
        // 2 am on March 8 doesn't exist: the clocks go from 1:59 to 3:00. Asking
        // on the evening before used to throw, taking the Settings page with it.
        var next = ReportService.NextDue("daily", 2, 1, Utc(2026, 3, 7, 23), Eastern);
        Assert.Equal(Utc(2026, 3, 9, 6), next);   // 2 am EDT on the 9th
    }

    [Fact]
    public void An_hour_that_happens_twice_is_still_due()
    {
        // 1 am on November 1 happens twice; the report is due, once.
        var next = ReportService.NextDue("daily", 1, 1, Utc(2026, 10, 31, 12), Eastern);
        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 11, 1), TimeZoneInfo.ConvertTimeFromUtc(next!.Value, Eastern).Date);
    }

    [Fact]
    public void Monthly_goes_in_its_hour_on_the_first()
    {
        Assert.True(ReportService.IsDue("monthly", 8, 1, Utc(2026, 10, 1, 12, 30), null, Eastern));
        Assert.False(ReportService.IsDue("monthly", 8, 1, Utc(2026, 10, 1, 13), null, Eastern));   // 9 am
        Assert.False(ReportService.IsDue("monthly", 8, 1, Utc(2026, 10, 2, 12, 30), null, Eastern)); // the 2nd
    }

    [Fact]
    public void A_report_goes_once_in_its_hour()
    {
        // Checked every minute: sent at 8:01, it isn't sent again at 8:02.
        Assert.False(ReportService.IsDue("monthly", 8, 1, Utc(2026, 10, 1, 12, 2), Utc(2026, 10, 1, 12, 1), Eastern));
        Assert.False(ReportService.IsDue("daily", 8, 1, Utc(2026, 10, 1, 12, 59), Utc(2026, 10, 1, 12, 1), Eastern));
    }

    [Fact]
    public void Last_times_report_doesnt_stop_this_times()
    {
        Assert.True(ReportService.IsDue("monthly", 8, 1, Utc(2026, 10, 1, 12, 1), Utc(2026, 9, 1, 12, 1), Eastern));
        Assert.True(ReportService.IsDue("daily", 8, 1, Utc(2026, 10, 2, 12, 1), Utc(2026, 10, 1, 12, 5), Eastern));
        Assert.True(ReportService.IsDue("weekly", 8, 1, Utc(2026, 10, 12, 12, 1), Utc(2026, 10, 5, 12, 1), Eastern));
    }

    [Fact]
    public void Weekly_goes_only_on_its_day()
    {
        Assert.True(ReportService.IsDue("weekly", 8, 1, Utc(2026, 10, 5, 12, 10), null, Eastern));   // Monday
        Assert.False(ReportService.IsDue("weekly", 8, 1, Utc(2026, 10, 6, 12, 10), null, Eastern));  // Tuesday
    }

    [Fact]
    public void Each_schedule_looks_back_over_its_period()
    {
        Assert.Equal(TimeSpan.FromDays(1), ReportService.PeriodFor("daily"));
        Assert.Equal(TimeSpan.FromDays(7), ReportService.PeriodFor("weekly"));
        Assert.Equal(TimeSpan.FromDays(30), ReportService.PeriodFor("monthly"));
    }
}
