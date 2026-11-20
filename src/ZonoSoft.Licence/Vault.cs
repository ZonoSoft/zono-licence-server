using System.Text.RegularExpressions;

namespace ZonoSoft.Licence;

/// <summary>
/// ZonoSoft's private backups (for example the Android signing password). Only the admin key can save, list or
/// download them: they're never on the website or in the customer portal. Each file is encrypted on disk
/// (AES-256-GCM with the server's secret key) in a folder only the service can read.
/// </summary>
public sealed partial class Vault
{
    public const long MaxBytes = 1024 * 1024;
    private readonly string _dir;
    private readonly Secrets _secrets;

    public Vault(string dir, Secrets secrets)
    {
        _dir = dir;
        _secrets = secrets;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public sealed record Item(string Name, long Bytes, DateTime SavedUtc);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,79}$")]
    private static partial Regex NameRule();

    public static bool ValidName(string? name) => name is not null && NameRule().IsMatch(name) && !name.Contains("..");

    private string PathOf(string name) => Path.Combine(_dir, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)) + ".vault");

    public IEnumerable<Item> All() =>
        Directory.EnumerateFiles(_dir, "*.vault").Select(p =>
        {
            var name = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(Path.GetFileNameWithoutExtension(p)));
            var lines = File.ReadAllLines(p);
            return new Item(name, long.Parse(lines[0]), File.GetLastWriteTimeUtc(p));
        }).OrderBy(i => i.Name);

    public async Task<Item> SaveAsync(string name, Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBytes) throw new InvalidDataException("Files in the vault can be up to 1 MB.");
            ms.Write(buffer, 0, read);
        }
        if (ms.Length == 0) throw new InvalidDataException("That file is empty.");
        var bytes = ms.ToArray();
        var path = PathOf(name);
        var temp = path + ".tmp";
        await File.WriteAllLinesAsync(temp, [bytes.Length.ToString(), _secrets.Protect(Convert.ToBase64String(bytes))], ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
        Array.Clear(bytes);
        return new Item(name, ms.Length, DateTime.UtcNow);
    }

    public byte[]? Read(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        return Convert.FromBase64String(_secrets.Unprotect(File.ReadAllLines(path)[1]));
    }

    public bool Delete(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
