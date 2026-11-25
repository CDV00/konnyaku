using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Konnyaku.Infrastructure.Redis;

/// <summary>
/// One Redis connection per process (rate-limit counters in the API, realtime signals from the API and the Worker), when
/// <c>Redis:ConnectionString</c> is set. Starts and keeps serving while Redis is down: commands fail fast instead of
/// queueing, and every caller has a fallback (in-memory counters; browsers catch up by polling).
/// </summary>
public static class KonnyakuRedis
{
    public static bool IsConfigured(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration["Redis:ConnectionString"]);

    public static IServiceCollection AddKonnyakuRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            options.BacklogPolicy = BacklogPolicy.FailFast;
            options.ConnectTimeout = 2000;
            options.AsyncTimeout = 1000;
            options.ClientName = $"konnyaku-{AppDomain.CurrentDomain.FriendlyName.Replace("Konnyaku.", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}";
            return ConnectionMultiplexer.Connect(options);
        });
        return services;
    }
}
