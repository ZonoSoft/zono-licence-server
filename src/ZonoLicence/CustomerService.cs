using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace ZonoLicence;

public sealed record HelloRequest(string? MachineId, string? Product, string? Platform, string? AppVersion, string? InstallId, string? BusinessName, string? DeviceName);

public sealed record RegisterRequest(
    string? MachineId, string? InstallId, string? Product, string? Platform, string? AppVersion, string? DeviceName,
    string? BusinessName, string? ContactName, string? Email, string? PhoneCountry, string? Phone, string? Username, string? Password,
    string? Plan, int? TeamSeats);

public sealed record LoginRequest(string? Username, string? Password);
public sealed record DetailsRequest(string? BusinessName, string? ContactName, string? Email, string? PhoneCountry, string? Phone);
public sealed record PasswordRequest(string? Current, string? New);

/// <summary>
/// The free trial (counted per machine from the first time it's seen, so reinstalling or resetting the app doesn't restart
/// it), registering for a licence from the app, and the customer portal.
/// </summary>
public sealed partial class CustomerService(CustomerStore customers, LicenceStore licences, LicenceService licensing, Secrets secrets)
{
    public const int TrialDays = 30;
    public const int IncludedTeamMembers = 5;

    public static decimal Price(string plan, int seats) => plan switch
    {
        "yearly" => 120m + 20m * Math.Max(0, seats - IncludedTeamMembers),
        "monthly" => 12m + 2m * Math.Max(0, seats - IncludedTeamMembers),
        _ => 0m
    };

    private static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    [GeneratedRegex("^[A-Za-z0-9_-]{16,80}$")]
    private static partial Regex MachineIdPattern();

    [GeneratedRegex(@"^\+[0-9]{1,4}(-[0-9]{1,4})?$")]
    private static partial Regex DialCodePattern();

    private static bool ValidMachine(string? id) => id is not null && MachineIdPattern().IsMatch(id);

    /// <summary>The trial start for a machine: the earliest of it and any other machine of the same business.</summary>
    private DateOnly TrialStart(Machine m) =>
        m.InstallId.Length == 0 ? m.TrialStarted
            : customers.MachinesForInstall(m.InstallId).Select(x => x.TrialStarted).Append(m.TrialStarted).Min();

    /// <summary>The licence for this business or machine's account, if there is one that has been paid for.</summary>
    private (Licence? Licence, string? Token) LicenceFor(Machine m, string businessName, string deviceName)
    {
        var licence = (m.InstallId.Length > 0 ? licences.ForInstall(m.InstallId) : null)
                      ?? (m.AccountId is { } a ? licences.ForAccount(a).FirstOrDefault(l => l.Status != "revoked") : null);
        if (licence is null || licence.Status == "pending" || m.InstallId.Length == 0) return (licence, null);
        if (!licences.Activate(licence, m.InstallId, businessName, deviceName, m.Platform, m.AppVersion)) return (licence, null);
        return (licence, licensing.Token(licence, m.InstallId, businessName));
    }

    /// <summary>Every launch: registers the machine and returns the trial start and, once paid for, the licence.</summary>
    public (int Status, object Body) Hello(HelloRequest r)
    {
        if (!ValidMachine(r.MachineId)) return (400, new { ok = false, error = "bad_machine", message = "Missing machine id." });
        var business = Clip(r.BusinessName, 80);
        var m = customers.Seen(r.MachineId!, Clip(r.Product, 20), Clip(r.Platform, 20), Clip(r.AppVersion, 20), Clip(r.InstallId, 64), business, DateTime.UtcNow);
        var start = TrialStart(m);
        var (licence, token) = LicenceFor(m, business, Clip(r.DeviceName, 80));
        var account = m.AccountId is { } id ? customers.GetAccount(id) : null;
        return (200, new
        {
            ok = true,
            trialStarted = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            trialDays = TrialDays,
            registered = account is not null,
            username = account?.Username,
            licenceStatus = licence?.Status,
            token
        });
    }

