namespace Konnyaku.SharedKernel;

public abstract class Entity
{
    public Guid Id { get; protected init; } = Guid.CreateVersion7();
}

/// <summary>Marks an entity whose rows belong to exactly one tenant (filtered by EF and PostgreSQL RLS).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
