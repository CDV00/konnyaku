using System.Net;
using System.Net.Http.Json;
using Testcontainers.Redis;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Per-IP limits (here: forgot password, 4 a minute) with two API replicas, as the production stack runs them: shared
/// through Redis they hold for the whole stack; without Redis each replica counts alone.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RateLimitTests(PostgresFixture db) : IAsyncLifetime
{
    private const int Limit = 4;
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.2-alpine").Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _redis.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _redis.DisposeAsync();

    [Fact]
    public async Task Two_replicas_share_one_limit_through_redis()
    {
        var settings = Settings(_redis.GetConnectionString());
        await using var first = new KonnyakuApiFactory(db) { Settings = settings };
        await using var second = new KonnyakuApiFactory(db) { Settings = settings };
        var slug = await TenantAsync(first);

        var statuses = await ForgotAlternatingAsync(slug, first, second, 2 * Limit);

        statuses.Count(s => s == HttpStatusCode.Accepted).ShouldBe(Limit);
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).ShouldBe(Limit);
        var rejected = await first.ClientFor(slug).PostAsJsonAsync("/api/identity/password/forgot", Forgot(), Ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_redis_each_replica_counts_alone()
    {
        var settings = Settings(null);
        await using var first = new KonnyakuApiFactory(db) { Settings = settings };
        await using var second = new KonnyakuApiFactory(db) { Settings = settings };
        var slug = await TenantAsync(first);

        var statuses = await ForgotAlternatingAsync(slug, first, second, 2 * Limit);

        // The reason for Redis: two replicas let twice the limit through.
        statuses.Count(s => s == HttpStatusCode.Accepted).ShouldBe(2 * Limit);
    }

    [Fact]
    public async Task When_redis_is_unreachable_each_replica_still_limits()
    {
        // Nothing listens there: the API starts anyway and falls back to counting in memory.
        var settings = Settings("127.0.0.1:1,connectTimeout=500");
        await using var api = new KonnyakuApiFactory(db) { Settings = settings };
        var slug = await TenantAsync(api);

        var statuses = await ForgotAlternatingAsync(slug, api, api, Limit + 2);

        statuses.Count(s => s == HttpStatusCode.Accepted).ShouldBe(Limit);
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).ShouldBe(2);
    }

    private static Dictionary<string, string> Settings(string? redis)
    {
        var settings = new Dictionary<string, string> { ["RateLimits:PasswordResetPerMinute"] = Limit.ToString() };
        if (redis is not null)
        {
            settings["Redis:ConnectionString"] = redis;
        }

        return settings;
    }

    private static async Task<string> TenantAsync(KonnyakuApiFactory api) =>
        (await api.CreateTenantWithAdminAsync("rl", $"admin-{Guid.NewGuid():N}@example.com", "Admin-Pass-123")).Slug;

    private static object Forgot() => new { email = $"nobody-{Guid.NewGuid():N}@example.com" };

    private static async Task<List<HttpStatusCode>> ForgotAlternatingAsync(string slug, KonnyakuApiFactory first, KonnyakuApiFactory second, int count)
    {
        // Windows are whole minutes: do not start right before one ends, or the requests would fall into two windows.
        var intoWindow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 60_000;
        if (intoWindow > 50_000)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(60_000 - intoWindow + 200), Ct);
        }

        var clients = new[] { first.ClientFor(slug), second.ClientFor(slug) };
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add((await clients[i % 2].PostAsJsonAsync("/api/identity/password/forgot", Forgot(), Ct)).StatusCode);
        }

        return statuses;
    }
}
