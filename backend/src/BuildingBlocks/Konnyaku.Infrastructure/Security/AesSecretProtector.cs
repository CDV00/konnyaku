using System.Security.Cryptography;
using System.Text;
using Konnyaku.Application.Security;
using Microsoft.Extensions.Configuration;

namespace Konnyaku.Infrastructure.Security;

/// <summary>AES-GCM with a key derived from <c>Auth:SecretKey</c> (or, if unset, <c>Auth:SigningKey</c>).</summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private readonly byte[] _key;

    public AesSecretProtector(IConfiguration configuration)
    {
        var material = configuration["Auth:SecretKey"] is { Length: > 0 } own ? own : configuration["Auth:SigningKey"];
        if (string.IsNullOrEmpty(material) || Encoding.UTF8.GetByteCount(material) < 32)
        {
            throw new InvalidOperationException("Auth:SecretKey or Auth:SigningKey must be configured with at least 32 bytes.");
        }

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(material), 32, info: Encoding.UTF8.GetBytes("konnyaku-account-secrets"));
    }

    public string Protect(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[secret.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, secret, cipher, tag);
        return $"{Version}.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(cipher)}.{Convert.ToBase64String(tag)}";
    }

    public byte[] Unprotect(string protectedSecret)
    {
        var parts = protectedSecret.Split('.');
        if (parts.Length != 4 || parts[0] != Version)
        {
            throw new CryptographicException("Unknown secret format.");
        }

        var nonce = Convert.FromBase64String(parts[1]);
        var cipher = Convert.FromBase64String(parts[2]);
        var tag = Convert.FromBase64String(parts[3]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
