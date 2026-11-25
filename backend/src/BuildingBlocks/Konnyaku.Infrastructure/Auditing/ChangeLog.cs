using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Konnyaku.Infrastructure.Auditing;

/// <summary>One field of one change: values are text (dates ISO, names as JSON) so any record type fits.</summary>
public sealed record FieldChange(string Field, string? Old, string? New);

public static class ChangeActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
}

/// <summary>
/// Who changed what and when, written in the same transaction as the change itself (a module's own table, protected by
/// RLS like any tenant data). Entries are only added, never edited.
/// </summary>
public sealed class ChangeLogEntry : ITenantOwned
{
    private ChangeLogEntry()
    {
    }

    public ChangeLogEntry(string entityType, Guid entityId, Guid subjectId, string action, IReadOnlyList<FieldChange> changes)
    {
        Id = Guid.CreateVersion7();
        EntityType = entityType;
        EntityId = entityId;
        SubjectId = subjectId;
        Action = action;
        Changes = changes.ToList();
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string EntityType { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }

    /// <summary>The record whose history page shows this entry (usually <see cref="EntityId"/>).</summary>
    public Guid SubjectId { get; private set; }

    public string Action { get; private set; } = string.Empty;
    public Guid? ActorId { get; private set; }
    public string? ActorName { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public List<FieldChange> Changes { get; private set; } = [];

    internal void Stamp(Guid? actorId, string? actorName, DateTimeOffset at)
    {
        ActorId ??= actorId;
        ActorName ??= actorName;
        if (OccurredAt == default)
        {
            OccurredAt = at;
        }
    }
}

internal sealed class ChangeLogEntryConfiguration : IEntityTypeConfiguration<ChangeLogEntry>
{
    public void Configure(EntityTypeBuilder<ChangeLogEntry> builder)
    {
        builder.ToTable("change_log");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EntityType).HasMaxLength(60);
        builder.Property(x => x.Action).HasMaxLength(20);
        builder.Property(x => x.ActorName).HasMaxLength(200);
        builder.Property(x => x.Changes).HasJsonConversion();
        builder.HasIndex(x => new { x.TenantId, x.SubjectId, x.OccurredAt });
        builder.HasIndex(x => new { x.TenantId, x.EntityId });
    }
}

public static class ChangeLogModelExtensions
{
    /// <summary>Adds the module's <c>change_log</c> table; the migration must also call <c>EnableTenantRls</c> on it.</summary>
    public static ModelBuilder AddChangeLog(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ChangeLogEntryConfiguration());
        return modelBuilder;
    }
}

/// <summary>The person behind the current request (null in background jobs and event handlers: "system").</summary>
public interface ICurrentActor
{
    Guid? UserId { get; }

    string? DisplayName { get; }
}

internal sealed class SystemActor : ICurrentActor
{
    public static readonly SystemActor Instance = new();

    public Guid? UserId => null;

    public string? DisplayName => null;
}

internal sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public Guid? UserId => accessor.HttpContext?.User.UserId();

    public string? DisplayName => accessor.HttpContext?.User.FindFirstValue(KonnyakuClaims.Name);
}

/// <summary>Turns tracked changes of <see cref="IAuditable"/> entities into change log entries before every save.</summary>
internal sealed class AuditInterceptor(ICurrentActor actor, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbContext? context)
    {
        if (context?.Model.FindEntityType(typeof(ChangeLogEntry)) is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var log = context.Set<ChangeLogEntry>();
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity is IAuditable).ToList())
        {
            var auditable = (IAuditable)entry.Entity;
            var id = entry.Metadata.FindPrimaryKey()?.Properties is [{ ClrType: var keyType } key] && keyType == typeof(Guid)
                ? (Guid)entry.Property(key.Name).CurrentValue!
                : auditable.AuditSubjectId;
            switch (entry.State)
            {
                case EntityState.Added:
                    log.Add(new ChangeLogEntry(entry.Metadata.ClrType.Name, id, auditable.AuditSubjectId, ChangeActions.Created, []));
                    break;
                case EntityState.Deleted:
                    log.Add(new ChangeLogEntry(entry.Metadata.ClrType.Name, id, auditable.AuditSubjectId, ChangeActions.Deleted, []));
                    break;
                case EntityState.Modified:
                    var changes = Diff(entry);
                    if (changes.Count > 0)
                    {
                        log.Add(new ChangeLogEntry(entry.Metadata.ClrType.Name, id, auditable.AuditSubjectId, ChangeActions.Updated, changes));
                    }

                    break;
            }
        }

        var now = clock.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<ChangeLogEntry>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.Stamp(actor.UserId, actor.DisplayName, now);
        }

        // The entries are added after the context assigned tenants to new rows: assign theirs too.
        (context as ModuleDbContext)?.ApplyTenantRules();
    }

    private static List<FieldChange> Diff(EntityEntry entry)
    {
        var changes = new List<FieldChange>();
        foreach (var property in entry.Properties)
        {
            if (!property.IsModified || property.Metadata.IsPrimaryKey() || property.Metadata.IsConcurrencyToken ||
                property.Metadata.Name is nameof(ITenantOwned.TenantId) or "CreatedAt" or "UpdatedAt")
            {
                continue;
            }

            // Personal data is logged masked: the history shows that a phone number changed, not the number.
            var before = Format(property.OriginalValue);
            var after = Format(property.CurrentValue);
            if (before == after)
            {
                continue;
            }

            if (property.Metadata.PropertyInfo?.GetCustomAttribute<PersonalDataAttribute>() is { } personal)
            {
                before = PersonalDataMask.Mask(personal.Kind, property.OriginalValue);
                after = PersonalDataMask.Mask(personal.Kind, property.CurrentValue);
            }

            changes.Add(new FieldChange(JsonNamingPolicy.CamelCase.ConvertName(property.Metadata.Name), before, after));
        }

        return changes;
    }

    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        LocalizedString l => JsonSerializer.Serialize(l.Values),
        decimal m => m.ToString("0.############################", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}

public sealed record ChangeLogView(
    Guid Id, string EntityType, Guid EntityId, string Action, Guid? ActorId, string? ActorName, DateTimeOffset OccurredAt,
    IReadOnlyList<FieldChange> Changes);

public static class ChangeHistoryEndpoints
{
    /// <summary>
    /// <c>GET /history/{id}</c>: the change log of a record and of everything logged under it, newest first.
    /// Any staff member may read it (teachers included); writing is checked where the change happens.
    /// </summary>
    public static RouteHandlerBuilder MapChangeHistory<TContext>(this RouteGroupBuilder group) where TContext : ModuleDbContext
    {
        // A delegate variable (not a lambda) keeps the route analyzer away from the generic context type.
        Delegate handler = new Func<Guid, TContext, CancellationToken, Task<IResult>>(HistoryAsync);
        return group.MapGet("/history/{id:guid}", handler).RequireAuthorization(Policies.Staff);
    }

    private static async Task<IResult> HistoryAsync<TContext>(Guid id, TContext db, CancellationToken ct) where TContext : ModuleDbContext =>
        Results.Ok(await db.Set<ChangeLogEntry>().AsNoTracking()
            .Where(e => e.SubjectId == id || e.EntityId == id)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(200)
            .Select(e => new ChangeLogView(e.Id, e.EntityType, e.EntityId, e.Action, e.ActorId, e.ActorName, e.OccurredAt, e.Changes))
            .ToListAsync(ct));
}
