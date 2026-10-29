using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using ZonoLicence;

// Zono licence server: activates and refreshes Zono Workforce licences, with an admin page for ZonoSoft.
// Settings (environment): ZONO_DATA (folder for the database and signing key), ZONO_ADMIN_KEY_FILE, ZONO_URLS.
var builder = WebApplication.CreateBuilder(args);
var dataDir = Environment.GetEnvironmentVariable("ZONO_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
var adminKeyFile = Environment.GetEnvironmentVariable("ZONO_ADMIN_KEY_FILE") ?? Path.Combine(dataDir, "admin.key");
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ZONO_URLS") ?? "http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new LicenceStore(Path.Combine(dataDir, "licences.db")));
builder.Services.AddSingleton(_ => new TokenSigner(Path.Combine(dataDir, "signing-key.pem")));
builder.Services.AddSingleton<LicenceService>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Caddy on the same server passes the visitor's address
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Add(IPAddress.Loopback);
    o.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("activate", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("refresh", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("admin", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
app.Use(async (c, next) =>
{
    c.Response.Headers["X-Content-Type-Options"] = "nosniff";
    c.Response.Headers["Referrer-Policy"] = "no-referrer";
    c.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

// The admin key: made once at install, kept in a root-only file; only its hash is held in memory
if (!File.Exists(adminKeyFile))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(adminKeyFile))!);
    File.WriteAllText(adminKeyFile, "zono-admin-" + TokenSigner.B64(RandomNumberGenerator.GetBytes(24)) + "\n");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(adminKeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
var adminHash = SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(adminKeyFile).Trim()));
bool IsAdmin(HttpContext c)
{
    var header = c.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
    return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim())), adminHash);
}
string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "";
IResult Send(ApiResult r) => r.Status == 200
    ? Results.Json(new { ok = true, token = r.Token, message = r.Message })
    : Results.Json(new { ok = false, error = r.Error, message = r.Message }, statusCode: r.Status);

// ---------- For the apps ----------

app.MapGet("/", () => Results.Text("Zono licence server. https://www.zonosoft.co.uk", "text/plain"));
app.MapGet("/api/v1/health", () => Results.Json(new { ok = true, time = DateTime.UtcNow }));
app.MapGet("/api/v1/public-key", (TokenSigner s) => Results.Text(s.PublicKeyPem, "text/plain"));
app.MapPost("/api/v1/activate", (ActivateRequest r, LicenceService svc, HttpContext c) => Send(svc.Activate(r, Ip(c)))).RequireRateLimiting("activate");
app.MapPost("/api/v1/refresh", (RefreshRequest r, LicenceService svc, HttpContext c) => Send(svc.Refresh(r, Ip(c)))).RequireRateLimiting("refresh");
app.MapPost("/api/v1/deactivate", (RefreshRequest r, LicenceService svc, HttpContext c) => Send(svc.Deactivate(r, Ip(c)))).RequireRateLimiting("refresh");

// ---------- Admin (ZonoSoft only) ----------

app.MapGet("/admin", () =>
{
    using var stream = typeof(LicenceStore).Assembly.GetManifestResourceStream("admin.html")!;
    using var reader = new StreamReader(stream);
    return Results.Content(reader.ReadToEnd(), "text/html; charset=utf-8");
});

var admin = app.MapGroup("/api/admin").RequireRateLimiting("admin");
admin.AddEndpointFilter(async (ctx, next) =>
{
    if (IsAdmin(ctx.HttpContext)) return await next(ctx);
    ctx.HttpContext.RequestServices.GetRequiredService<LicenceStore>().Log(null, "admin-denied", ctx.HttpContext.Request.Path, Ip(ctx.HttpContext));
    await Task.Delay(500);
    return Results.Json(new { ok = false, message = "Admin key needed." }, statusCode: 401);
});

object Describe(LicenceStore s, Licence l) => new
{
    l.Id, l.KeyHint, l.BusinessName, l.Email, l.Plan, l.TeamSeats,
    Expires = l.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.MaxActivations, l.Status, l.Notes, l.CreatedUtc,
    Activations = s.Activations(l.Id).Select(a => new { a.Id, a.InstallId, a.BusinessName, a.DeviceName, a.Platform, a.AppVersion, a.FirstSeenUtc, a.LastSeenUtc, a.Released })
};

admin.MapGet("/licences", (LicenceStore s) => Results.Json(s.All().Select(l => Describe(s, l))));
admin.MapGet("/events", (LicenceStore s) => Results.Json(s.Events().Select(e => new { e.At, e.LicenceId, e.What, e.Detail, e.Ip })));

admin.MapPost("/licences", (NewLicence n, LicenceStore s, HttpContext c) =>
{
    if (string.IsNullOrWhiteSpace(n.BusinessName)) return Results.Json(new { ok = false, message = "Enter the business name." }, statusCode: 400);
    DateOnly? expires = DateOnly.TryParse(n.Expires, CultureInfo.InvariantCulture, out var d) ? d : null;
    var (licence, key) = s.Create(n.BusinessName, n.Email ?? "", n.Plan ?? "monthly", Math.Clamp(n.TeamSeats ?? 5, 1, 10000), expires,
        Math.Clamp(n.MaxActivations ?? 1, 1, 50), n.Notes ?? "");
    s.Log(licence.Id, "created", $"{licence.BusinessName} · {licence.Plan} · {licence.TeamSeats} team members", Ip(c));
    return Results.Json(new { ok = true, key, licence = Describe(s, licence) });
});

admin.MapPost("/licences/{id}", (string id, LicenceChange ch, LicenceStore s, HttpContext c) =>
{
    if (s.Get(id) is null) return Results.NotFound();
    DateOnly? expires = DateOnly.TryParse(ch.Expires, CultureInfo.InvariantCulture, out var d) ? d : null;
    var status = ch.Status is "active" or "revoked" ? ch.Status : null;
    s.Update(id, ch.BusinessName, ch.Email, ch.Plan, ch.TeamSeats is { } t ? Math.Clamp(t, 1, 10000) : null, expires, ch.Expires == "",
        ch.MaxActivations is { } m ? Math.Clamp(m, 1, 50) : null, status, ch.Notes);
    s.Log(id, "changed", System.Text.Json.JsonSerializer.Serialize(ch), Ip(c));
    return Results.Json(new { ok = true, licence = Describe(s, s.Get(id)!) });
});

admin.MapPost("/licences/{id}/new-key", (string id, LicenceStore s, HttpContext c) =>
{
    if (s.Get(id) is null) return Results.NotFound();
    var key = s.NewKeyFor(id);
    s.Log(id, "new key", "", Ip(c));
    return Results.Json(new { ok = true, key });
});

admin.MapPost("/activations/{id:long}/release", (long id, LicenceStore s, HttpContext c) =>
{
    s.ReleaseById(id);
    s.Log(null, "released", "activation " + id, Ip(c));
    return Results.Json(new { ok = true });
});

admin.MapGet("/backup", (LicenceStore s) =>
{
    var path = Path.Combine(Path.GetTempPath(), $"zono-licences-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
    s.BackupTo(path);
    var bytes = File.ReadAllBytes(path);
    File.Delete(path);
    return Results.File(bytes, "application/octet-stream", Path.GetFileName(path));
});

app.Run();

public sealed record NewLicence(string BusinessName, string? Email, string? Plan, int? TeamSeats, string? Expires, int? MaxActivations, string? Notes);
public sealed record LicenceChange(string? BusinessName, string? Email, string? Plan, int? TeamSeats, string? Expires, int? MaxActivations, string? Status, string? Notes);

public partial class Program;
