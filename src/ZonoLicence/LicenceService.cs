using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZonoLicence;

public sealed record ActivateRequest(string? Key, string? InstallId, string? BusinessName, string? DeviceName, string? Platform, string? AppVersion);
public sealed record RefreshRequest(string? Token, string? InstallId, string? AppVersion);
public sealed record ApiResult(int Status, string? Token, string? Error, string? Message);

/// <summary>
/// The rules: a licence key activates on up to <see cref="Licence.MaxActivations"/> installs (one business's shared data
/// counts once); each token is signed and valid for <see cref="TokenDays"/> days, and the apps refresh it weekly.
/// </summary>
public sealed class LicenceService(LicenceStore store, TokenSigner signer, TimeProvider clock)
{
    /// <summary>How long a token works without reaching the server (the apps refresh weekly).</summary>
    public const int TokenDays = 35;
    public const int IncludedTeamMembers = 5;

    public string Token(Licence l, string installId, string businessName)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var status = l.Status != "active" ? l.Status
            : l.Expires is { } e && DateOnly.FromDateTime(now) > e ? "expired"
            : "active";
        var payload = new JsonObject
        {
            ["v"] = 1,
            ["lid"] = l.Id,
            ["iid"] = installId,
            ["bus"] = businessName.Length > 0 ? businessName : l.BusinessName,
            ["plan"] = l.Plan,
            ["seats"] = l.TeamSeats,
            ["exp"] = l.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["st"] = status,
            ["iat"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["until"] = now.AddDays(TokenDays).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        };
        return signer.Sign(payload.ToJsonString());
    }

    private static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    public ApiResult Activate(ActivateRequest r, string ip)
    {
        var installId = Clip(r.InstallId, 64);
        if (string.IsNullOrWhiteSpace(r.Key) || installId.Length < 8)
            return new(400, null, "bad_request", "Enter the licence key.");
        var licence = store.FindByKey(r.Key);
        if (licence is null)
        {
            store.Log(null, "activate-failed", "unknown key", ip);
            return new(404, null, "unknown_key", "That licence key wasn't found. Check it and try again.");
        }
        if (licence.Status == "revoked")
        {
            store.Log(licence.Id, "activate-failed", "revoked", ip);
            return new(403, null, "revoked", "This licence has been cancelled. Contact ZonoSoft.");
        }
        var business = Clip(r.BusinessName, 80);
        if (!store.Activate(licence, installId, business, Clip(r.DeviceName, 80), Clip(r.Platform, 20), Clip(r.AppVersion, 20)))
        {
            store.Log(licence.Id, "activate-failed", "no free activations", ip);
            return new(409, null, "in_use",
                "This licence key is already in use for another business or PC. Deactivate it there first (Settings → About → Licence), or contact ZonoSoft to move it.");
        }
        store.Log(licence.Id, "activated", $"{business} · {Clip(r.DeviceName, 80)} · {Clip(r.AppVersion, 20)}", ip);
        return new(200, Token(licence, installId, business), null, null);
    }

    /// <summary>Reads a token this server signed (null when the signature doesn't match).</summary>
    public JsonObject? Read(string? token) =>
        token is { Length: > 0 } && signer.Verify(token) is { } json ? JsonNode.Parse(json) as JsonObject : null;

    public ApiResult Refresh(RefreshRequest r, string ip)
    {
        var payload = Read(r.Token);
        if (payload is null) return new(400, null, "bad_token", "The saved licence isn't valid. Enter the licence key again.");
        var lid = (string?)payload["lid"];
        var iid = (string?)payload["iid"];
        var licence = lid is null ? null : store.Get(lid);
        if (licence is null || iid is null) return new(404, null, "unknown_licence", "This licence no longer exists. Enter a licence key.");
        if (!store.IsActive(licence.Id, iid))
            return new(409, null, "released", "This licence was deactivated for this business. Enter the licence key again.");
        var business = (string?)payload["bus"] ?? "";
        store.Activate(licence, iid, business, "", "", Clip(r.AppVersion, 20));   // updates "last seen"
        return new(200, Token(licence, iid, business), null, null);
    }

    public ApiResult Deactivate(RefreshRequest r, string ip)
    {
        var payload = Read(r.Token);
        if (payload is null || (string?)payload["lid"] is not { } lid || (string?)payload["iid"] is not { } iid)
            return new(400, null, "bad_token", "The saved licence isn't valid.");
        store.Release(lid, iid);
        store.Log(lid, "deactivated", (string?)payload["bus"] ?? "", ip);
        return new(200, null, null, "Deactivated. The licence key can now be used elsewhere.");
    }
}
