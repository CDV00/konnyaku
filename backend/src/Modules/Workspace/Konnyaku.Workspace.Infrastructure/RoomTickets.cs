using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konnyaku.Workspace.Application;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Konnyaku.Workspace.Infrastructure;

/// <summary>Settings of the built-in classroom (section <c>Rtc</c>).</summary>
public sealed class RtcOptions
{
    public const string Section = "Rtc";

    /// <summary>HMAC secret shared with the signaling service (at least 32 bytes). Empty: the built-in classroom is off.</summary>
    public string? SignalingSecret { get; set; }

    /// <summary>WebSocket URL of the signaling service; a path (default) is resolved against the tenant's own host.</summary>
    public string SignalingUrl { get; set; } = "/rtc/ws";

    /// <summary>A ticket only opens the WebSocket; the room page asks for a new one on every reconnect.</summary>
    public int TicketSeconds { get; set; } = 120;

    public string[] StunUrls { get; set; } = [];

    /// <summary>TURN servers (coturn with <c>use-auth-secret</c>), e.g. <c>turn:turn.example.com:3478?transport=udp</c>.</summary>
    public string[] TurnUrls { get; set; } = [];

    /// <summary>coturn <c>static-auth-secret</c>: credentials follow the TURN REST API (username = expiry:user).</summary>
    public string? TurnSecret { get; set; }

    public int TurnCredentialHours { get; set; } = 6;
}

internal sealed class RoomTickets(IOptions<RtcOptions> options, TimeProvider clock) : IRoomTickets
{
    /// <summary>Audience of tickets checked by the signaling service.</summary>
    public const string TicketAudience = "konnyaku-rtc";

    /// <summary>Issuer/audience of the tokens the signaling service attaches to its reports to the API.</summary>
    public const string ServiceIssuer = "konnyaku-rtc";
    public const string ServiceAudience = "konnyaku-api";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    private RtcOptions Options => options.Value;

    public bool IsConfigured => Key is not null;

    public string SignalingUrl => Options.SignalingUrl;

    private SymmetricSecurityKey? Key =>
        Options.SignalingSecret is { } secret && Encoding.UTF8.GetByteCount(secret) >= 32
            ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
            : null;

    public IssuedTicket Issue(RoomTicketClaims claims)
    {
        var key = Key ?? throw new InvalidOperationException("Rtc:SignalingSecret must be configured with at least 32 bytes.");
        var now = clock.GetUtcNow();
        var expires = now.AddSeconds(Options.TicketSeconds);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "konnyaku",
            Audience = TicketAudience,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = claims.UserId.ToString(),
                ["tid"] = claims.TenantId.ToString(),
                ["sid"] = claims.SessionId.ToString(),
                ["name"] = claims.DisplayName,
                ["role"] = claims.Role,
                // When the session ends: the signaling service closes rooms left open after that.
                ["ends"] = claims.SessionEndsAt.ToUnixTimeSeconds(),
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
        return new IssuedTicket(token, expires);
    }

    public IReadOnlyList<IceServerView> IceServers(Guid userId)
    {
        var servers = new List<IceServerView>();
        if (Options.StunUrls.Length > 0)
        {
            servers.Add(new IceServerView(Options.StunUrls, null, null));
        }

        if (Options.TurnUrls.Length > 0 && !string.IsNullOrEmpty(Options.TurnSecret))
        {
            // TURN REST API (draft-uberti-behave-turn-rest): coturn recomputes the password from its shared secret.
            var expiry = clock.GetUtcNow().AddHours(Options.TurnCredentialHours).ToUnixTimeSeconds();
            var username = string.Create(CultureInfo.InvariantCulture, $"{expiry}:{userId:N}");
            var credential = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Options.TurnSecret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerView(Options.TurnUrls, username, credential));
        }

        return servers;
    }

    public async Task<bool> IsValidServiceTokenAsync(string token)
    {
        if (Key is not { } key || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = ServiceIssuer,
            ValidAudience = ServiceAudience,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
            RequireExpirationTime = true,
        });
        return result.IsValid;
    }
}
