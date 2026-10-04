using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>
/// BAMF's own warnings and errors, kept in memory so the dashboard can show them: a webhook that wouldn't take an alert,
/// a router that refused a password, a scan that failed. Without it the only place they went was a log file on the server.
/// Only BAMF's own code is listed (not the web server's chatter), a problem that repeats is one row with a count, and
/// anything that looks like a web address is cut back to where it goes, so a webhook's secret path can't end up on screen.
/// What's kept is since BAMF started; it isn't saved.
/// </summary>
public sealed class ProblemLog : ILoggerProvider
{
    public const int MaxRows = 100;

    public sealed record Row(string Source, string Level, string Message, int Count, DateTime FirstUtc, DateTime LastUtc);

    private readonly object _lock = new();
    private readonly List<Row> _rows = new();

    /// <summary>When this log started, so "nothing since BAMF started" can say how long that is.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    private static readonly Regex Url = new(@"https?://[^\s""'<>)]+", RegexOptions.Compiled);

    /// <summary>A web address cut back to its scheme and host: where it goes, never the path or query that may carry a secret.</summary>
    internal static string Redact(string text) =>
        Url.Replace(text, m => Uri.TryCreate(m.Value.TrimEnd('.', ',', ';'), UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}/…" : "(a web address)");

    /// <summary>Notes one problem. The same problem again adds to its count and moves to the top.</summary>
    public void Add(string source, string level, string message, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        message = Redact(message.Trim());
        if (message.Length > 400) message = message[..400] + "…";
        lock (_lock)
        {
            var i = _rows.FindIndex(r => r.Source == source && r.Message == message);
            if (i >= 0)
            {
                var r = _rows[i];
                _rows.RemoveAt(i);
                _rows.Insert(0, r with { Count = r.Count + 1, LastUtc = now, Level = level == "error" || r.Level == "error" ? "error" : "warning" });
            }
            else
            {
                _rows.Insert(0, new Row(source, level, message, 1, now, now));
                if (_rows.Count > MaxRows) _rows.RemoveAt(_rows.Count - 1);
            }
        }
    }

    public List<Row> Rows() { lock (_lock) return _rows.ToList(); }
    public void Clear() { lock (_lock) _rows.Clear(); }

    // ---- logging plumbing ----
    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("LanWatch", StringComparison.Ordinal) ? new Logger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]) : NullLogger.Instance;

    public void Dispose() { }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class Logger(ProblemLog owner, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            if (exception is not null) text += $": {exception.GetBaseException().Message}";
            owner.Add(source, logLevel >= LogLevel.Error ? "error" : "warning", text);
        }
    }
}
