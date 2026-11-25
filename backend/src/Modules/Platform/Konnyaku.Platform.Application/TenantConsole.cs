using Konnyaku.Academic.Contracts;
using Konnyaku.Crm.Contracts;
using Konnyaku.Identity.Contracts;
using Konnyaku.Platform.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Platform.Application;

/// <summary>Runs work as a tenant (own DI scope, tenant context set, RLS applies) from a platform operation.</summary>
public interface ITenantScope
{
    Task<T> RunAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work);
}

public sealed record TenantUsage(int Students, int ActiveClasses, int Staff);

public sealed record TenantSummary(
    Guid Id, string Slug, string Name, string Status, string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone,
    DateTimeOffset CreatedAt, DateTimeOffset? SuspendedAt, string? SuspendedReason, string PrimaryHost, IReadOnlyList<string> Hosts,
    TenantUsage? Usage);

public sealed record ProvisionTenantCommand(
    string Slug, string Name, string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone, string AdminEmail, string AdminName);

/// <param name="TemporaryPassword">Shown once; null when the admin already had a staff password (they keep it).</param>
public sealed record TenantProvisioned(Guid TenantId, string Slug, string Host, string AdminEmail, string? TemporaryPassword);

public sealed record TenantAdminCommand(string Email, string DisplayName);

public sealed record TenantAdminAdded(string AdminEmail, string? TemporaryPassword);

public sealed record SuspendTenantCommand(string? Reason);

public sealed record TenantDomainCommand(string Host);

