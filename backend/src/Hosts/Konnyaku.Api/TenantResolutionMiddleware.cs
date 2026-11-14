using Konnyaku.Infrastructure.Tenancy;

namespace Konnyaku.Api;

/// <summary>
/// Phase 0: tenant comes from the <c>X-Tenant-Id</c> header, accepted only in Development.
/// Phase 1 replaces this with host lookup + token check (a token for another tenant is rejected).
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public const string HeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (environment.IsDevelopment()
            && context.Request.Headers.TryGetValue(HeaderName, out var value)
            && Guid.TryParse(value, out var tenantId))
        {
            tenant.Set(tenantId);
        }

        await next(context);
    }
}
