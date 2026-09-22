using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Which recorded switches BAMF reads traffic counters from over SNMP, and how
/// to reach them. The address is the switch's own device unless one is given;
/// the community is SNMP v2c's password, "public" unless changed. It's only
/// ever handed back to the dashboard as "one is saved", never the value.
/// </summary>
public partial class HostStore
{
    public sealed record SnmpConfig(long SwitchId, bool Enabled, string Address, string Community);

    private static void InitSnmp(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS switch_snmp (
                switch_id INTEGER PRIMARY KEY,
                enabled   INTEGER NOT NULL,
                address   TEXT NOT NULL DEFAULT '',
                community TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public List<SnmpConfig> GetSnmpConfigs()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT switch_id, enabled, address, community FROM switch_snmp";
            var list = new List<SnmpConfig>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new SnmpConfig(r.GetInt64(0), r.GetInt32(1) != 0, r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    /// <summary>Saves a switch's SNMP settings. A null community keeps the saved one.</summary>
    public void SaveSnmpConfig(long switchId, bool enabled, string address, string? community)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = community is null
                ? """
                  INSERT INTO switch_snmp (switch_id, enabled, address, community) VALUES ($id, $on, $addr, '')
                  ON CONFLICT(switch_id) DO UPDATE SET enabled = $on, address = $addr
                  """
                : """
                  INSERT INTO switch_snmp (switch_id, enabled, address, community) VALUES ($id, $on, $addr, $comm)
                  ON CONFLICT(switch_id) DO UPDATE SET enabled = $on, address = $addr, community = $comm
                  """;
            cmd.Parameters.AddWithValue("$id", switchId);
            cmd.Parameters.AddWithValue("$on", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$addr", address);
            if (community is not null) cmd.Parameters.AddWithValue("$comm", community);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteSnmpConfig(long switchId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM switch_snmp WHERE switch_id = $id";
            cmd.Parameters.AddWithValue("$id", switchId);
            cmd.ExecuteNonQuery();
        }
    }
}
