using System.Globalization;
using System.Net.Mail;

namespace ZonoLicence;

public sealed record HelloRequest(string? MachineId, string? Product, string? Platform, string? AppVersion, string? InstallId, string? BusinessName, string? DeviceName);

public sealed record RegisterRequest(
    string? MachineId, string? InstallId, string? Product, string? Platform, string? AppVersion, string? DeviceName,
    string? BusinessName, string? ContactName, string? Email, string? PhoneCountry, string? Phone, string? Username, string? Password,
    string? Plan, int? TeamSeats);

public sealed record LoginRequest(string? Username, string? Password);
public sealed record WebRegisterRequest(string? BusinessName, string? ContactName, string? Email, string? PhoneCountry, string? Phone,
    string? Username, string? Password, bool? AcceptTerms);
public sealed record DetailsRequest(string? BusinessName, string? ContactName, string? Email, string? PhoneCountry, string? Phone);
public sealed record PasswordRequest(string? Current, string? New);
public sealed record BuyRequest(string? LicenceId, string? Plan, int? TeamSeats, bool? ForBusiness);

/// <summary>The terms customers agree to (the date on zonosoft.co.uk/terms).</summary>
public static class Terms
{
    public const string Version = "4 November 2026";
}
public sealed record StripeKeyRequest(string? SecretKey);

/// <summary>
/// Registering for a licence from the app, picking up a licence once it's paid, and the customer portal. There's no free
/// trial (and nothing is kept about devices that haven't got a licence): people ask for a demo key instead.
/// </summary>
public sealed class CustomerService(CustomerStore customers, LicenceStore licences, LicenceService licensing, Secrets secrets, Payments payments)
{
    public const int IncludedTeamMembers = 5;

    public static decimal Price(string plan, int seats) => plan switch
    {
        "yearly" => 120m + 20m * Math.Max(0, seats - IncludedTeamMembers),
        "monthly" => 12m + 2m * Math.Max(0, seats - IncludedTeamMembers),
        _ => 0m
    };

    private static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    /// <summary>
    /// A business that registered from the app asks whether its licence has been paid for, and gets it once it has.
    /// Nothing is recorded about the device (older apps also send a device code; it's ignored).
    /// </summary>
    public (int Status, object Body) Hello(HelloRequest r)
    {
        var installId = Clip(r.InstallId, 64);
        // Only the business the licence was registered (or activated) for; a deactivated business stays deactivated
        var licence = installId.Length >= 8 ? licences.ForInstall(installId) : null;
        if (licence is null) return (200, new { ok = true });
        var business = Clip(r.BusinessName, 80);
        string? token = null;
        if (licence.Status != "pending" &&
            licences.Activate(licence, installId, business, LicenceStore.DeviceLabel(r.Platform), Clip(r.Platform, 20), Clip(r.AppVersion, 20)))
            token = licensing.Token(licence, installId, business);
        var account = licence.AccountId is { } id ? customers.GetAccount(id) : null;
        return (200, new { ok = true, registered = account is not null, username = account?.Username, licenceStatus = licence.Status, token });
    }

    private static string? CheckDetails(string business, string contact, string email, string dial, string phone)
    {
        // Only what's needed to run an account: the business, an email to reach it, and (optionally) a name.
        // Phone numbers are no longer asked for or kept (data minimisation); older apps may still send one.
        if (business.Length < 2) return "Enter the business name.";
        if (!MailAddress.TryCreate(email, out var mail) || !mail.Host.Contains('.', StringComparison.Ordinal)) return "Enter a valid email address.";
        return null;
    }

