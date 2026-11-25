using System.Text;
using System.Text.Json;
using Konnyaku.Notification.Application;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace Konnyaku.Notification.Infrastructure;

public sealed class RealtimeOptions
{
    /// <summary>
    /// WebSocket address of the realtime service as the browser sees it: relative (<c>/rt</c>, same host through Caddy) or
    /// absolute (<c>ws://localhost:5100/rt</c> in dev).
    /// </summary>
    public string Url { get; set; } = "/rt";

    public int TicketSeconds { get; set; } = 60;
}

/// <summary>Redis channel of one user; the realtime service subscribes to <c>user:*</c> and forwards to that user's sockets.</summary>
public static class RealtimeChannels
{
    public const string Prefix = "user:";

    public static string ForUser(Guid userId) => Prefix + userId.ToString("D");

    /// <summary><c>{"t":"inbox","id":"…","seq":812}</c>: type, id and sequence only, never content.</summary>
    public static string Payload(RealtimeSignal signal)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", signal.Type);
            if (signal.Id is { } id)
            {
                json.WriteString("id", id);
            }

            if (signal.Seq > 0)
            {
                json.WriteNumber("seq", signal.Seq);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>PUBLISH on Redis (fire and forget: a lost signal is caught up by the next sync of the browser).</summary>
public sealed class RedisRealtimePublisher(IConnectionMultiplexer redis, ILogger<RedisRealtimePublisher> logger) : IRealtimePublisher
{
    public async Task PublishAsync(Guid userId, RealtimeSignal signal, CancellationToken cancellationToken)
    {
        if (!redis.IsConnected)
        {
            logger.LogDebug("Redis not connected: realtime signal {Type} dropped", signal.Type);
            return;
        }

        await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(RealtimeChannels.ForUser(userId)), RealtimeChannels.Payload(signal));
    }
}

/// <summary>Without Redis (dev without the realtime service, tests): browsers fall back to polling.</summary>
public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task PublishAsync(Guid userId, RealtimeSignal signal, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record RealtimeTicketView(string Ticket, DateTimeOffset ExpiresAt, string Url);

/// <summary>
/// Short-lived tickets for the <c>/rt</c> WebSocket, signed with the secret shared with the realtime service (the same
/// one as classroom tickets, another audience). The service only learns the user and tenant ids.
/// </summary>
public sealed class RealtimeTickets(RealtimeOptions options, string? secret, TimeProvider clock)
{
    public const string Issuer = "konnyaku";
    public const string Audience = "konnyaku-realtime";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public bool IsConfigured => secret is not null && Encoding.UTF8.GetByteCount(secret) >= 32;

    public RealtimeTicketView? Issue(Guid userId, Guid tenantId)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var expires = now.AddSeconds(options.TicketSeconds);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["tid"] = tenantId.ToString(),
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret!)), SecurityAlgorithms.HmacSha256),
        });
        return new RealtimeTicketView(token, expires, options.Url);
    }
}
