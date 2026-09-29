using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// Who may open BAMF. There are two passwords: the main one opens everything,
/// and the view-only one, which only counts alongside a main one, opens the
/// dashboard to look at. Each can be set in Settings, where it's kept hashed,
/// or in appsettings.json (Bamf:Password, Bamf:ViewerPassword); one set in
/// Settings wins over the file's. With no main password BAMF is open to anyone
/// who can reach it, as it always has been.
///
/// A browser signs in once and gets a cookie. Scripts, other BAMF servers and
/// Home Assistant go on sending the password with HTTP Basic auth. Either way,
/// too many wrong passwords from one address locks that address out a while.
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "bamf_session";
    public const int MinLength = 8;
    public static readonly TimeSpan SessionLife = TimeSpan.FromDays(30);
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);
    private const int Iterations = 210_000;

    private readonly HostStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthService> _log;
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new();
    private readonly ConcurrentDictionary<string, DateTime> _lockedUntil = new();
    private readonly object _secretLock = new();

    // Every request asks for these, so they're kept here rather than read from
    // the database each time: refreshed every few seconds, so a restored backup
    // shows, and at once when this class changes them.
    private static readonly string[] Keys = ["authPassword", "authViewerPassword", "authSecret"];
    private readonly ConcurrentDictionary<string, string?> _settings = new();
    private DateTime _settingsRead = DateTime.MinValue;
    private static readonly TimeSpan SettingsFresh = TimeSpan.FromSeconds(5);

    // Passwords already checked against a stored hash, so the deliberately slow
    // hash runs once per password rather than on every request that sends one.
    private readonly ConcurrentDictionary<string, byte> _verified = new();

    /// <summary>The clock; tests set their own.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Called when an address is locked out: the address and how many wrong passwords.</summary>
    public Action<string, int>? LockedOut { get; set; }

    public AuthService(HostStore store, IConfiguration config, ILogger<AuthService> log)
    {
        _store = store; _config = config; _log = log;
    }

    private string? FilePassword(string role) =>
        _config[role == "admin" ? "Bamf:Password" : "Bamf:ViewerPassword"] is { Length: > 0 } p ? p : null;

    private string? Setting(string key)
    {
        if (DateTime.UtcNow - _settingsRead > SettingsFresh)
        {
            foreach (var k in Keys) _settings[k] = _store.GetSetting(k);
            _settingsRead = DateTime.UtcNow;
        }
        return _settings.GetValueOrDefault(key);
    }

    private void Save(string key, string? value)
    {
        if (value is null) _store.DeleteSetting(key); else _store.SetSetting(key, value);
        _settings[key] = value;
    }

    private string? StoredHash(string role) =>
        Setting(role == "admin" ? "authPassword" : "authViewerPassword") is { Length: > 0 } h ? h : null;

    /// <summary>Where a role's password comes from: "settings", "file", or null for none.</summary>
    public string? Source(string role)
    {
        if (role == "viewer" && Source("admin") is null) return null;   // view-only needs a main password
        return StoredHash(role) is not null ? "settings" : FilePassword(role) is not null ? "file" : null;
    }

    /// <summary>Whether BAMF asks for a password at all.</summary>
    public bool Required => Source("admin") is not null;

    /// <summary>The role a password opens, or null if it opens nothing.</summary>
    public string? RoleFor(string password)
    {
        if (string.IsNullOrEmpty(password) || !Required) return null;
        foreach (var role in new[] { "admin", "viewer" })
            if (Matches(role, password)) return role;
        return null;
    }

    private bool Matches(string role, string password) => Source(role) switch
    {
        "settings" => VerifyCached(StoredHash(role)!, password),
        "file" => FixedEquals(FilePassword(role)!, password),
        _ => false,
    };

    private bool VerifyCached(string stored, string password)
    {
        var key = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(stored + "\0" + password)));
        if (_verified.ContainsKey(key)) return true;
        if (!Verify(stored, password)) return false;
        if (_verified.Count > 32) _verified.Clear();
        _verified[key] = 0;
        return true;
    }

    /// <summary>
    /// Sets or clears a password from Settings. The current main password is
    /// needed to change either, once there is one. Clearing the main password
    /// clears the view-only one too, since it means nothing on its own. A
    /// password from appsettings.json can be replaced here but not cleared.
    /// Returns what's wrong, or null when it's done.
    /// </summary>
    public string? SetPassword(string role, string? current, string? password)
    {
        if (role is not ("admin" or "viewer")) return "That isn't a password BAMF has.";
        if (Required && !Matches("admin", current ?? "")) return "The current password isn't right.";
        password ??= "";
        if (password.Length == 0)
        {
            if (Source(role) == "file")
                return "That password is set in appsettings.json. Remove it there, or set a new one here to replace it.";
            Save(role == "admin" ? "authPassword" : "authViewerPassword", null);
            if (role == "admin") Save("authViewerPassword", null);
            _log.LogInformation("Sign-in: the {Role} password was removed in Settings", role == "admin" ? "main" : "view-only");
            return null;
        }
        if (password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (role == "viewer")
        {
            if (!Required) return "Set a main password first: a view-only one only counts alongside it.";
            if (Matches("admin", password)) return "The view-only password has to be different from the main one.";
        }
        else if (Source("viewer") is not null && Matches("viewer", password))
            return "The main password has to be different from the view-only one.";
        Save(role == "admin" ? "authPassword" : "authViewerPassword", Hash(password));
        _log.LogInformation("Sign-in: the {Role} password was set in Settings", role == "admin" ? "main" : "view-only");
        return null;
    }

    /// <summary>
    /// For a forgotten password: clears the main and view-only passwords set in
    /// Settings, so BAMF is open again (or asks for appsettings.json's, if it
    /// has one) and a new one can be set. Only ever from the machine BAMF runs
    /// on; see Program.cs. Says whether there was anything to clear.
    /// </summary>
    public bool ResetSettingsPasswords(string how)
    {
        var had = StoredHash("admin") is not null || StoredHash("viewer") is not null;
        Save("authPassword", null);
        Save("authViewerPassword", null);
        if (had) _log.LogWarning("Sign-in: the passwords set in Settings were cleared, by {How}. " +
            (Required ? "BAMF now asks for the password in appsettings.json." : "BAMF is open until a new password is set.") + " Set one in Settings, under Security.", how);
        else _log.LogInformation("Sign-in: asked to clear the passwords set in Settings, by {How}, but there weren't any", how);
        return had;
    }

    // ---------- hashing ----------

    internal static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal static bool Verify(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var want = Convert.FromBase64String(parts[3]);
            var got = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, want.Length);
            return CryptographicOperations.FixedTimeEquals(got, want);
        }
        catch (FormatException) { return false; }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ---------- sessions ----------
    // A session is the role and when it ends, signed with a key kept in the
    // database. The signature also covers the role's password, so changing a
    // password signs out every browser that used the old one.

    private byte[] Secret()
    {
        lock (_secretLock)
        {
            if (Setting("authSecret") is { Length: > 0 } s) return Convert.FromBase64String(s);
            var key = RandomNumberGenerator.GetBytes(32);
            Save("authSecret", Convert.ToBase64String(key));
            return key;
        }
    }

    private string Fingerprint(string role) => Source(role) switch
    {
        "settings" => StoredHash(role)!,
        "file" => Convert.ToBase64String(HMACSHA256.HashData(Secret(), Encoding.UTF8.GetBytes("file:" + FilePassword(role)))),
        _ => "",
    };

    private byte[] Sign(string payload, string role) =>
        HMACSHA256.HashData(Secret(), Encoding.UTF8.GetBytes(payload + "|" + Fingerprint(role)));

    /// <summary>A signed session for a role, good for SessionLife.</summary>
    public string Issue(string role)
    {
        var payload = $"{role}|{new DateTimeOffset(Now().Add(SessionLife)).ToUnixTimeSeconds()}";
        return B64(Encoding.UTF8.GetBytes(payload)) + "." + B64(Sign(payload, role));
    }

    /// <summary>The role a session is for, and when it ends, or null if it's no good.</summary>
    public (string Role, DateTime Expires)? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Required) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0) return null;
        try
        {
            var payload = Encoding.UTF8.GetString(UnB64(token[..dot]));
            var parts = payload.Split('|');
            if (parts.Length != 2 || parts[0] is not ("admin" or "viewer") || !long.TryParse(parts[1], out var unix)) return null;
            if (Source(parts[0]) is null) return null;
            if (!CryptographicOperations.FixedTimeEquals(UnB64(token[(dot + 1)..]), Sign(payload, parts[0]))) return null;
            var expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            return expires > Now() ? (parts[0], expires) : null;
        }
        catch (FormatException) { return null; }
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    // ---------- wrong passwords ----------

    /// <summary>Whether an address is locked out, and for how much longer.</summary>
    public bool IsLocked(string address, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        if (!_lockedUntil.TryGetValue(address, out var until)) return false;
        if (until <= Now()) { _lockedUntil.TryRemove(address, out _); return false; }
        left = until - Now();
        return true;
    }

    /// <summary>A wrong password from an address; enough of them in a row locks it out.</summary>
    public void Failed(string address)
    {
        var now = Now();
        var list = _failures.GetOrAdd(address, _ => []);
        int count;
        lock (list)
        {
            list.RemoveAll(t => now - t > FailureWindow);
            list.Add(now);
            count = list.Count;
            if (count >= MaxFailures) list.Clear();
        }
        if (count < MaxFailures) return;
        _lockedUntil[address] = now + LockoutTime;
        _log.LogWarning("Sign-in: {Count} wrong passwords from {Address}; locked out for {Minutes} minutes",
            count, address, (int)LockoutTime.TotalMinutes);
        try { LockedOut?.Invoke(address, count); } catch (Exception ex) { _log.LogWarning(ex, "Sign-in: couldn't report the lockout"); }
    }

    /// <summary>The right password: an address's count of wrong ones starts again.</summary>
    public void Succeeded(string address) => _failures.TryRemove(address, out _);
}
