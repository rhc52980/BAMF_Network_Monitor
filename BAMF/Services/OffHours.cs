namespace LanWatch.Services;

/// <summary>
/// The hours a device should be off: the TV between 23:00 and 07:00, the kids' tablet on school nights. Plain rules on plain times, no
/// learning: a device found online inside its off hours is one alert per night. Times are the server's local time; a window may cross
/// midnight, and its days are the days it starts on (Monday first), so "Friday 23:00 to 07:00" runs into Saturday morning.
/// </summary>
public sealed record OffWindow(string From, string To, int Days);

public static class OffHours
{
    public const int AllDays = 127;
    private static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary>Minutes after midnight from "HH:mm" (24-hour), or false.</summary>
    public static bool TryMinutes(string? s, out int minutes)
    {
        minutes = 0;
        if (s is null || s.Length != 5 || s[2] != ':') return false;
        if (!int.TryParse(s.AsSpan(0, 2), out var h) || !int.TryParse(s.AsSpan(3, 2), out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    /// <summary>What is wrong with a window, for the person, or null when it is fine.</summary>
    public static string? Problem(string? from, string? to, int days)
    {
        if (!TryMinutes(from, out var f) || !TryMinutes(to, out var t)) return "Give the times as hours and minutes, like 23:00 and 07:00.";
        if (f == t) return "The hours it should be off can't start and end at the same time.";
        if (days is < 1 or > AllDays) return "Pick at least one day.";
        return null;
    }

    private static int Bit(DayOfWeek d) => ((int)d + 6) % 7;                       // Monday 0 ... Sunday 6
    private static bool Has(OffWindow w, DateTime day) => (w.Days >> Bit(day.DayOfWeek) & 1) == 1;

    /// <summary>Whether this local moment is inside the window.</summary>
    public static bool Within(OffWindow w, DateTime local)
    {
        if (!TryMinutes(w.From, out var f) || !TryMinutes(w.To, out var t) || f == t) return false;
        var min = local.Hour * 60 + local.Minute;
        if (f < t) return min >= f && min < t && Has(w, local);
        if (min >= f) return Has(w, local);                                           // the evening part, on the day it starts
        return min < t && Has(w, local.AddDays(-1));                                  // the morning part, belonging to yesterday's window
    }

    /// <summary>The local date the window this moment is in began on, so a night is one alert however many scans fall in it.</summary>
    public static DateTime StartDate(OffWindow w, DateTime local)
    {
        TryMinutes(w.From, out var f);
        TryMinutes(w.To, out var t);
        return f > t && local.Hour * 60 + local.Minute < t ? local.Date.AddDays(-1) : local.Date;
    }

    /// <summary>"23:00 to 07:00 every night"-style words for an alert or a label.</summary>
    public static string Describe(OffWindow w)
    {
        var days = w.Days == AllDays ? "every day"
            : w.Days == 31 ? "Monday to Friday"
            : w.Days == 96 ? "at weekends"
            : "on " + string.Join(", ", Enumerable.Range(0, 7).Where(i => (w.Days >> i & 1) == 1).Select(i => DayNames[i]));
        return $"{w.From} to {w.To}, {days}";
    }
}
