using System.Net;
using System.Net.Mail;

namespace LanWatch.Services;

/// <summary>
/// Where an email alert goes and how to reach the mail server: kept as one URL so an email destination sits in the same list
/// as the others and is masked the same way (the dashboard shows only smtp://host, never the password).
///
///   smtp://user:password@mail.example.com:587/?to=you@example.com,them@example.com&amp;from=bamf@example.com&amp;tls=1
///
/// tls=1 (the default) starts TLS on the connection (STARTTLS, usually port 587); tls=0 sends in the clear, which only
/// belongs on a relay inside your own network. Implicit TLS on port 465 isn't supported by the mail client .NET ships.
/// The user and password are optional, for a relay that doesn't ask. The from address defaults to the user, or to bamf@host.
/// </summary>
public sealed record EmailTarget(string Host, int Port, string? User, string? Password, string From, IReadOnlyList<string> To, bool Tls)
{
    public static EmailTarget? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals("smtp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(uri.Host)) return null;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var to = (query["to"] ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => MailAddress.TryCreate(a, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (to.Count == 0 || to.Count > 10) return null;
        string? user = null, pass = null;
        if (uri.UserInfo.Length > 0)
        {
            var i = uri.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(i < 0 ? uri.UserInfo : uri.UserInfo[..i]);
            pass = i < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[(i + 1)..]);
        }
        var tls = query["tls"] != "0";
        var port = uri.IsDefaultPort || uri.Port <= 0 ? (tls ? 587 : 25) : uri.Port;
        var from = query["from"];
        if (string.IsNullOrWhiteSpace(from) || !MailAddress.TryCreate(from, out _))
            from = user is not null && MailAddress.TryCreate(user, out _) ? user : $"bamf@{uri.Host}";
        return new EmailTarget(uri.Host, port, user, pass, from!, to, tls);
    }

    /// <summary>The URL for a form's fields: what the dashboard builds and BAMF stores.</summary>
    public static string Build(string host, int port, bool tls, string? user, string? password, string? from, IEnumerable<string> to)
    {
        var info = string.IsNullOrEmpty(user) ? "" : Uri.EscapeDataString(user) + (string.IsNullOrEmpty(password) ? "" : ":" + Uri.EscapeDataString(password)) + "@";
        var q = "?to=" + Uri.EscapeDataString(string.Join(",", to)) + (string.IsNullOrWhiteSpace(from) ? "" : "&from=" + Uri.EscapeDataString(from)) + "&tls=" + (tls ? "1" : "0");
        return $"smtp://{info}{host}:{port}/{q}";
    }
}

public partial class ScannerService
{
    /// <summary>How an email is sent: the target, the subject and the body. Swapped out by tests.</summary>
    internal Func<EmailTarget, string, string, CancellationToken, Task> EmailSender { get; set; } = SendSmtp;

    private async Task SendEmail(Destination d, string title, string text, CancellationToken ct)
    {
        var target = EmailTarget.Parse(d.Url) ?? throw new InvalidOperationException("The email settings can't be read; open the destination and save it again.");
        // A subject is one line, and not an essay.
        var subject = "[BAMF] " + title.Replace('\r', ' ').Replace('\n', ' ');
        if (subject.Length > 150) subject = subject[..150];
        await EmailSender(target, subject, text, ct);
    }

    private static async Task SendSmtp(EmailTarget t, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(t.Host, t.Port) { EnableSsl = t.Tls, Timeout = 20000, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrEmpty(t.User)) client.Credentials = new NetworkCredential(t.User, t.Password ?? "");
        using var mail = new MailMessage { From = new MailAddress(t.From), Subject = subject, Body = body, SubjectEncoding = System.Text.Encoding.UTF8, BodyEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in t.To) mail.To.Add(a);
        await client.SendMailAsync(mail, ct);
    }
}
