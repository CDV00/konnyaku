using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Konnyaku.Infrastructure.Http;

/// <summary>
/// Per-IP fixed-window limits of the anonymous endpoints (<see cref="RateLimitPolicies"/>). With
/// <c>Redis:ConnectionString</c> the counters live in Redis and are shared by every API replica, so two replicas do not
/// double the limit; without it (dev, tests, a single replica) each process counts in memory. When Redis cannot be
/// reached a request is counted in memory instead of failing or passing unchecked.
/// </summary>
public static class KonnyakuRateLimiting
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddKonnyakuRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var redis = configuration["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redis);
                // Start (and keep serving) while Redis is down; fail fast instead of queueing commands, so a Redis outage
                // costs one round trip at most before the in-memory fallback answers.
                options.AbortOnConnectFail = false;
                options.BacklogPolicy = BacklogPolicy.FailFast;
                options.ConnectTimeout = 2000;
                options.AsyncTimeout = 1000;
                options.ClientName = "konnyaku-api";
                return ConnectionMultiplexer.Connect(options);
            });
            services.AddSingleton<RedisWindowCounter>();
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            AddPerIpPolicy(options, RateLimitPolicies.PublicForms, "RateLimits:PublicFormsPerMinute", 10);
            AddPerIpPolicy(options, RateLimitPolicies.PasswordReset, "RateLimits:PasswordResetPerMinute", 5);
        });
        return services;
    }

    private static void AddPerIpPolicy(RateLimiterOptions options, string policy, string limitKey, int defaultLimit) =>
        options.AddPolicy(policy, context =>
        {
            var services = context.RequestServices;
            var limit = services.GetRequiredService<IConfiguration>().GetValue(limitKey, defaultLimit);
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var counter = services.GetService<RedisWindowCounter>();
            return counter is null
                ? RateLimitPartition.GetFixedWindowLimiter(ip, _ => LocalOptions(limit))
                : RateLimitPartition.Get(ip, key => new RedisFixedWindowRateLimiter(counter, $"{policy}:{Hash(key)}", limit, Window));
        });

    internal static FixedWindowRateLimiterOptions LocalOptions(int limit) => new()
    {
        PermitLimit = limit,
        Window = Window,
        QueueLimit = 0,
    };

    /// <summary>Redis keys never hold the client IP itself (personal data), only a short hash of it.</summary>
    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

/// <summary>
/// One counter per key and window in Redis (INCR + PEXPIRE in one script). Windows are aligned on the Unix epoch, so
/// every replica agrees on which window a request falls in.
/// </summary>
public sealed class RedisWindowCounter(IConnectionMultiplexer redis, TimeProvider clock, ILogger<RedisWindowCounter> logger)
{
    private const string Script = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        return n
        """;

    private long _lastWarning;

    /// <summary>Counts one hit; null when Redis is unavailable (the caller falls back to its own counter).</summary>
    public async Task<(long Count, TimeSpan RetryAfter)?> HitAsync(string key, TimeSpan window)
    {
        var windowMs = (long)window.TotalMilliseconds;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var index = now / windowMs;
        try
        {
            var count = (long)await redis.GetDatabase().ScriptEvaluateAsync(Script, [new RedisKey($"kk:rl:{key}:{index}")], [windowMs]);
            return (count, TimeSpan.FromMilliseconds(((index + 1) * windowMs) - now));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // At most one warning a minute: a Redis outage must not flood the logs.
            var last = Interlocked.Read(ref _lastWarning);
            if (now - last > 60_000 && Interlocked.CompareExchange(ref _lastWarning, now, last) == last)
            {
                logger.LogWarning(ex, "Redis unavailable for rate limiting; counting per process until it is back");
            }

            return null;
        }
    }
}

internal sealed class RedisFixedWindowRateLimiter(RedisWindowCounter counter, string key, int limit, TimeSpan window) : RateLimiter
{
    private readonly FixedWindowRateLimiter _fallback = new(KonnyakuRateLimiting.LocalOptions(limit));
    private long _lastUsedTicks = Environment.TickCount64;

    /// <summary>Idle once a whole window passed without requests, so the partition can be dropped from memory.</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastUsedTicks));
            return idle >= window ? idle : null;
        }
    }

    public override RateLimiterStatistics? GetStatistics() => _fallback.GetStatistics();

    // The middleware tries a synchronous acquire first and awaits AcquireAsync when it fails: count only there.
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Lease.Rejected(null);

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastUsedTicks, Environment.TickCount64);
        var hit = await counter.HitAsync(key, window);
        if (hit is not { } result)
        {
            return _fallback.AttemptAcquire(permitCount);
        }

        return result.Count <= limit ? Lease.Acquired : Lease.Rejected(result.RetryAfter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fallback.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public static readonly Lease Acquired = new(true, null);

        public static Lease Rejected(TimeSpan? retryAfter) => new(false, retryAfter);

        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = retryAfter;
            return retryAfter is not null && metadataName == MetadataName.RetryAfter.Name;
        }
    }
}
