using Konnyaku.Application.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Konnyaku.Infrastructure.Storage;

public static class ObjectStorageExtensions
{
    /// <summary>Registers <see cref="IObjectStorage"/> from the <c>Storage</c> section (once, whoever calls it first).</summary>
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IObjectStorage)))
        {
            return services;
        }

        services.Configure<ObjectStorageOptions>(configuration.GetSection(ObjectStorageOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IObjectStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ObjectStorageOptions>>();
            return options.Value.Provider switch
            {
                ObjectStorageOptions.S3Provider => new S3ObjectStorage(options, sp.GetRequiredService<TimeProvider>()),
                ObjectStorageOptions.LocalDiskProvider => new LocalDiskStorage(options, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>()),
                var other => throw new InvalidOperationException($"Unknown Storage:Provider '{other}' (LocalDisk or S3)."),
            };
        });
        if (string.Equals(configuration[$"{ObjectStorageOptions.Section}:Provider"], ObjectStorageOptions.S3Provider, StringComparison.Ordinal))
        {
            services.AddHostedService<S3CorsInitializer>();
        }

        return services;
    }
}
