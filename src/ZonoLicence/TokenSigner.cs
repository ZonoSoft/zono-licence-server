using System.Security.Cryptography;
using System.Text;

namespace ZonoLicence;

/// <summary>
/// Signs licence tokens with an ECDSA P-256 key that never leaves the server. The apps hold only the public key, so they
/// can check a token offline but can't make one. Token = base64url(payload JSON) + "." + base64url(signature, 64 bytes).
/// </summary>
public sealed class TokenSigner : IDisposable
{
    private readonly ECDsa _key;

    public TokenSigner(string keyPath)
    {
        _key = ECDsa.Create();
        if (File.Exists(keyPath))
        {
            _key.ImportFromPem(File.ReadAllText(keyPath));
        }
        else
        {
            _key.GenerateKey(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
            File.WriteAllText(keyPath, _key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>The public key the apps embed (SubjectPublicKeyInfo, PEM).</summary>
    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    public string Sign(string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var signature = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return B64(payload) + "." + B64(signature);
    }

    /// <summary>The payload JSON when the signature is this server's, otherwise null.</summary>
    public string? Verify(string token) => Verify(token, _key);

    public static string? Verify(string token, ECDsa publicKey)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0) return null;
        try
        {
            var payload = FromB64(token[..dot]);
            var signature = FromB64(token[(dot + 1)..]);
            return publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? Encoding.UTF8.GetString(payload)
                : null;
        }
        catch (FormatException) { return null; }
    }

    public static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    public void Dispose() => _key.Dispose();
}
