using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ZonoLicence;

/// <summary>One installer on the server.</summary>
public sealed record DownloadFile(string Name, string Product, string Platform, string Version, long Bytes, string Sha256, DateTime UploadedUtc);

/// <summary>
/// The app installers people download from the website. ZonoSoft uploads them on the admin page; they're kept in the
/// data folder and the website lists the newest of each (Zono Workforce / Zono Team, Windows / Android).
/// File names follow the build names, e.g. ZonoWorkforceSuite-v0.32.0-Windows.zip or ZonoTeam-v0.32.0-Android.apk.
/// </summary>
public sealed partial class Downloads(string folder)
{
    public const long MaxBytes = 600L * 1024 * 1024;

    [GeneratedRegex(@"^(ZonoWorkforceSuite|ZonoWorkforce|ZonoTeam)-v(\d+\.\d+\.\d+)-(Windows|Android|iOS|Mac|Linux)[A-Za-z0-9_-]*\.(zip|apk|exe|msi|ipa|dmg)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    public static bool ValidName(string? name) => name is not null && name.Length <= 120 && NamePattern().IsMatch(name);

    private static (string Product, string Platform, string Version)? Parse(string name)
    {
        var m = NamePattern().Match(name);
        if (!m.Success) return null;
        var product = m.Groups[1].Value.Equals("ZonoTeam", StringComparison.OrdinalIgnoreCase) ? "team" : "workforce";
        var platform = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups[3].Value.ToLowerInvariant());
        return (product, platform, m.Groups[2].Value);
    }

    public string PathOf(string name) => Path.Combine(folder, name);

    public List<DownloadFile> All()
    {
        if (!Directory.Exists(folder)) return [];
        var list = new List<DownloadFile>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (Parse(name) is not { } p) continue;
            var info = new FileInfo(path);
            var sha = File.Exists(path + ".sha256") ? File.ReadAllText(path + ".sha256").Trim() : "";
            list.Add(new DownloadFile(name, p.Product, p.Platform, p.Version, info.Length, sha, info.LastWriteTimeUtc));
        }
        return list.OrderByDescending(f => Version.Parse(f.Version)).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The newest file for each app and platform.</summary>
    public List<DownloadFile> Latest() =>
        All().GroupBy(f => (f.Product, f.Platform)).Select(g => g.First()).OrderBy(f => f.Product).ThenBy(f => f.Platform).ToList();

    /// <summary>Saves an upload (written to a temporary file first, so a broken upload never replaces a good file).</summary>
    public async Task<DownloadFile> SaveAsync(string name, Stream body, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + name + ".uploading");
        long total = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var file = File.Create(temp))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await body.ReadAsync(buffer, cancel)) > 0)
            {
                total += read;
                if (total > MaxBytes) throw new InvalidDataException("The file is too big (over 600 MB).");
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
        }
        if (total == 0)
        {
            File.Delete(temp);
            throw new InvalidDataException("The file was empty.");
        }
        File.Move(temp, PathOf(name), overwrite: true);
        await File.WriteAllTextAsync(PathOf(name) + ".sha256", Convert.ToHexStringLower(sha.GetHashAndReset()), cancel);
        return All().First(f => f.Name == name);
    }

    public bool Delete(string name)
    {
        if (!ValidName(name) || !File.Exists(PathOf(name))) return false;
        File.Delete(PathOf(name));
        if (File.Exists(PathOf(name) + ".sha256")) File.Delete(PathOf(name) + ".sha256");
        return true;
    }

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".apk" => "application/vnd.android.package-archive",
        ".zip" => "application/zip",
        ".msi" => "application/x-msi",
        _ => "application/octet-stream"
    };
}
