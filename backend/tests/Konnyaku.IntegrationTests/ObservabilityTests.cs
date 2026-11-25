using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Telemetry and error reporting never stop the API: on with an unreachable collector and error tracker, or switched
/// off again, the API starts and serves requests that touch PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ObservabilityTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Api_serves_with_telemetry_on_or_off(bool enabled)
    {
        await using var api = new KonnyakuApiFactory(db)
        {
            Settings = new Dictionary<string, string>
            {
                // Closed ports: exports and error reports fail in the background, requests must not notice.
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["OTEL_SDK_DISABLED"] = enabled ? "false" : "true",
                ["Sentry:Dsn"] = enabled ? "http://public@127.0.0.1:9/1" : "",
            },
        };

        (await api.CreateClient().GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tenant = await api.CreateTenantWithAdminAsync("otel", $"admin-{Guid.NewGuid():N}@example.com", "Admin-Pass-123");
        var signIn = await api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/identity/staff/login",
            new { email = "nobody@example.com", password = "Wrong-Pass-123" }, Ct);
        signIn.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (api.Services.GetService<TracerProvider>() is not null).ShouldBe(enabled);
    }
}
