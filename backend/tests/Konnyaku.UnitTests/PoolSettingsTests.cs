using Konnyaku.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Konnyaku.UnitTests;

public class PoolSettingsTests
{
    private const string ConnectionString = "Host=postgres;Database=konnyaku;Username=kk_crm;Password=secret";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Without_settings_the_connection_string_is_unchanged() =>
        ServiceCollectionExtensions.WithPoolSettings(ConnectionString, Config()).ShouldBe(ConnectionString);

    [Fact]
    public void Pool_size_and_idle_lifetime_come_from_configuration()
    {
        var result = new NpgsqlConnectionStringBuilder(ServiceCollectionExtensions.WithPoolSettings(
            ConnectionString, Config(("Database:MaxPoolSize", "8"), ("Database:ConnectionIdleLifetime", "60"))));

        result.MaxPoolSize.ShouldBe(8);
        result.ConnectionIdleLifetime.ShouldBe(60);
        result.Username.ShouldBe("kk_crm");
        result.Password.ShouldBe("secret");
    }
}
