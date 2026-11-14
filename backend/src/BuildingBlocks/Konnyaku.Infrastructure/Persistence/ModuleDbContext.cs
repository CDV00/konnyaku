using System.Reflection;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Infrastructure.Persistence;

/// <summary>
/// Base DbContext of a module: one schema per module, tenant query filter on every <see cref="ITenantOwned"/>
/// entity, and the module's own outbox/inbox tables.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, ITenantContext tenant) : DbContext(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(ModuleDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected Guid? CurrentTenantId => tenant.TenantId;

    /// <summary>Queues an integration event in this module's outbox; it is saved in the same transaction.</summary>
    public void Publish(IIntegrationEvent integrationEvent, string? correlationId = null) =>
        OutboxMessages.Add(OutboxMessage.Create(integrationEvent, tenant.RequiredTenantId, correlationId));

    /// <summary>For platform-level modules that act on behalf of a tenant without being in its context.</summary>
    public void PublishFor(Guid tenantId, IIntegrationEvent integrationEvent, string? correlationId = null) =>
        OutboxMessages.Add(OutboxMessage.Create(integrationEvent, tenantId, correlationId));

    /// <summary>Records that <paramref name="consumer"/> handled <paramref name="eventId"/>; false if it already did.</summary>
    public async Task<bool> TryAcceptAsync(Guid eventId, string consumer, CancellationToken cancellationToken)
    {
        if (await InboxMessages.AnyAsync(m => m.EventId == eventId && m.Consumer == consumer, cancellationToken))
        {
            return false;
        }

        InboxMessages.Add(new InboxMessage(eventId, consumer, DateTimeOffset.UtcNow));
        return true;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        ConfigureModel(modelBuilder);

        var tenantOwned = modelBuilder.Model.GetEntityTypes()
            .Where(t => t.BaseType is null && typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .Select(t => t.ClrType)
            .ToList();
        foreach (var clrType in tenantOwned)
        {
            ApplyTenantFilterMethod.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }

    private void ApplyTenantRules()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            var property = entry.Property(e => e.TenantId);
            switch (entry.State)
            {
                case EntityState.Added when property.CurrentValue == Guid.Empty:
                    property.CurrentValue = tenant.RequiredTenantId;
                    break;
                case EntityState.Added when property.CurrentValue != tenant.RequiredTenantId:
                    throw new InvalidOperationException("Cannot insert a row for another tenant.");
                case EntityState.Modified when property.IsModified:
                    throw new InvalidOperationException("TenantId of an existing row cannot change.");
            }
        }
    }
}