    private static string? CheckDetails(string business, string contact, string email, string dial, string phone)
    {
        if (business.Length < 2) return "Enter the business name.";
        if (contact.Length < 2) return "Enter the contact name.";
        if (!MailAddress.TryCreate(email, out var mail) || !mail.Host.Contains('.', StringComparison.Ordinal)) return "Enter a valid contact email.";
        if (!DialCodePattern().IsMatch(dial)) return "Choose the phone number's country code.";
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is < 4 or > 15 || phone.Any(ch => !(char.IsAsciiDigit(ch) || ch is ' ' or '-' or '(' or ')')))
            return "Enter a valid phone number (digits only, without the country code).";
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
    public (int Status, object Body) Register(RegisterRequest r, string ip)
    {
        if (!ValidMachine(r.MachineId)) return (400, new { ok = false, error = "bad_machine", message = "Missing machine id." });
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

        var m = customers.Seen(r.MachineId!, Clip(r.Product, 20), Clip(r.Platform, 20), Clip(r.AppVersion, 20), installId, business, DateTime.UtcNow);
        if (m.AccountId is { } existing && customers.GetAccount(existing) is { } already)
            return (409, new { ok = false, error = "already_registered", message = $"This business is already registered as \"{already.Username}\". Sign in at zonosoft.co.uk/portal." });
        if (licences.ForInstall(installId) is { Status: not "revoked" })
            return (409, new { ok = false, error = "already_licensed", message = "This business already has a licence." });
        if (customers.UsernameTaken(username))
            return (409, new { ok = false, error = "username_taken", message = "That username is already used. Choose another, or sign in at zonosoft.co.uk/portal." });

        var account = customers.CreateAccount(username, password, business, contact, email, dial, new string(phone.Where(char.IsAsciiDigit).ToArray()));
        var (licence, _) = licences.Create(business, "", plan, seats, null, 1, "Registered from the app", status: "pending", accountId: account.Id, secrets: secrets);
        licences.Activate(licence, installId, business, Clip(r.DeviceName, 80), m.Platform, m.AppVersion);
        customers.LinkMachine(m.Id, account.Id);
        licences.Log(licence.Id, "registered", $"{business} · {plan} · {seats} team members · account {account.Id}", ip);
        var price = Price(plan, seats).ToString("0.##", CultureInfo.InvariantCulture);
        return (200, new
        {
            ok = true,
            message = $"Registered. Your licence ({plan}, {seats} team members, £{price}{(plan == "yearly" ? " a year" : " a month")}) " +
                      "starts as soon as payment is received — the app picks it up by itself. You can see your licence key and details " +
                      "at zonosoft.co.uk/portal with your username and password.",
            username
        });
    }

    // ---------- Portal ----------

    public (string? AccountId, string? Problem) Login(LoginRequest r, string ip)
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
        licences.Log(null, ok ? "portal-login" : "portal-login-failed", account.Id, ip);
        return ok ? (account.Id, null) : (null, "That username or password isn't right.");
    }

    public object Me(string accountId)
    {
        var a = customers.GetAccount(accountId)!;
        return new
        {
            ok = true,
            a.Username, a.BusinessName, a.ContactName, a.Email, a.PhoneCountry, a.Phone,
            created = a.CreatedUtc,
            licences = licences.ForAccount(accountId).Select(l => new
            {
                l.Id,
                key = licences.KeyOf(l.Id, secrets),
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

    public string? UpdateDetails(string accountId, DetailsRequest r)
    {
        var business = Clip(r.BusinessName, 80);
        var contact = Clip(r.ContactName, 80);
        var email = Clip(r.Email, 120);
        var dial = Clip(r.PhoneCountry, 10);
        var phone = Clip(r.Phone, 30);
        if (CheckDetails(business, contact, email, dial, phone) is { } problem) return problem;
        customers.UpdateDetails(accountId, business, contact, email, dial, new string(phone.Where(char.IsAsciiDigit).ToArray()));
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
