using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Contracts;

namespace Konnyaku.Api;

/// <summary>
/// Resolves the tenant of a request:
/// 1. the host name (<c>X-Tenant-Host</c> forwarded by the web apps' server side, else the request Host);
/// 2. otherwise the <c>tid</c> claim of the token (API clients without a tenant host);
/// 3. in Development only, the <c>X-Tenant-Id</c> header.
/// A token issued for another tenant than the host's is rejected (401) — it never overrides the host.
/// A suspended tenant gets 403 <c>platform.tenant_suspended</c> on every call (sign-in included), except the public
/// tenant info the site uses to show the suspension notice.
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
            var info = hostTenant ?? await platform.GetTenantAsync(tenantId, context.RequestAborted);
            if (info?.Status == TenantStatuses.Suspended && !IsPublicTenantInfo(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new ErrorBody("platform.tenant_suspended", new Dictionary<string, object?>()), context.RequestAborted);
                return;
            }

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

    private static bool IsPublicTenantInfo(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && request.Path.Equals("/api/platform/tenant", StringComparison.OrdinalIgnoreCase);
}
