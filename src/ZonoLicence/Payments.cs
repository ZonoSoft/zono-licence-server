using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ZonoLicence;

/// <summary>What the admin page shows about card payments.</summary>
public sealed record PaymentStatus(bool Connected, string? Account, bool LiveMode, string? WebhookUrl, string? Problem);

/// <summary>
/// Card and PayPal payments with Stripe. Customers pay on Stripe's own checkout page (card details never reach this
/// server), and Stripe tells this server about payments through a signed webhook. A paid licence becomes active with
/// its paid-until date set to the end of the period paid for; renewals extend it by themselves.
///
/// Stripe's secret key is pasted on the admin page by ZonoSoft (never stored in the code), and kept encrypted.
/// Saving it also sets up the webhook on the Stripe account and keeps its signing secret, encrypted too.
/// </summary>
public sealed class Payments
{
    private readonly string _configPath;
    private readonly string _connection;
    private readonly Secrets _secrets;
    private readonly LicenceStore _licences;
    private readonly CustomerStore _customers;
    private readonly HttpClient _http;
    private readonly string _apiBase = (Environment.GetEnvironmentVariable("ZONO_STRIPE_API") ?? "https://api.stripe.com").TrimEnd('/');
    public static string PublicUrl => (Environment.GetEnvironmentVariable("ZONO_PUBLIC_URL") ?? "https://licence.zonosoft.co.uk").TrimEnd('/');
    public static string WebsiteUrl => (Environment.GetEnvironmentVariable("ZONO_WEBSITE_URL") ?? "https://zonosoft.co.uk").TrimEnd('/');
    public static string WebhookUrl => PublicUrl + "/api/stripe/webhook";

    private static readonly string[] WebhookEvents =
        ["checkout.session.completed", "invoice.paid", "customer.subscription.updated", "customer.subscription.deleted"];

    private sealed record Config(string SecretKey, string? WebhookId, string? WebhookSecret, string Account, bool LiveMode);

    public Payments(string dataDir, string databasePath, Secrets secrets, LicenceStore licences, CustomerStore customers, HttpClient? http = null)
    {
        _configPath = Path.Combine(dataDir, "stripe.json");
        _connection = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        _secrets = secrets;
        _licences = licences;
        _customers = customers;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Which Stripe customer is which account, and which subscription pays for which licence
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS stripe_links (
                kind TEXT NOT NULL,
                local_id TEXT NOT NULL,
                stripe_id TEXT NOT NULL,
                PRIMARY KEY (kind, local_id));
            CREATE INDEX IF NOT EXISTS ix_stripe_links_stripe ON stripe_links(stripe_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        return c;
    }

    private string? Link(string kind, string localId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT stripe_id FROM stripe_links WHERE kind = $k AND local_id = $l;";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$l", localId);
        return cmd.ExecuteScalar() as string;
    }

