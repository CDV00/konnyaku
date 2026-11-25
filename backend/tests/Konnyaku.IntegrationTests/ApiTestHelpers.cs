using System.Net.Http.Headers;
using System.Net.Http.Json;
using Konnyaku.Composition;
using Konnyaku.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Konnyaku.IntegrationTests;

/// <summary>The real API host (auth, tenant resolution, all modules) against the test database.</summary>
/// <remarks>The public-form captcha is off unless <c>captcha</c> is true (only <c>CaptchaTests</c> need it).</remarks>
public sealed class KonnyakuApiFactory(PostgresFixture db, bool captcha = false) : WebApplicationFactory<Program>
{
    public const string PlatformKey = "test-platform-key";
    public const string BaseDomain = "test.local";
    public const string AdminHost = "admin.test.local";
    public const string CaptchaKey = "test-captcha-key-0123456789abcdef-0123456789";
    public const string RtcSecret = "test-rtc-secret-0123456789abcdef-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var module in KonnyakuModules.All)
        {
            builder.UseSetting($"ConnectionStrings:{module.Name}", db.ModuleConnectionString(module.Name));
        }

        builder.UseSetting("Auth:SigningKey", "test-signing-key-0123456789abcdef-0123456789");
        builder.UseSetting("Platform:AdminKey", PlatformKey);
        builder.UseSetting("Platform:BaseDomain", BaseDomain);
        builder.UseSetting("RateLimits:PublicFormsPerMinute", "1000");
        builder.UseSetting("RateLimits:PasswordResetPerMinute", "1000");
        // Seeded flows sign in with a password alone; AccountSecurityTests turn 2FA enforcement on where they test it.
        builder.UseSetting("Auth:TwoFactor:Enforce", "false");
        builder.UseSetting("Workspace:DevTools", "true");
        builder.UseSetting("Captcha:Enabled", captcha ? "true" : "false");
        builder.UseSetting("Captcha:Key", CaptchaKey);
        builder.UseSetting("Rtc:SignalingSecret", RtcSecret);
        builder.UseSetting("Rtc:StunUrls:0", "stun:turn.test.local:3478");
        builder.UseSetting("Rtc:TurnUrls:0", "turn:turn.test.local:3478?transport=udp");
        builder.UseSetting("Rtc:TurnSecret", "test-turn-secret");
        builder.ConfigureServices(services =>
        {
            foreach (var contextType in OutboxContexts)
            {
                services.AddSingleton(typeof(OutboxProcessor<>).MakeGenericType(contextType));
            }
        });
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => TestServices?.Invoke(services));
    }

    /// <summary>Extra configuration for one test class (e.g. faster retries).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>Replaces services after the modules registered theirs (e.g. a fake email sender).</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    private static IEnumerable<Type> OutboxContexts => KonnyakuModules.All.SelectMany(m => m.DbContextTypes);

    /// <summary>Runs every module outbox until no message is left, like the Worker host does continuously.</summary>
    public async Task DrainOutboxAsync()
    {
        var processors = OutboxContexts.Select(t => Services.GetRequiredService(typeof(OutboxProcessor<>).MakeGenericType(t))).ToList();
        for (var round = 0; round < 20; round++)
        {
            var processed = 0;
            foreach (var processor in processors)
            {
                processed += await (Task<int>)processor.GetType().GetMethod(nameof(OutboxProcessor<Konnyaku.Organization.Infrastructure.OrganizationDbContext>.ProcessBatchAsync))!
                    .Invoke(processor, [100, CancellationToken.None])!;
            }

            if (processed == 0)
            {
                return;
            }
        }
    }

    /// <summary>Client acting on a tenant's host, as the web apps' server side does.</summary>
    public HttpClient ClientFor(string slug, string? accessToken = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Host", $"{slug}.{BaseDomain}");
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public HttpClient PlatformClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Platform-Key", PlatformKey);
        return client;
    }

    public async Task<TenantDto> CreateTenantWithAdminAsync(string slugPrefix, string adminEmail, string adminPassword)
    {
        var slug = $"{slugPrefix}-{Guid.NewGuid():N}"[..20];
        var platform = PlatformClient();
        var created = await platform.PostAsJsonAsync("/api/platform/tenants", new
        {
            slug,
            name = $"Center {slug}",
            defaultLocale = "vi",
            enabledLocales = new[] { "vi", "en" },
            timeZone = "Asia/Ho_Chi_Minh",
        });
        created.EnsureSuccessStatusCode();
        var tenant = (await created.Content.ReadFromJsonAsync<TenantDto>())!;

        var admin = await platform.PostAsJsonAsync("/api/identity/bootstrap/tenant-admin", new
        {
            tenantId = tenant.Id,
            email = adminEmail,
            displayName = "Admin Test",
            password = adminPassword,
        });
        admin.EnsureSuccessStatusCode();
        return tenant;
    }

    public async Task<SignInDto> StaffLoginAsync(string slug, string email, string password)
    {
        var response = await ClientFor(slug).PostAsJsonAsync("/api/identity/staff/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SignInDto>())!;
    }
}

public sealed record TenantDto(Guid Id, string Slug, string Name, string DefaultLocale, string[] EnabledLocales, string TimeZone, string[] Hosts);

public sealed record SignInDto(string AccessToken, string Audience, DateTimeOffset ExpiresAt, string DisplayName, string[] Roles, bool MustChangePassword,
    string? Challenge = null, string? NextStep = null);

public sealed record ErrorDto(string Code);

/// <summary>The page shape every list endpoint returns.</summary>
public sealed record PageDto<T>(T[] Items, int Total, int PageNumber, int PageSize);

public static class PageClientExtensions
{
    /// <summary>The rows of one page of a list endpoint (<c>{ items, total, pageNumber, pageSize }</c>).</summary>
    public static async Task<T[]> GetItemsAsync<T>(this HttpClient client, string url, CancellationToken ct = default) =>
        (await client.GetFromJsonAsync<PageDto<T>>(url, ct))!.Items;

    public static async Task<PageDto<T>> GetPageAsync<T>(this HttpClient client, string url, CancellationToken ct = default) =>
        (await client.GetFromJsonAsync<PageDto<T>>(url, ct))!;
}
