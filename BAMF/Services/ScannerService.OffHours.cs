namespace LanWatch.Services;

public partial class ScannerService
{
    /// <summary>
    /// Devices found on inside the hours they should be off: one alert for the night, whatever the number of scans that find them. A device that
    /// is ignored, forgotten, snoozed or offline is left alone. <paramref name="localNow"/> is for tests; the real call uses the server's clock.
    /// </summary>
    internal async Task RaiseOffHours(CancellationToken ct, DateTime? localNow = null)
    {
        if (!_store.OffHoursAlertEnabled) return;
        var windows = _store.GetOffHours();
        if (windows.Count == 0) return;
        var local = localNow ?? DateTime.Now;
        var hosts = _store.GetAll().ToDictionary(h => h.Id);
        foreach (var (id, w) in windows)
        {
            if (!hosts.TryGetValue(id, out var h) || !h.Online || h.Ignored || h.Forgotten || _store.IsSnoozed(id)) continue;
            if (!OffHours.Within(w, local)) continue;
            var night = OffHours.StartDate(w, local);
            if (!SecurityDue($"offhours|{id}|{night:yyyy-MM-dd}", TimeSpan.FromHours(30))) continue;
            var name = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
            var title = $"{name} is on in its off hours";
            var detail = $"It answered at {local:HH:mm}. You said it should be off {OffHours.Describe(w)}. " +
                         "Set or clear a device's off hours from its menu (⋯), under Off hours.";
            _log.LogWarning("Off hours: {Title}. {Detail}", title, detail);
            _store.AddAlert("schedule", title, detail);
            await SendGenericAlert(title, detail, "status", ct);
        }
    }
}
