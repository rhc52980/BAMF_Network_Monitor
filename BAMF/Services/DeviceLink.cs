namespace LanWatch.Services;

/// <summary>
/// Turns a per-host link override into the URL the dashboard actually opens.
/// The result ends up in an href, so anything that isn't plainly http(s) is
/// rejected rather than sanitised - a stored "javascript:..." must never
/// become a clickable link.
/// </summary>
public static class DeviceLink
{
    public const string DefaultTemplate = "http://{ip}";

    /// <summary>
    /// Accepts, in order of convenience:
    ///   ""            -> the global template (usually http://{ip})
    ///   "8006"        -> http://&lt;ip&gt;:8006
    ///   ":8006/admin" -> http://&lt;ip&gt;:8006/admin
    ///   "https://{ip}:8006" or a full absolute URL, with {ip} substituted
    /// Anything that doesn't resolve to an http(s) URL falls back to http://&lt;ip&gt;.
    /// </summary>
    public static string Resolve(string? raw, string ip, string? globalTemplate)
    {
        var fallback = $"http://{ip}";
        var value = (raw ?? "").Trim();

        if (value.Length == 0)
        {
            var tpl = string.IsNullOrWhiteSpace(globalTemplate) ? DefaultTemplate : globalTemplate.Trim();
            value = tpl;
        }

        value = value.Replace("{ip}", ip, StringComparison.OrdinalIgnoreCase);

        // Bare port, e.g. "8006"
        if (value.All(char.IsDigit) && value.Length is > 0 and <= 5 && int.TryParse(value, out var port) && port is > 0 and <= 65535)
            value = $"http://{ip}:{port}";
        // Port with a path, e.g. ":8006/admin"
        else if (value.StartsWith(':'))
            value = $"http://{ip}{value}";
        // Scheme-less, e.g. "192.168.1.1:8006" or "nas.local/admin"
        else if (!value.Contains("://"))
            value = $"http://{value}";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return fallback;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return fallback;

        return uri.ToString();
    }
}
