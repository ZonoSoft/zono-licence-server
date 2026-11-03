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
builder.Services.AddSingleton(_ => new Secrets(Path.Combine(dataDir, "secret.key")));
builder.Services.AddSingleton(sp => new CustomerStore(Path.Combine(dataDir, "licences.db"), sp.GetRequiredService<Secrets>()));
builder.Services.AddSingleton(sp => new Payments(dataDir, Path.Combine(dataDir, "licences.db"), sp.GetRequiredService<Secrets>(),
    sp.GetRequiredService<LicenceStore>(), sp.GetRequiredService<CustomerStore>()));
builder.Services.AddSingleton<CustomerService>();
builder.Services.AddSingleton(_ => new Downloads(Path.Combine(dataDir, "downloads")));
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
    o.AddPolicy("hello", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("register", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
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

app.MapGet("/", (HttpContext c) => c.Request.Host.Host.EndsWith("zonosoft.co.uk", StringComparison.OrdinalIgnoreCase) && !c.Request.Host.Host.StartsWith("licence.", StringComparison.OrdinalIgnoreCase)
    ? Results.Redirect("/portal")
    : Results.Text("Zono licence server. Customer portal: https://zonosoft.co.uk/portal", "text/plain"));
app.MapGet("/api/v1/health", () => Results.Json(new { ok = true, time = DateTime.UtcNow }));
app.MapGet("/api/v1/public-key", (TokenSigner s) => Results.Text(s.PublicKeyPem, "text/plain"));
app.MapPost("/api/v1/activate", (ActivateRequest r, LicenceService svc, HttpContext c) => Send(svc.Activate(r, Ip(c)))).RequireRateLimiting("activate");
app.MapPost("/api/v1/refresh", (RefreshRequest r, LicenceService svc, HttpContext c) => Send(svc.Refresh(r, Ip(c)))).RequireRateLimiting("refresh");
app.MapPost("/api/v1/hello", (HelloRequest r, CustomerService svc) => { var (status, body) = svc.Hello(r); return Results.Json(body, statusCode: status); })
    .RequireRateLimiting("hello");
app.MapPost("/api/v1/register", (RegisterRequest r, CustomerService svc, HttpContext c) => { var (status, body) = svc.Register(r, Ip(c)); return Results.Json(body, statusCode: status); })
    .RequireRateLimiting("register");
app.MapPost("/api/v1/deactivate", (RefreshRequest r, LicenceService svc, HttpContext c) => Send(svc.Deactivate(r, Ip(c)))).RequireRateLimiting("refresh");

// ---------- Downloads (the website's installers) ----------

app.MapGet("/api/v1/downloads", (Downloads d) => Results.Json(d.Latest().Select(f => new
{
    f.Name, f.Product, f.Platform, f.Version, f.Bytes, f.Sha256, f.UploadedUtc, Url = "/downloads/" + f.Name
})));
// Only people signed in to a ZonoSoft account can download
app.MapGet("/downloads/{name}", (string name, Downloads d, CustomerStore store, LicenceStore log, HttpContext c) =>
{
    if (!Downloads.ValidName(name) || !File.Exists(d.PathOf(name))) return Results.NotFound();
    if (store.SessionAccount(SessionToken(c)) is not { } accountId)
        return Results.Redirect("/portal?next=" + Uri.EscapeDataString("/workforce/download"));
    c.Response.Headers.CacheControl = "private, no-store";
    if (c.Request.Headers.Range.Count == 0 || c.Request.Headers.Range.ToString().StartsWith("bytes=0-", StringComparison.Ordinal))
        log.Log(null, "download", $"{name} · account {accountId}", Ip(c));
    return Results.File(d.PathOf(name), Downloads.ContentType(name), name, enableRangeProcessing: true);
}).RequireRateLimiting("refresh");

// ---------- App updates ----------
// An app that has said hello (a known machine) asks whether there's a newer version. The answer is signed with the
// licence signing key, so the app knows it came from ZonoSoft; the signed ticket also lets it download that one file
// for an hour (the website's downloads need an account; the apps use this instead). The app checks the file's
// SHA-256 against the signed one before installing.
app.MapPost("/api/v1/update", (UpdateRequest r, Downloads d, CustomerStore store, TokenSigner signer, LicenceStore log, HttpContext c) =>
{
    if (r.MachineId is not { Length: >= 16 and <= 128 } mid || store.GetMachine(mid) is null)
        return Results.Json(new { ok = false, message = "Unknown machine." }, statusCode: 403);
    var product = r.Product?.ToLowerInvariant() == "team" ? "team" : "workforce";
    var platform = (r.Platform ?? "").ToLowerInvariant();
    var ext = platform switch { "windows" => ".exe", "android" => ".apk", _ => null };
    if (ext is null || !Version.TryParse(r.AppVersion, out var current)) return Results.Json(new { ok = true, update = false });
    var latest = d.All()
        .Where(f => f.Product == product && f.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)
                    && f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && f.Sha256.Length == 64)
        .OrderByDescending(f => Version.Parse(f.Version)).FirstOrDefault();
    if (latest is null || Version.Parse(latest.Version) <= current) return Results.Json(new { ok = true, update = false });
    var ticket = signer.Sign(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["typ"] = "update", ["name"] = latest.Name, ["ver"] = latest.Version, ["sha"] = latest.Sha256, ["bytes"] = latest.Bytes,
        ["mid"] = mid, ["exp"] = DateTime.UtcNow.AddHours(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
    }));
    return Results.Json(new { ok = true, update = true, version = latest.Version, name = latest.Name, bytes = latest.Bytes, sha256 = latest.Sha256, ticket });
}).RequireRateLimiting("hello");

