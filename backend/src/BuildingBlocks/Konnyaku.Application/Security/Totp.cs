using System.Security.Cryptography;
using System.Text;

namespace Konnyaku.Application.Security;

/// <summary>Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits), as authenticator apps expect.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The time step a code belongs to, accepting one step of clock drift either way; null if it matches none.</summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now)
    {
        var digits = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        for (var step = current - 1; step <= current + 1; step++)
        {
            // Compare every candidate in constant time; do not stop at the first match.
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
            {
                matched = step;
            }
        }

        return matched;
    }

    public static string OtpAuthUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 base32 without padding: the format authenticator apps use for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    public static bool TryDecode(string? text, out byte[] data)
    {
        var clean = (text ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                data = [];
                return false;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        data = [.. bytes];
        return data.Length > 0;
    }
}

/// <summary>Single-use recovery codes: shown once, stored as SHA-256 hashes (they are random, so no slow hash is needed).</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static IReadOnlyList<string> Generate() =>
        Enumerable.Range(0, Count)
            .Select(_ => $"{RandomNumberGenerator.GetString(Alphabet, 5)}-{RandomNumberGenerator.GetString(Alphabet, 5)}")
            .ToList();

    public static string Hash(string code)
    {
        var normalized = new string((code ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
