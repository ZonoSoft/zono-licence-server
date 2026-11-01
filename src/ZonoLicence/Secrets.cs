using System.Security.Cryptography;
using System.Text;

namespace ZonoLicence;

/// <summary>
/// Encrypts customers' personal details and licence keys in the database (AES-256-GCM), and makes lookup hashes
/// (HMAC-SHA256) for fields that have to be searched, such as the username. The 32-byte key is made on first run and
/// kept in the data folder, readable only by the service; it's included in the daily backup alongside the signing key.
/// </summary>
public sealed class Secrets
{
    private readonly byte[] _encrypt;
    private readonly byte[] _lookup;

    public Secrets(string keyPath)
    {
        if (!File.Exists(keyPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var master = File.ReadAllBytes(keyPath);
        _encrypt = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("zono-encrypt"));
        _lookup = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("zono-lookup"));
    }

    public string Protect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var plain = Encoding.UTF8.GetBytes(text);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encrypt, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return "e1:" + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("e1:", StringComparison.Ordinal)) return stored;
        var all = Convert.FromBase64String(stored[3..]);
        var nonce = all[..12];
        var tag = all[^16..];
        var cipher = all[12..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encrypt, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>A stable, keyed hash for looking something up without storing it readable (case and spaces ignored).</summary>
    public string Lookup(string text) =>
        Convert.ToHexString(HMACSHA256.HashData(_lookup, Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant())));

    // ---------- Passwords ----------

    private const int Iterations = 210_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool CheckPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(hash, expected);
    }

    /// <summary>A password-check that takes as long as a real one, for unknown usernames.</summary>
    public static void SpendPasswordTime(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], Iterations, HashAlgorithmName.SHA256, 32);
}