    private static string? CheckPassword(string username, string password)
    {
        if (password.Length < 10) return "The password needs at least 10 characters.";
        if (password.Length > 200) return "The password is too long.";
        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase)) return "The password can't be the same as the username.";
        return null;
    }

    /// <summary>From the app: makes the customer's portal account and their licence (waiting for payment) for this business.</summary>
    public (int Status, object Body) Register(RegisterRequest r)
    {
        var installId = Clip(r.InstallId, 64);
        if (installId.Length < 8) return (400, new { ok = false, error = "bad_request", message = "Missing business id." });
        var business = Clip(r.BusinessName, 80);
        var contact = Clip(r.ContactName, 80);
        var email = Clip(r.Email, 120);
        var dial = Clip(r.PhoneCountry, 10);
        var phone = Clip(r.Phone, 30);
        var username = Clip(r.Username, 120);
        var password = r.Password ?? "";
        var plan = r.Plan is "yearly" ? "yearly" : "monthly";
        var seats = Math.Clamp(r.TeamSeats ?? IncludedTeamMembers, IncludedTeamMembers, 10000);

        if (CheckDetails(business, contact, email, dial, phone) is { } problem) return (400, new { ok = false, error = "bad_details", message = problem });
        if (username.Length < 3 || username.Any(char.IsWhiteSpace)) return (400, new { ok = false, error = "bad_username", message = "Choose a username of at least 3 characters, without spaces (your email address works)." });
        if (CheckPassword(username, password) is { } weak) return (400, new { ok = false, error = "weak_password", message = weak });

        if (licences.ForInstall(installId) is { Status: not "revoked" } existing)
            return existing.AccountId is { } aid && customers.GetAccount(aid) is { } already
                ? (409, new { ok = false, error = "already_registered", message = $"This business is already registered as \"{already.Username}\". Sign in at zonosoft.co.uk/portal." })
                : (409, new { ok = false, error = "already_licensed", message = "This business already has a licence." });
        // An existing ZonoSoft account (e.g. made on the website): the right password adds this business to it
        Account account;
        if (customers.FindByUsername(username) is { } known)
        {
            if (known.LockedUntilUtc is { } until && until > DateTime.UtcNow)
                return (429, new { ok = false, error = "locked", message = "Too many wrong passwords for that account. Try again in 15 minutes." });
            var right = Secrets.CheckPassword(password, known.PasswordHash);
            customers.LoginResult(known.Id, right);
            if (!right)
                return (409, new { ok = false, error = "username_taken",
                    message = "That username already has a ZonoSoft account. If it's yours, enter its password to add this business to it; otherwise choose another username." });
            account = known;
        }
        else
            account = customers.CreateAccount(username, password, business, contact, email, "", "");
        var (licence, _) = licences.Create(business, "", plan, seats, null, 1, "Registered from the app", status: "pending", accountId: account.Id, secrets: secrets);
        licences.Activate(licence, installId, business, LicenceStore.DeviceLabel(r.Platform), Clip(r.Platform, 20), Clip(r.AppVersion, 20));
        licences.Log(licence.Id, "registered", $"{business} · {plan} · {seats} team members · account {account.Id}");
        var price = Price(plan, seats).ToString("0.##", CultureInfo.InvariantCulture);
        return (200, new
        {
            ok = true,
            message = $"Registered. Your licence ({plan}, {seats} team members, £{price}{(plan == "yearly" ? " a year" : " a month")}) " +
                      "starts as soon as you've paid — the app picks it up by itself. Pay by card or PayPal at zonosoft.co.uk/portal " +
                      "(sign in with your username and password); your licence key appears there once paid.",
            username
        });
    }

    /// <summary>From the website: a ZonoSoft account without a licence yet (the app's "Buy a licence" adds one to it).</summary>
    public (int Status, object Body, string? AccountId) WebRegister(WebRegisterRequest r)
    {
        var business = Clip(r.BusinessName, 80);
        var contact = Clip(r.ContactName, 80);
        var email = Clip(r.Email, 120);
        var dial = Clip(r.PhoneCountry, 10);
        var phone = Clip(r.Phone, 30);
        var username = Clip(r.Username, 120);
        var password = r.Password ?? "";
        if (r.AcceptTerms != true) return (400, new { ok = false, message = "Please agree to the terms and privacy notice." }, null);
        if (CheckDetails(business, contact, email, dial, phone) is { } problem) return (400, new { ok = false, message = problem }, null);
        if (username.Length < 3 || username.Any(char.IsWhiteSpace))
            return (400, new { ok = false, message = "Choose a username of at least 3 characters, without spaces (your email address works)." }, null);
        if (CheckPassword(username, password) is { } weak) return (400, new { ok = false, message = weak }, null);
        if (customers.UsernameTaken(username))
            return (409, new { ok = false, message = "That username already has an account. Sign in instead, or choose another username." }, null);
        var account = customers.CreateAccount(username, password, business, contact, email, "", "");
        licences.Log(null, "account created", account.Id + " (website)");
        return (200, new { ok = true }, account.Id);
    }

    // ---------- Portal ----------

    public (string? AccountId, string? Problem) Login(LoginRequest r)
    {
        var username = Clip(r.Username, 120);
        var password = r.Password ?? "";
        var account = username.Length == 0 ? null : customers.FindByUsername(username);
        if (account is null)
        {
            Secrets.SpendPasswordTime(password);
            return (null, "That username or password isn't right.");
        }
        if (account.LockedUntilUtc is { } until && until > DateTime.UtcNow)
            return (null, "Too many wrong passwords. Try again in 15 minutes.");
        var ok = Secrets.CheckPassword(password, account.PasswordHash);
        customers.LoginResult(account.Id, ok);
        licences.Log(null, ok ? "portal-login" : "portal-login-failed", account.Id);
        return ok ? (account.Id, null) : (null, "That username or password isn't right.");
    }

    public object Me(string accountId)
    {
        var a = customers.GetAccount(accountId)!;
        return new
        {
            ok = true,
            a.Username, a.BusinessName, a.ContactName, a.Email,
            created = a.CreatedUtc,
            payOnline = payments.Enabled,
            manageBilling = payments.Enabled && payments.HasCustomer(accountId),
            licences = licences.ForAccount(accountId).Select(l => new
            {
                l.Id,
                // The key is shown once the licence has been paid for
                key = l.Status == "pending" ? null : licences.KeyOf(l.Id, secrets),
                subscription = payments.HasSubscription(l.Id),
                l.KeyHint,
                l.Plan,
                l.TeamSeats,
                paidUntil = l.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                status = l.Status == "active" && l.Expires is { } e && DateOnly.FromDateTime(DateTime.UtcNow) > e ? "expired" : l.Status,
                price = Price(l.Plan, l.TeamSeats),
                usedOn = licences.Activations(l.Id, includeReleased: false).Select(x => new { x.BusinessName, x.DeviceName, x.Platform, x.AppVersion, x.LastSeenUtc })
            })
        };
    }

    /// <summary>
    /// A licence to pay for from the website: the account's unpaid one (with the chosen plan and team members), or a new
    /// one. The customer enters its key in Zono Workforce (Settings → Licence) once it's paid.
    /// </summary>
    public (Licence? Licence, string? Problem) LicenceToPay(string accountId, BuyRequest r)
    {
        var account = customers.GetAccount(accountId)!;
        var mine = licences.ForAccount(accountId);
        if (r.LicenceId is { Length: > 0 } id)
        {
            var chosen = mine.FirstOrDefault(l => l.Id == id);
            if (chosen is null) return (null, "That licence isn't on your account.");
            if (chosen.Status == "revoked") return (null, "That licence was cancelled. Email ZonoSoft@outlook.com.");
            if (payments.HasSubscription(chosen.Id) && chosen.Status == "active" && chosen.Expires is { } e && e >= DateOnly.FromDateTime(DateTime.UtcNow))
                return (null, "This licence is already paid by card. Use \"Manage payments\" to change the card or see invoices.");
            return (chosen, null);
        }
        var plan = r.Plan is "yearly" ? "yearly" : "monthly";
        var seats = Math.Clamp(r.TeamSeats ?? IncludedTeamMembers, IncludedTeamMembers, 10000);
        if (mine.FirstOrDefault(l => l.Status == "pending") is { } unpaid)
        {
            licences.Update(unpaid.Id, null, null, plan, seats, null, false, null, null, null);
            return (licences.Get(unpaid.Id), null);
        }
        var (licence, _) = licences.Create(account.BusinessName, account.Email, plan, seats, null, 1, "Bought on the website",
            status: "pending", accountId: accountId, secrets: secrets);
        licences.Log(licence.Id, "registered", $"{account.BusinessName} · {plan} · {seats} team members · account {accountId} (website)");
        return (licence, null);
    }

    public string? UpdateDetails(string accountId, DetailsRequest r)
    {
        var business = Clip(r.BusinessName, 80);
        var contact = Clip(r.ContactName, 80);
        var email = Clip(r.Email, 120);
        var dial = Clip(r.PhoneCountry, 10);
        var phone = Clip(r.Phone, 30);
        if (CheckDetails(business, contact, email, dial, phone) is { } problem) return problem;
        customers.UpdateDetails(accountId, business, contact, email, "", "");
        return null;
    }

    public string? ChangePassword(string accountId, PasswordRequest r)
    {
        var a = customers.GetAccount(accountId)!;
        if (!Secrets.CheckPassword(r.Current ?? "", a.PasswordHash)) return "The current password isn't right.";
        if (CheckPassword(a.Username, r.New ?? "") is { } weak) return weak;
        customers.SetPassword(accountId, r.New!);
        return null;
    }
}
