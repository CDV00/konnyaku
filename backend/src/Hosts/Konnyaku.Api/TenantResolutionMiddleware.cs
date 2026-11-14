using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Contracts;

namespace Konnyaku.Api;

/// <summary>
/// Resolves the tenant of a request:
/// 1. the host name (<c>X-Tenant-Host</c> forwarded by the web apps' server side, else the request Host);
/// 2. otherwise the <c>tid</c> claim of the token (API clients without a tenant host);
/// 3. in Development only, the <c>X-Tenant-Id</c> header.
/// A token issued for another tenant than the host's is rejected (401) — it never overrides the host.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public const string TenantHostHeader = "X-Tenant-Host";
    public const string DevTenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant, IPlatformApi platform)
    {
        var host = context.Request.Headers.TryGetValue(TenantHostHeader, out var forwarded) && !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.ToString()
            : context.Request.Host.Host;
        var hostTenant = await platform.ResolveHostAsync(host, context.RequestAborted);
        var tokenTenant = context.User.Identity?.IsAuthenticated == true ? context.User.TenantId() : null;

        if (hostTenant is not null && tokenTenant is not null && hostTenant.Id != tokenTenant)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if ((hostTenant?.Id ?? tokenTenant) is { } tenantId)
        {
            tenant.Set(tenantId);
        }
        else if (environment.IsDevelopment()
                 && context.Request.Headers.TryGetValue(DevTenantHeader, out var value)
                 && Guid.TryParse(value, out var devTenant))
        {
            tenant.Set(devTenant);
        }

        await next(context);
    }
}