app.MapGet("/api/v1/update/file", (string t, Downloads d, TokenSigner signer, LicenceStore log, HttpContext c) =>
{
    if (signer.Verify(t) is not { } json) return Results.StatusCode(403);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var p = doc.RootElement;
    string S(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
    if (S("typ") != "update" || !DateTime.TryParse(S("exp"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var exp) || exp < DateTime.UtcNow)
        return Results.StatusCode(403);
    var name = S("name");
    if (!Downloads.ValidName(name) || !File.Exists(d.PathOf(name))) return Results.NotFound();
    if (c.Request.Headers.Range.Count == 0 || c.Request.Headers.Range.ToString().StartsWith("bytes=0-", StringComparison.Ordinal))
        log.Log(null, "update", $"{name} · machine {S("mid")[..Math.Min(12, S("mid").Length)]}", Ip(c));
    c.Response.Headers.CacheControl = "private, no-store";
    return Results.File(d.PathOf(name), Downloads.ContentType(name), name, enableRangeProcessing: true);
}).RequireRateLimiting("refresh");

// The server's import key: builds are encrypted to it before they're published (see deploy/import-downloads.sh)
app.MapGet("/api/v1/import-key", () =>
{
    var path = Path.Combine(dataDir, "import-public.pem");
    return File.Exists(path) ? Results.Text(File.ReadAllText(path), "text/plain") : Results.NotFound();
});

// Is someone signed in (for the website's download page)
app.MapGet("/api/portal/session", (CustomerStore store, HttpContext c) =>
    Results.Json(new { signedIn = store.SessionAccount(SessionToken(c)) is not null }));

// ---------- Customer portal ----------

string Page(string name)
{
    using var stream = typeof(LicenceStore).Assembly.GetManifestResourceStream(name)!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
string CookieName(HttpContext c) => c.Request.IsHttps ? "__Host-zono-portal" : "zono-portal";
string? SessionToken(HttpContext c) => c.Request.Cookies[CookieName(c)];

app.MapGet("/portal", (HttpContext c) =>
{
    c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
    return Results.Content(Page("portal.html"), "text/html; charset=utf-8");
});

var portal = app.MapGroup("/api/portal");
portal.MapPost("/login", (LoginRequest r, CustomerService svc, CustomerStore store, HttpContext c) =>
{
    var (accountId, problem) = svc.Login(r, Ip(c));
    if (accountId is null) return Results.Json(new { ok = false, message = problem }, statusCode: 401);
    c.Response.Cookies.Append(CookieName(c), store.StartSession(accountId), new CookieOptions
    {
        HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12)
    });
    return Results.Json(new { ok = true });
}).RequireRateLimiting("login");

portal.MapPost("/register", (WebRegisterRequest r, CustomerService svc, CustomerStore store, HttpContext c) =>
{
    var (status, body, accountId) = svc.WebRegister(r, Ip(c));
    if (accountId is not null)
        c.Response.Cookies.Append(CookieName(c), store.StartSession(accountId), new CookieOptions
        {
            HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12)
        });
    return Results.Json(body, statusCode: status);
}).RequireRateLimiting("register");

portal.MapPost("/logout", (CustomerStore store, HttpContext c) =>
{
    store.EndSession(SessionToken(c));
    c.Response.Cookies.Delete(CookieName(c), new CookieOptions { Path = "/", Secure = c.Request.IsHttps });
    return Results.Json(new { ok = true });
});

// Everything below needs a signed-in customer
var mine = portal.MapGroup("").RequireRateLimiting("admin");
mine.AddEndpointFilter(async (ctx, next) =>
{
    var c = ctx.HttpContext;
    if (c.Request.Path.Value is "/api/portal/login" or "/api/portal/logout") return await next(ctx);
    var id = c.RequestServices.GetRequiredService<CustomerStore>().SessionAccount(SessionToken(c));
    if (id is null) return Results.Json(new { ok = false, message = "Please sign in." }, statusCode: 401);
    c.Items["account"] = id;
    return await next(ctx);
});
mine.MapGet("/me", (CustomerService svc, HttpContext c) => Results.Json(svc.Me((string)c.Items["account"]!)));
mine.MapPost("/details", (DetailsRequest r, CustomerService svc, HttpContext c) =>
    svc.UpdateDetails((string)c.Items["account"]!, r) is { } problem
        ? Results.Json(new { ok = false, message = problem }, statusCode: 400)
        : Results.Json(new { ok = true, message = "Details saved." }));
mine.MapPost("/password", (PasswordRequest r, CustomerService svc, CustomerStore store, HttpContext c) =>
{
    var id = (string)c.Items["account"]!;
    if (svc.ChangePassword(id, r) is { } problem) return Results.Json(new { ok = false, message = problem }, statusCode: 400);
    c.Response.Cookies.Append(CookieName(c), store.StartSession(id), new CookieOptions
    {
        HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12)
    });
    return Results.Json(new { ok = true, message = "Password changed." });
});