    private string? LocalFor(string kind, string stripeId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT local_id FROM stripe_links WHERE kind = $k AND stripe_id = $s;";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$s", stripeId);
        return cmd.ExecuteScalar() as string;
    }

    private void SetLink(string kind, string localId, string stripeId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO stripe_links(kind, local_id, stripe_id) VALUES ($k,$l,$s) ON CONFLICT(kind, local_id) DO UPDATE SET stripe_id = excluded.stripe_id;";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$l", localId);
        cmd.Parameters.AddWithValue("$s", stripeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The licence's Stripe subscription, if it's paid by one (customers can manage it on Stripe).</summary>
    public bool HasSubscription(string licenceId) => Link("subscription", licenceId) is not null;
    public bool HasCustomer(string accountId) => Link("customer", accountId) is not null;

    // ---------- Settings (admin page) ----------

    private Config? Load()
    {
        if (!File.Exists(_configPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var key = _secrets.Unprotect(S("secretKey"));
            if (string.IsNullOrEmpty(key)) return null;
            var hook = S("webhookSecret") is { Length: > 0 } h ? _secrets.Unprotect(h) : null;
            return new Config(key, S("webhookId"), hook, S("account") ?? "", r.TryGetProperty("liveMode", out var l) && l.GetBoolean());
        }
        catch (Exception e) when (e is JsonException or CryptographicException or FormatException or IOException)
        {
            return null;
        }
    }

    private void Save(Config c)
    {
        var json = JsonSerializer.Serialize(new
        {
            secretKey = _secrets.Protect(c.SecretKey),
            webhookId = c.WebhookId,
            webhookSecret = c.WebhookSecret is null ? null : _secrets.Protect(c.WebhookSecret),
            account = c.Account,
            liveMode = c.LiveMode
        });
        var temp = _configPath + ".new";
        File.WriteAllText(temp, json);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _configPath, overwrite: true);
    }

    public bool Enabled => Load() is { WebhookSecret: not null };

    public PaymentStatus Status()
    {
        var c = Load();
        return c is null
            ? new PaymentStatus(false, null, false, null, null)
            : new PaymentStatus(c.WebhookSecret is not null, c.Account, c.LiveMode, WebhookUrl, c.WebhookSecret is null ? "The webhook isn't set up." : null);
    }

    /// <summary>
    /// Connects a Stripe account: checks the key works, then (re)creates this server's webhook on it.
    /// Returns a problem to show, or null.
    /// </summary>
    public async Task<string?> ConnectAsync(string? secretKey, CancellationToken cancel)
    {
        var key = (secretKey ?? "").Trim();
        if (!(key.StartsWith("sk_", StringComparison.Ordinal) || key.StartsWith("rk_", StringComparison.Ordinal)) || key.Length < 20 || key.Any(char.IsWhiteSpace))
            return "That doesn't look like a Stripe secret key. It starts with sk_test_ or sk_live_ (or rk_ for a restricted key).";
        var account = await CallAsync(key, HttpMethod.Get, "/v1/account", null, cancel);
        if (account.Error is { } e) return "Stripe didn't accept the key: " + e;
        var root = account.Json!.RootElement;
        var name = Str(root, "settings", "dashboard", "display_name") ?? Str(root, "business_profile", "name") ?? Str(root, "email") ?? Str(root, "id") ?? "Stripe account";
        var live = key.Contains("_live_", StringComparison.Ordinal);

        // Replace the webhook this server made before (if any)
        if (Load() is { WebhookId: { Length: > 0 } oldHook, SecretKey: var oldKey })
            await CallAsync(oldKey, HttpMethod.Delete, "/v1/webhook_endpoints/" + Uri.EscapeDataString(oldHook), null, cancel);
        var form = new List<KeyValuePair<string, string>>
        {
            new("url", WebhookUrl),
            new("description", "Zono licence server: marks licences paid")
        };
        form.AddRange(WebhookEvents.Select(ev => new KeyValuePair<string, string>("enabled_events[]", ev)));
        var hook = await CallAsync(key, HttpMethod.Post, "/v1/webhook_endpoints", form, cancel);
        if (hook.Error is { } he)
        {
            Save(new Config(key, null, null, name, live));
            return "The key works, but the webhook couldn't be set up: " + he;
        }
        Save(new Config(key, Str(hook.Json!.RootElement, "id"), Str(hook.Json.RootElement, "secret"), name, live));
        return null;
    }

    public async Task DisconnectAsync(CancellationToken cancel)
    {
        if (Load() is { WebhookId: { Length: > 0 } hook } c)
            await CallAsync(c.SecretKey, HttpMethod.Delete, "/v1/webhook_endpoints/" + Uri.EscapeDataString(hook), null, cancel);
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    // ---------- Paying ----------

    /// <summary>Stripe's checkout page for a licence (card, PayPal and wallets, as turned on in Stripe). Returns its address.</summary>
    public async Task<(string? Url, string? Problem)> CheckoutAsync(Licence licence, Account account, CancellationToken cancel)
    {
        if (Load() is not { WebhookSecret: not null } c) return (null, "Online payment isn't available yet. Email ZonoSoft@outlook.com and we'll tell you how to pay.");
        var price = CustomerService.Price(licence.Plan, licence.TeamSeats);
        if (price <= 0) return (null, "This licence doesn't have a price. Email ZonoSoft@outlook.com.");

        var customer = Link("customer", account.Id);
        if (customer is null)
        {
            var made = await CallAsync(c.SecretKey, HttpMethod.Post, "/v1/customers",
            [
                new("name", account.BusinessName), new("email", account.Email),
                new("metadata[account_id]", account.Id), new("metadata[username]", account.Username)
            ], cancel);
            if (made.Error is { } me) return (null, "Couldn't start the payment: " + me);
            customer = Str(made.Json!.RootElement, "id")!;
            SetLink("customer", account.Id, customer);
        }

        var yearly = licence.Plan == "yearly";
        var pence = ((long)Math.Round(price * 100m)).ToString(CultureInfo.InvariantCulture);
        var session = await CallAsync(c.SecretKey, HttpMethod.Post, "/v1/checkout/sessions",
        [
            new("mode", "subscription"),
            new("customer", customer),
            new("client_reference_id", licence.Id),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", "gbp"),
            new("line_items[0][price_data][unit_amount]", pence),
            new("line_items[0][price_data][recurring][interval]", yearly ? "year" : "month"),
            new("line_items[0][price_data][product_data][name]",
                $"Zono Workforce Suite licence ({(yearly ? "yearly" : "monthly")}, {licence.TeamSeats} team members)"),
            new("metadata[licence_id]", licence.Id),
            new("subscription_data[metadata][licence_id]", licence.Id),
            new("subscription_data[description]", $"{licence.BusinessName} · licence ending {licence.KeyHint}"),
            new("success_url", WebsiteUrl + "/portal?paid={CHECKOUT_SESSION_ID}"),
            new("cancel_url", WebsiteUrl + "/portal?cancelled=1")
        ], cancel);
        if (session.Error is { } se) return (null, "Couldn't start the payment: " + se);
        _licences.Log(licence.Id, "checkout", $"£{price:0.##} {(yearly ? "a year" : "a month")} · account {account.Id}", "");
        return (Str(session.Json!.RootElement, "url"), null);
    }

    /// <summary>Stripe's own page where customers change their card, see invoices or cancel. Returns its address.</summary>
    public async Task<(string? Url, string? Problem)> BillingPortalAsync(string accountId, CancellationToken cancel)
    {
        if (Load() is not { } c || Link("customer", accountId) is not { } customer) return (null, "There are no card payments on this account yet.");
        var portal = await CallAsync(c.SecretKey, HttpMethod.Post, "/v1/billing_portal/sessions",
            [new("customer", customer), new("return_url", WebsiteUrl + "/portal")], cancel);
        return portal.Error is { } e ? (null, "Couldn't open the payments page: " + e) : (Str(portal.Json!.RootElement, "url"), null);
    }

    // ---------- Webhook ----------

    /// <summary>
    /// Checks Stripe's signature (Stripe-Signature: t=…,v1=…, HMAC-SHA256 of "t.body" with the webhook secret), refusing
    /// anything older than 5 minutes.
    /// </summary>
    public static bool SignatureValid(string body, string? header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header)) return false;
        long? t = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)) t = ts;
            else if (kv[0].Trim() == "v1") signatures.Add(kv[1].Trim());
        }
        if (t is null || signatures.Count == 0 || Math.Abs(now.ToUnixTimeSeconds() - t.Value) > 300) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return signatures.Any(s =>
        {
            try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(s), expected); }
            catch (FormatException) { return false; }
        });
    }

    /// <summary>A payment event from Stripe. Returns the HTTP status for Stripe (it retries on errors).</summary>
    public async Task<int> HandleWebhookAsync(string body, string? signature, string ip, CancellationToken cancel)
    {
        if (Load() is not { WebhookSecret: { } secret } c) return 503;
        if (!SignatureValid(body, signature, secret, DateTimeOffset.UtcNow))
        {
            _licences.Log(null, "stripe-bad-signature", "", ip);
            return 400;
        }
        using var doc = JsonDocument.Parse(body);
        var type = Str(doc.RootElement, "type");
        var obj = doc.RootElement.GetProperty("data").GetProperty("object");
        switch (type)
        {
            case "checkout.session.completed":
            {
                var licenceId = Str(obj, "metadata", "licence_id") ?? Str(obj, "client_reference_id");
                var subscription = Str(obj, "subscription");
                if (licenceId is null || _licences.Get(licenceId) is null) return 200;
                if (subscription is not null) SetLink("subscription", licenceId, subscription);
                if (Str(obj, "customer") is { } cus && _licences.Get(licenceId)?.AccountId is { } acc) SetLink("customer", acc, cus);
                if (Str(obj, "payment_status") is "paid" or "no_payment_required")
                    return await PaidAsync(c, licenceId, subscription, "card payment", ip, cancel);
                return 200;
            }
            case "invoice.paid":
            {
                // Renewals (and the first payment): the subscription carries the licence id
                var subscription = Str(obj, "subscription") ?? Str(obj, "parent", "subscription_details", "subscription");
                if (subscription is null) return 200;
                var licenceId = LocalFor("subscription", subscription)
                    ?? Str(obj, "subscription_details", "metadata", "licence_id")
                    ?? Str(obj, "parent", "subscription_details", "metadata", "licence_id");
                if (licenceId is null || _licences.Get(licenceId) is null) return 200;
                SetLink("subscription", licenceId, subscription);
                return await PaidAsync(c, licenceId, subscription, "renewal paid", ip, cancel);
            }
            case "customer.subscription.updated":
            case "customer.subscription.deleted":
            {
                var subscription = Str(obj, "id");
                var licenceId = subscription is null ? null : LocalFor("subscription", subscription) ?? Str(obj, "metadata", "licence_id");
                if (licenceId is null || _licences.Get(licenceId) is not { } licence) return 200;
                var status = Str(obj, "status");
                var cancelling = type == "customer.subscription.deleted" || obj.TryGetProperty("cancel_at_period_end", out var cape) && cape.ValueKind == JsonValueKind.True;
                if (cancelling)
                    _licences.Log(licenceId, "subscription cancelled",
                        $"{status} · licence runs to {licence.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}", ip);
                return 200;
            }
            default:
                return 200;
        }
    }

    /// <summary>Marks a licence paid up to the end of the subscription's current period (plus a day's leeway).</summary>
    private async Task<int> PaidAsync(Config c, string licenceId, string? subscription, string what, string ip, CancellationToken cancel)
    {
        DateOnly? until = null;
        if (subscription is not null)
        {
            var sub = await CallAsync(c.SecretKey, HttpMethod.Get, "/v1/subscriptions/" + Uri.EscapeDataString(subscription), null, cancel);
            if (sub.Error is not null) return 502;   // Stripe will send it again
            var r = sub.Json!.RootElement;
            // Newer Stripe API versions keep the period on the subscription's items
            var end = Long(r, "current_period_end");
            if (end is null && r.TryGetProperty("items", out var items) && items.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                end = Long(data[0], "current_period_end");
            if (end is { } e) until = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime).AddDays(1);
        }
        var licence = _licences.Get(licenceId)!;
        until ??= DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(licence.Plan == "yearly" ? 12 : 1).AddDays(1);
        if (licence.Expires is { } current && current > until) until = current;   // never shorten
        _licences.Update(licenceId, null, null, null, null, until, false, null, licence.Status == "revoked" ? null : "active", null);
        _licences.Log(licenceId, "paid", $"{what} · paid until {until.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", ip);
        return 200;
    }

    // ---------- Stripe API ----------

    private sealed record Reply(JsonDocument? Json, string? Error);

    private async Task<Reply> CallAsync(string key, HttpMethod method, string path, List<KeyValuePair<string, string>>? form, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(method, _apiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            using var response = await _http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            JsonDocument? json = null;
            try { json = JsonDocument.Parse(text); } catch (JsonException) { }
            if (response.IsSuccessStatusCode && json is not null) return new Reply(json, null);
            var message = json is not null ? Str(json.RootElement, "error", "message") : null;
            return new Reply(null, message ?? $"Stripe answered {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new Reply(null, "Stripe couldn't be reached.");
        }
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
