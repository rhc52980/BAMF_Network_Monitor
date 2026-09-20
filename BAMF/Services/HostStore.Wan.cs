using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// What the internet watch records: one row a minute, holding how long the
/// gateway and the address beyond it took to answer, or -1 for no reply.
/// Outages are worked out from the rows rather than stored, so a gap caused by
/// BAMF itself being off doesn't get counted as the internet being down.
/// </summary>
public partial class HostStore
{
    public sealed record WanSample(string At, int Gateway, int Internet);
    public sealed record WanOutage(string Start, string End, int Minutes, bool Local);

    private static void InitWan(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wan_samples (
                at       TEXT PRIMARY KEY,
                gateway  INTEGER NOT NULL,
                internet INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS wan_outages (
                start   TEXT PRIMARY KEY,
                ended   TEXT NOT NULL,
                minutes INTEGER NOT NULL,
                local   INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddWanSample(int gatewayMs, int internetMs)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO wan_samples (at, gateway, internet) VALUES ($at, $g, $i)";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$g", gatewayMs);
            cmd.Parameters.AddWithValue("$i", internetMs);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Writes an outage down when it ends. The minute-by-minute readings are
    /// pruned with everything else, so without this the history of outages
    /// would quietly disappear after a few weeks; this row stays.
    /// </summary>
    public void AddWanOutage(DateTime start, DateTime ended, bool local)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO wan_outages (start, ended, minutes, local) VALUES ($s, $e, $m, $l)";
            cmd.Parameters.AddWithValue("$s", start.ToString("o"));
            cmd.Parameters.AddWithValue("$e", ended.ToString("o"));
            cmd.Parameters.AddWithValue("$m", Math.Max(1, (int)Math.Round((ended - start).TotalMinutes)));
            cmd.Parameters.AddWithValue("$l", local ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Outages as they were written down, newest first.</summary>
    public List<WanOutage> GetWanOutageLog(int limit = 50)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT start, ended, minutes, local FROM wan_outages ORDER BY start DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
            var list = new List<WanOutage>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new WanOutage(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3) != 0));
            return list;
        }
    }

    public List<WanSample> GetWanSamples(int hours)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT at, gateway, internet FROM wan_samples WHERE at >= $from ORDER BY at";
            cmd.Parameters.AddWithValue("$from", DateTime.UtcNow.AddHours(-hours).ToString("o"));
            var list = new List<WanSample>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new WanSample(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
            return list;
        }
    }

    /// <summary>Drops rows older than the history retention setting.</summary>
    public void PruneWan()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM wan_samples WHERE at < $from";
            cmd.Parameters.AddWithValue("$from", DateTime.UtcNow.AddDays(-RetentionDays).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Runs of samples where the internet didn't answer. A run only counts
    /// while BAMF was watching: a gap of more than five minutes between rows
    /// ends whatever was going on, since nothing was being measured.
    /// </summary>
    public List<WanOutage> GetWanOutages(int hours)
    {
        var rows = GetWanSamples(hours);
        var outages = new List<WanOutage>();
        DateTime? start = null, last = null;
        var localToo = true;
        foreach (var s in rows)
        {
            if (!DateTime.TryParse(s.At, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) continue;
            var down = s.Internet < 0;
            var gapped = last is { } l && at - l > TimeSpan.FromMinutes(5);
            if (start is { } st && (!down || gapped))
            {
                var end = gapped ? last!.Value : at;
                outages.Add(new WanOutage(st.ToString("o"), end.ToString("o"), Math.Max(1, (int)Math.Round((end - st).TotalMinutes)), localToo));
                start = null;
            }
            if (down && start is null) { start = at; localToo = s.Gateway < 0; }
            else if (down && s.Gateway >= 0) localToo = false;
            last = at;
        }
        if (start is { } open && last is { } fin)
            outages.Add(new WanOutage(open.ToString("o"), fin.ToString("o"), Math.Max(1, (int)Math.Round((fin - open).TotalMinutes)), localToo));
        outages.Reverse();
        return outages;
    }
}