// Paying online (Stripe's checkout page: card, PayPal, Apple Pay / Google Pay as turned on in Stripe)
mine.MapPost("/checkout", async (BuyRequest r, CustomerService svc, CustomerStore store, Payments pay, HttpContext c) =>
{
    var id = (string)c.Items["account"]!;
    var (licence, problem) = svc.LicenceToPay(id, r);
    if (licence is null) return Results.Json(new { ok = false, message = problem }, statusCode: 400);
    var (url, payProblem) = await pay.CheckoutAsync(licence, store.GetAccount(id)!, c.RequestAborted);
    return url is null ? Results.Json(new { ok = false, message = payProblem }, statusCode: 400) : Results.Json(new { ok = true, url });
});
mine.MapPost("/billing", async (Payments pay, HttpContext c) =>
{
    var (url, problem) = await pay.BillingPortalAsync((string)c.Items["account"]!, c.RequestAborted);
    return url is null ? Results.Json(new { ok = false, message = problem }, statusCode: 400) : Results.Json(new { ok = true, url });
});

// Stripe tells us about payments (signed; see Payments.SignatureValid)
app.MapPost("/api/stripe/webhook", async (HttpContext c, Payments pay) =>
{
    using var reader = new StreamReader(c.Request.Body);
    var body = await reader.ReadToEndAsync(c.RequestAborted);
    if (body.Length > 512 * 1024) return Results.StatusCode(413);
    return Results.StatusCode(await pay.HandleWebhookAsync(body, c.Request.Headers["Stripe-Signature"].ToString(), Ip(c), c.RequestAborted));
});

// ---------- Admin (ZonoSoft only) ----------

app.MapGet("/admin", () => Results.Content(Page("admin.html"), "text/html; charset=utf-8"));

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
    Account = l.AccountId is { } aid && app.Services.GetRequiredService<CustomerStore>().GetAccount(aid) is { } a
        ? new { a.Id, a.Username, a.ContactName, a.Email, Phone = a.PhoneCountry + " " + a.Phone, a.BusinessName } : null,
    l.Id, l.KeyHint, l.BusinessName, l.Email, l.Plan, l.TeamSeats,
    Expires = l.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.MaxActivations, l.Status, l.Notes, l.CreatedUtc,
    Activations = s.Activations(l.Id).Select(a => new { a.Id, a.InstallId, a.BusinessName, a.DeviceName, a.Platform, a.AppVersion, a.FirstSeenUtc, a.LastSeenUtc, a.Released })
};

admin.MapGet("/licences", (LicenceStore s) => Results.Json(s.All().Select(l => Describe(s, l))));
admin.MapGet("/machines", (CustomerStore s) => Results.Json(s.AllMachines().Select(m => new
{
    m.Id, m.Product, m.Platform, m.AppVersion, m.BusinessName, m.FirstSeenUtc, m.LastSeenUtc, m.Launches,
    TrialStarted = m.TrialStarted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Registered = m.AccountId is not null
})));
admin.MapGet("/events", (LicenceStore s) => Results.Json(s.Events().Select(e => new { e.At, e.LicenceId, e.What, e.Detail, e.Ip })));

