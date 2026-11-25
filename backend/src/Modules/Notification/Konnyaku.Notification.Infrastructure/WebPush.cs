using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konnyaku.Notification.Application;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Konnyaku.Notification.Infrastructure;

public sealed class PushOptions
{
    /// <summary>VAPID public key: uncompressed P-256 point (65 bytes), base64url. From <c>infra/deploy/scripts/gen-secrets.sh</c>.</summary>
    public string? PublicKey { get; set; }

    /// <summary>VAPID private key: the 32-byte P-256 scalar, base64url.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Contact for push services (<c>mailto:</c> or <c>https:</c>), sent in every VAPID token.</summary>
    public string Subject { get; set; } = "mailto:admin@example.com";

    /// <summary>How long a push service keeps a push for an offline browser.</summary>
    public int TtlSeconds { get; set; } = 86400;

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Web Push with VAPID (RFC 8292), no payload: the push only wakes the service worker, which fetches the newest unread
/// item from the web app (signed in, so access is checked). No content crosses the push service, and no payload
/// encryption is needed. Push services: FCM, Mozilla, Apple, all plain HTTPS POST.
/// </summary>
public sealed class VapidPushSender : IPushSender, IDisposable
{
    private readonly PushOptions _options;
    private readonly ILogger<VapidPushSender> _logger;
    private readonly HttpClient _http;
    private readonly ECDsa? _key;
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = [];
    private readonly Lock _gate = new();

    public VapidPushSender(PushOptions options, ILogger<VapidPushSender> logger, HttpMessageHandler? handler = null)
    {
        _options = options;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _key = LoadKey(options, logger);
    }

    public bool IsConfigured => _key is not null;

    public string? PublicKey => IsConfigured ? _options.PublicKey : null;

    public async Task<PushOutcome> SendAsync(PushTarget target, CancellationToken cancellationToken)
    {
        if (_key is null || !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return PushOutcome.Rejected;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Token(endpoint)}, k={_options.PublicKey}");
        request.Headers.TryAddWithoutValidation("TTL", _options.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        request.Content = new ByteArrayContent([]);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            return Classify(response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Push to {Host} failed: {Error}", endpoint.Host, ex.GetType().Name);
            return PushOutcome.Transient;
        }
    }

    public static PushOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => PushOutcome.Delivered,
        404 or 410 => PushOutcome.Gone,
        429 or >= 500 => PushOutcome.Transient,
        _ => PushOutcome.Rejected,
    };

    /// <summary>ES256 JWT for the push service origin, reused for up to 12 hours (the limit is 24).</summary>
    public string Token(Uri endpoint)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_tokens.TryGetValue(audience, out var cached) && cached.Expires - now > TimeSpan.FromHours(1))
            {
                return cached.Token;
            }

            var expires = now.AddHours(12);
            var header = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
            var claims = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["aud"] = audience,
                ["exp"] = expires.ToUnixTimeSeconds(),
                ["sub"] = _options.Subject,
            }));
            var signature = _key!.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var token = $"{header}.{claims}.{Base64UrlEncoder.Encode(signature)}";
            _tokens[audience] = (token, expires);
            return token;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _key?.Dispose();
    }

    private static ECDsa? LoadKey(PushOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.PublicKey) || string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            return null;
        }

        try
        {
            var point = Base64UrlEncoder.DecodeBytes(options.PublicKey);
            var d = Base64UrlEncoder.DecodeBytes(options.PrivateKey);
            if (point.Length != 65 || point[0] != 0x04 || d.Length != 32)
            {
                throw new FormatException("VAPID keys must be a 65-byte uncompressed P-256 point and a 32-byte scalar (base64url).");
            }

            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = d,
                Q = new ECPoint { X = point[1..33], Y = point[33..] },
            });
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            logger.LogError("Web Push disabled: invalid VAPID keys ({Error})", ex.Message);
            return null;
        }
    }
}
