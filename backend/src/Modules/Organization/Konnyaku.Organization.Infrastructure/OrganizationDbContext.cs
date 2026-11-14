using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Organization.Application;
using Konnyaku.Organization.Contracts;
using Konnyaku.Organization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Konnyaku.Organization.Infrastructure;

public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IOrganizationDbContext
{
    public const string SchemaName = "org";

    public override string Schema => SchemaName;

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<Room> Rooms => Set<Room>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Branch>(b =>
        {
            b.ToTable("branch");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(20);
            b.Property(x => x.Name)
                .HasConversion(new LocalizedStringConverter(), LocalizedStringConverter.Comparer)
                .HasColumnType("jsonb");
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        modelBuilder.Entity<Holiday>(b =>
        {
            b.ToTable("holiday");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name)
                .HasConversion(new LocalizedStringConverter(), LocalizedStringConverter.Comparer)
                .HasColumnType("jsonb");
            b.HasIndex(x => new { x.TenantId, x.Date });
        });
        modelBuilder.Entity<Room>(b =>
        {
            b.ToTable("room");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.BranchId, x.Code }).IsUnique();
        });
    }
}

internal sealed class InProcessOrganizationApi(OrganizationDbContext db) : IOrganizationApi
{
    public async Task<BranchDto?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        return branch?.ToDto();
    }

    public async Task<RoomDto?> GetRoomAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == roomId, cancellationToken);
        return room is null ? null : new RoomDto(room.Id, room.BranchId, room.Code, room.Capacity);
    }

    public async Task<IReadOnlySet<DateOnly>> ListHolidaysAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var dates = await db.Holidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to && (h.BranchId == null || h.BranchId == branchId))
            .Select(h => h.Date).ToListAsync(cancellationToken);
        return dates.ToHashSet();
    }
}

internal sealed class OrganizationDesignTimeFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrganizationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", OrganizationDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new OrganizationDbContext(options, new TenantContext());
    }
}