admin.MapPost("/licences", (NewLicence n, LicenceStore s, HttpContext c) =>
{
    if (string.IsNullOrWhiteSpace(n.BusinessName)) return Results.Json(new { ok = false, message = "Enter the business name." }, statusCode: 400);
    DateOnly? expires = DateOnly.TryParse(n.Expires, CultureInfo.InvariantCulture, out var d) ? d : null;
    var (licence, key) = s.Create(n.BusinessName, n.Email ?? "", n.Plan ?? "monthly", Math.Clamp(n.TeamSeats ?? 5, 1, 10000), expires,
        Math.Clamp(n.MaxActivations ?? 1, 1, 50), n.Notes ?? "", secrets: c.RequestServices.GetRequiredService<Secrets>());
    s.Log(licence.Id, "created", $"{licence.BusinessName} · {licence.Plan} · {licence.TeamSeats} team members", Ip(c));
    return Results.Json(new { ok = true, key, licence = Describe(s, licence) });
});

admin.MapPost("/licences/{id}", (string id, LicenceChange ch, LicenceStore s, HttpContext c) =>
{
    if (s.Get(id) is null) return Results.NotFound();
    DateOnly? expires = DateOnly.TryParse(ch.Expires, CultureInfo.InvariantCulture, out var d) ? d : null;
    var status = ch.Status is "active" or "revoked" or "pending" ? ch.Status : null;
    s.Update(id, ch.BusinessName, ch.Email, ch.Plan, ch.TeamSeats is { } t ? Math.Clamp(t, 1, 10000) : null, expires, ch.Expires == "",
        ch.MaxActivations is { } m ? Math.Clamp(m, 1, 50) : null, status, ch.Notes);
    s.Log(id, "changed", System.Text.Json.JsonSerializer.Serialize(ch), Ip(c));
    return Results.Json(new { ok = true, licence = Describe(s, s.Get(id)!) });
});

admin.MapPost("/licences/{id}/new-key", (string id, LicenceStore s, HttpContext c) =>
{
    if (s.Get(id) is null) return Results.NotFound();
    var key = s.NewKeyFor(id, c.RequestServices.GetRequiredService<Secrets>());
    s.Log(id, "new key", "", Ip(c));
    return Results.Json(new { ok = true, key });
});

admin.MapPost("/activations/{id:long}/release", (long id, LicenceStore s, HttpContext c) =>
{
    s.ReleaseById(id);
    s.Log(null, "released", "activation " + id, Ip(c));
    return Results.Json(new { ok = true });
});

admin.MapGet("/payments", (Payments pay) => Results.Json(pay.Status()));
admin.MapPost("/payments", async (StripeKeyRequest r, Payments pay, LicenceStore s, HttpContext c) =>
{
    var problem = await pay.ConnectAsync(r.SecretKey, c.RequestAborted);
    s.Log(null, problem is null ? "stripe connected" : "stripe connect failed", problem ?? "", Ip(c));
    return problem is null ? Results.Json(new { ok = true, status = pay.Status() }) : Results.Json(new { ok = false, message = problem, status = pay.Status() }, statusCode: 400);
});
admin.MapPost("/payments/disconnect", async (Payments pay, LicenceStore s, HttpContext c) =>
{
    await pay.DisconnectAsync(c.RequestAborted);
    s.Log(null, "stripe disconnected", "", Ip(c));
    return Results.Json(new { ok = true });
});

admin.MapGet("/downloads", (Downloads d) => Results.Json(d.All()));
admin.MapPost("/downloads", async (HttpContext c, Downloads d, LicenceStore s) =>
{
    var name = c.Request.Query["name"].ToString();
    if (!Downloads.ValidName(name))
        return Results.Json(new { ok = false, message = "Use the build's own file name, e.g. ZonoWorkforceSuite-v0.32.0-Windows.zip or ZonoTeam-v0.32.0-Android.apk." }, statusCode: 400);
    try
    {
        var saved = await d.SaveAsync(name, c.Request.Body, c.RequestAborted);
        s.Log(null, "download uploaded", $"{name} ({saved.Bytes / 1048576.0:0.0} MB)", Ip(c));
        return Results.Json(new { ok = true, file = saved });
    }
    catch (InvalidDataException e)
    {
        return Results.Json(new { ok = false, message = e.Message }, statusCode: 400);
    }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Downloads.MaxBytes));
admin.MapPost("/downloads/{name}/delete", (string name, Downloads d, LicenceStore s, HttpContext c) =>
{
    if (!d.Delete(name)) return Results.NotFound();
    s.Log(null, "download deleted", name, Ip(c));
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

public sealed record UpdateRequest(string? MachineId, string? Product, string? Platform, string? AppVersion);