/// <summary>What platform operators do with centers: list, create with a first admin, suspend / resume, custom domains.</summary>
public sealed class TenantConsole(
    IPlatformDbContext db, CreateTenantHandler create, ITenantScope tenants, ITenantDirectoryCache cache, AuditLog audit,
    PlatformOptions options, TimeProvider clock)
{
    private const int UsageConcurrency = 4;

    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var all = await db.Tenants.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);
        var hosts = (await db.TenantDomains.AsNoTracking().ToListAsync(cancellationToken))
            .ToLookup(d => d.TenantId, d => d.Host);
        using var gate = new SemaphoreSlim(UsageConcurrency);
        var usage = await Task.WhenAll(all.Select(async t =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await UsageAsync(t.Id, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
        return all.Select((t, i) => ToSummary(t, hosts[t.Id].Order().ToList(), usage[i])).ToList();
    }

    public async Task<TenantSummary?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        return ToSummary(tenant, await HostsAsync(tenantId, cancellationToken), await UsageAsync(tenantId, cancellationToken));
    }

    /// <summary>
    /// Creates the center, then its first administrator. The two live in different modules (no shared transaction):
    /// if the administrator step fails, the center exists and an administrator can be added from its page.
    /// </summary>
    public async Task<Result<TenantProvisioned>> ProvisionAsync(ProvisionTenantCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        var adminEmail = PlatformOperator.NormalizeEmail(command.AdminEmail);
        if (!PlatformOperator.IsValidEmail(adminEmail))
        {
            return Error.Validation("platform.admin_email_invalid");
        }

        if (string.IsNullOrWhiteSpace(command.AdminName))
        {
            return Error.Validation("platform.admin_name_required");
        }

        var created = await create.HandleAsync(
            new CreateTenantCommand(command.Slug, command.Name, command.DefaultLocale, command.EnabledLocales ?? [], command.TimeZone),
            actor, cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var tenant = created.Value;
        var admin = await AddAdminAsync(tenant.Id, new TenantAdminCommand(adminEmail, command.AdminName), actor, cancellationToken);
        return admin.IsSuccess
            ? new TenantProvisioned(tenant.Id, tenant.Slug, tenant.Hosts[0], admin.Value.AdminEmail, admin.Value.TemporaryPassword)
            : admin.Error!;
    }

    public async Task<Result<TenantAdminAdded>> AddAdminAsync(Guid tenantId, TenantAdminCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            return Error.NotFound("platform.tenant_not_found");
        }

        var email = PlatformOperator.NormalizeEmail(command.Email);
        if (!PlatformOperator.IsValidEmail(email))
        {
            return Error.Validation("platform.admin_email_invalid");
        }

        var provisioned = await tenants.RunAsync(tenantId, sp =>
            sp.GetRequiredService<IIdentityPlatformApi>().ProvisionTenantAdminAsync(email, command.DisplayName, cancellationToken));
        if (!provisioned.IsSuccess)
        {
            return provisioned.Error!;
        }

        audit.Record(actor, AuditActions.TenantAdminCreate, tenantId, new Dictionary<string, string?>
        {
            ["email"] = provisioned.Value.Email,
            ["newAccount"] = provisioned.Value.TemporaryPassword is null ? "false" : "true",
        });
        await db.SaveChangesAsync(cancellationToken);
        return new TenantAdminAdded(provisioned.Value.Email, provisioned.Value.TemporaryPassword);
    }

    public async Task<Result<TenantSummary>> SuspendAsync(Guid tenantId, SuspendTenantCommand command, PlatformActor actor, CancellationToken cancellationToken) =>
        await ChangeStatusAsync(tenantId, actor, t => t.Suspend(command.Reason, clock.GetUtcNow()), AuditActions.TenantSuspend,
            new Dictionary<string, string?> { ["reason"] = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim() },
            cancellationToken);

    public async Task<Result<TenantSummary>> ResumeAsync(Guid tenantId, PlatformActor actor, CancellationToken cancellationToken) =>
        await ChangeStatusAsync(tenantId, actor, t => t.Resume(), AuditActions.TenantResume, null, cancellationToken);

    public async Task<Result<TenantSummary>> AddDomainAsync(Guid tenantId, TenantDomainCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound("platform.tenant_not_found");
        }

        var host = TenantDomain.NormalizeHost(command.Host ?? string.Empty);
        if (TenantDomains.CheckCustomHost(host, options) is { } error)
        {
            return error;
        }

        if (await db.TenantDomains.AnyAsync(d => d.Host == host, cancellationToken))
        {
            return Error.Conflict("platform.host_taken");
        }

        db.TenantDomains.Add(new TenantDomain(tenantId, host));
        audit.Record(actor, AuditActions.TenantDomainAdd, tenantId, new Dictionary<string, string?> { ["host"] = host });
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(tenantId, [host]);
        return ToSummary(tenant, await HostsAsync(tenantId, cancellationToken), null);
    }

    public async Task<Result<TenantSummary>> RemoveDomainAsync(Guid tenantId, TenantDomainCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound("platform.tenant_not_found");
        }

        var host = TenantDomain.NormalizeHost(command.Host ?? string.Empty);
        if (host == options.DefaultHost(tenant.Slug))
        {
            return Error.Validation("platform.domain_primary");
        }

        var domain = await db.TenantDomains.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Host == host, cancellationToken);
        if (domain is null)
        {
            return Error.NotFound("platform.domain_not_found");
        }

        db.TenantDomains.Remove(domain);
        audit.Record(actor, AuditActions.TenantDomainRemove, tenantId, new Dictionary<string, string?> { ["host"] = host });
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(tenantId, [host]);
        return ToSummary(tenant, await HostsAsync(tenantId, cancellationToken), null);
    }

    private async Task<Result<TenantSummary>> ChangeStatusAsync(
        Guid tenantId, PlatformActor actor, Func<Tenant, Result<bool>> change, string action,
        IReadOnlyDictionary<string, string?>? details, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound("platform.tenant_not_found");
        }

        var changed = change(tenant);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(actor, action, tenantId, details);
        await db.SaveChangesAsync(cancellationToken);
        var hosts = await HostsAsync(tenantId, cancellationToken);
        cache.Invalidate(tenantId, hosts);
        return ToSummary(tenant, hosts, null);
    }

    private async Task<TenantUsage?> UsageAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            return await tenants.RunAsync(tenantId, async sp => new TenantUsage(
                await sp.GetRequiredService<ICrmUsageApi>().CountStudentsAsync(cancellationToken),
                await sp.GetRequiredService<IAcademicUsageApi>().CountActiveClassesAsync(cancellationToken),
                await sp.GetRequiredService<IIdentityPlatformApi>().CountStaffAsync(cancellationToken)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Usage figures are informative: one module failing must not hide the list of centers.
            return null;
        }
    }

    private async Task<List<string>> HostsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.TenantDomains.AsNoTracking().Where(d => d.TenantId == tenantId).OrderBy(d => d.Host).Select(d => d.Host).ToListAsync(cancellationToken);

    private TenantSummary ToSummary(Tenant t, IReadOnlyList<string> hosts, TenantUsage? usage) => new(
        t.Id, t.Slug, t.Name, t.Status.ToString(), t.DefaultLocale, t.EnabledLocales, t.TimeZone, t.CreatedAt, t.SuspendedAt,
        t.SuspendedReason, options.DefaultHost(t.Slug), hosts, usage);
}
