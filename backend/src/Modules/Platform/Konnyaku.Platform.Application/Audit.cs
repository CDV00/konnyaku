using Konnyaku.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Platform.Application;

/// <summary>Who performs a platform operation: a signed-in operator, or a script holding the automation key.</summary>
public sealed record PlatformActor(Guid? OperatorId, string Name, string? IpAddress)
{
    public static PlatformActor Automation(string? ipAddress) => new(null, PlatformAuditEntry.AutomationActor, ipAddress);
}

/// <summary>Adds audit entries to the current unit of work: they are saved in the same transaction as the change.</summary>
public sealed class AuditLog(IPlatformDbContext db, TimeProvider clock)
{
    public void Record(PlatformActor actor, string action, Guid? tenantId, IReadOnlyDictionary<string, string?>? details = null) =>
        db.AuditEntries.Add(new PlatformAuditEntry(clock.GetUtcNow(), actor.OperatorId, actor.Name, action, tenantId, details, actor.IpAddress));
}

public sealed record AuditEntryView(
    Guid Id, DateTimeOffset At, string Actor, string Action, Guid? TenantId, string? TenantName,
    IReadOnlyDictionary<string, string?> Details, string? IpAddress);

public sealed class AuditQueries(IPlatformDbContext db)
{
    public const int MaxTake = 500;

    /// <summary>Newest first; optionally for one tenant or before a point in time (paging).</summary>
    public async Task<IReadOnlyList<AuditEntryView>> ListAsync(Guid? tenantId, DateTimeOffset? before, int take, CancellationToken cancellationToken)
    {
        var query = db.AuditEntries.AsNoTracking();
        if (tenantId is { } id)
        {
            query = query.Where(e => e.TenantId == id);
        }

        if (before is { } cursor)
        {
            query = query.Where(e => e.At < cursor);
        }

        var entries = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Take(Math.Clamp(take, 1, MaxTake)).ToListAsync(cancellationToken);
        var tenantIds = entries.Where(e => e.TenantId is not null).Select(e => e.TenantId!.Value).Distinct().ToList();
        var names = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        return entries
            .Select(e => new AuditEntryView(
                e.Id, e.At, e.Actor, e.Action, e.TenantId,
                e.TenantId is { } t && names.TryGetValue(t, out var name) ? name : null,
                e.Details, e.IpAddress))
            .ToList();
    }
}
