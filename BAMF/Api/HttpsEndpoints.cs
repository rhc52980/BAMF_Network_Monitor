using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>One-click HTTPS.</summary>
internal static class HttpsEndpoints
{
    public static void Map(WebApplication app, Func<object> HttpsJson)
    {
        app.MapPost("/api/settings/https", (HttpsRequest body) =>
        {
            var path = HttpsCert.PathFor(app.Configuration);
            switch (body.Action)
            {
                case "create":
                    if (HttpsCert.FileHttps) return Results.BadRequest(new { error = "appsettings.json already serves HTTPS, with its own certificate." });
                    var (names, addresses) = HttpsCert.LocalNames();
                    using (var cert = HttpsCert.Create(names, addresses, DateTimeOffset.UtcNow)) HttpsCert.Save(cert, path);
                    app.Logger.LogInformation("Made an HTTPS certificate for {Names}", string.Join(", ", names.Concat(addresses.Select(a => a.ToString()))));
                    return Results.Json(HttpsJson());
                case "remove":
                    if (File.Exists(path)) File.Delete(path);
                    app.Logger.LogInformation("Removed the HTTPS certificate");
                    return Results.Json(HttpsJson());
                default:
                    return Results.BadRequest(new { error = "Create or remove." });
            }
        });

        // The certificate alone, without its key, to install as trusted on a device
        // so its browser stops warning.
        app.MapGet("/api/settings/https/certificate", () =>
        {
            using var cert = HttpsCert.Load(HttpsCert.PathFor(app.Configuration));
            return cert is null ? Results.NotFound() : Results.File(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert), "application/x-x509-ca-cert", "bamf.crt");
        });
    }
}
