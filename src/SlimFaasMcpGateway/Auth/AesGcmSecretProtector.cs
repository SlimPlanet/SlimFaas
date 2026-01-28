using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SlimFaasMcpGateway.Options;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Auth;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecurityOptions> options)
    {
        var keyB64 = options.Value.DiscoveryTokenEncryptionKey;
        if (string.IsNullOrWhiteSpace(keyB64))
            throw new ApiException(500, "Security:DiscoveryTokenEncryptionKey is missing.");

        _key = Convert.FromBase64String(keyB64);
        if (_key.Length != 32)
            throw new ApiException(500, "Security:DiscoveryTokenEncryptionKey must be 32 bytes (base64).");
    }

    public string Protect(string plaintext)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key);
        aes.Encrypt(nonce, pt, ct, tag);

        // format: base64(nonce).base64(tag).base64(cipher)
        return $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ct)}";
    }

    public string Unprotect(string protectedValue)
    {
        if (protectedValue is null) throw new ArgumentNullException(nameof(protectedValue));
        var parts = protectedValue.Split('.', 3);
        if (parts.Length != 3) throw new ApiException(500, "Invalid protected secret format.");

        var nonce = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var ct = Convert.FromBase64String(parts[2]);
        var pt = new byte[ct.Length];

        using var aes = new AesGcm(_key);
        aes.Decrypt(nonce, ct, tag, pt);

        return Encoding.UTF8.GetString(pt);
    }
}
