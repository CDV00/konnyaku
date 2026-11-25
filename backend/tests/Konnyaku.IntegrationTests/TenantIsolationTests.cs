using System.Collections.Concurrent;
using Konnyaku.Application.Messaging;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Organization.Application;
using Konnyaku.Organization.Contracts;
using Konnyaku.Organization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> Name = new() { ["vi"] = "Cơ sở", ["en"] = "Branch" };

    [Fact]
    public async Task Tenant_sees_only_its_own_branches()
    {
        using var host = fixture.BuildHost();
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());

        await CreateBranchAsync(host, tenantA, "A1");
        await CreateBranchAsync(host, tenantB, "B1");
        await CreateBranchAsync(host, tenantB, "B2");

        (await ListCodesAsync(host, tenantA)).ShouldBe(["A1"]);
        (await ListCodesAsync(host, tenantB)).ShouldBe(["B1", "B2"]);
    }

    [Fact]
    public async Task Same_code_is_allowed_in_different_tenants_but_not_twice_in_one()
    {
        using var host = fixture.BuildHost();
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());

        (await CreateBranchAsync(host, tenantA, "HQ")).IsSuccess.ShouldBeTrue();
        (await CreateBranchAsync(host, tenantB, "HQ")).IsSuccess.ShouldBeTrue();
        (await CreateBranchAsync(host, tenantA, "HQ")).Error!.Code.ShouldBe("organization.branch_code_taken");
    }

    [Fact]
    public async Task Rls_hides_rows_even_when_the_ef_filter_is_bypassed()
    {
        using var host = fixture.BuildHost();
        var tenantA = Guid.NewGuid();
        await CreateBranchAsync(host, tenantA, "RLS1");

        (await CountIgnoringFiltersAsync(host, Guid.NewGuid())).ShouldBe(0);
        (await CountIgnoringFiltersAsync(host, tenant: null)).ShouldBe(0);
        (await CountIgnoringFiltersAsync(host, tenantA)).ShouldBe(1);
    }

    [Fact]
    public async Task Rls_rejects_writing_a_row_for_another_tenant()
    {
        using var host = fixture.BuildHost();
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(Guid.NewGuid());
        var db = scope.ServiceProvider.GetRequiredService<OrganizationDbContext>();

        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO org.branch (id, tenant_id, code, name, time_zone, is_virtual, created_at) " +
            "VALUES ({0}, {1}, 'X1', '{{\"vi\":\"x\"}}', 'UTC', false, now())",
            [Guid.NewGuid(), Guid.NewGuid()], Ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Every_table_with_tenant_id_has_forced_rls_and_a_policy()
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT c.table_schema || '.' || c.table_name
            FROM information_schema.columns c
            JOIN pg_namespace n ON n.nspname = c.table_schema
            JOIN pg_class k ON k.relname = c.table_name AND k.relnamespace = n.oid AND k.relkind = 'r'
            WHERE c.column_name = 'tenant_id'
              AND c.table_name NOT IN ('outbox_message', 'inbox_message')
              AND c.table_schema || '.' || c.table_name <> 'notification.dispatch_item'
              AND c.table_schema <> 'platform'
              AND (NOT k.relrowsecurity OR NOT k.relforcerowsecurity
                   OR NOT EXISTS (SELECT 1 FROM pg_policies p
                                  WHERE p.schemaname = c.table_schema AND p.tablename = c.table_name))
            """, connection);
        var unprotected = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            unprotected.Add(reader.GetString(0));
        }

        unprotected.ShouldBeEmpty();
    }

    [Fact]
    public async Task Module_role_cannot_read_another_schema()
    {
        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await new NpgsqlCommand(
                "CREATE SCHEMA IF NOT EXISTS probe; CREATE TABLE IF NOT EXISTS probe.secret (id int);", owner).ExecuteNonQueryAsync(Ct);
        }

        await using var module = new NpgsqlConnection(fixture.ModuleConnectionString("organization"));
        await module.OpenAsync(Ct);
        var error = await Should.ThrowAsync<PostgresException>(() =>
            new NpgsqlCommand("SELECT count(*) FROM probe.secret", module).ExecuteScalarAsync(Ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Outbox_delivers_event_once_with_the_publishing_tenant_as_context()
    {
        var received = new ConcurrentBag<(BranchCreatedV1 Event, Guid? Tenant)>();
        using var host = fixture.BuildHost(configure: services =>
        {
            services.AddSingleton(received);
            services.AddScoped<IIntegrationEventHandler<BranchCreatedV1>, RecordingHandler>();
            services.AddSingleton<OutboxProcessor<OrganizationDbContext>>();
        });
        var tenant = Guid.NewGuid();
        var branch = (await CreateBranchAsync(host, tenant, "EVT")).Value;

        var processor = host.Services.GetRequiredService<OutboxProcessor<OrganizationDbContext>>();
        while (await processor.ProcessBatchAsync(100, Ct) > 0)
        {
        }

        var mine = received.Where(r => r.Event.BranchId == branch.Id).ToList();
        mine.Count.ShouldBe(1);
        mine[0].Tenant.ShouldBe(tenant);
    }

    private static async Task<Konnyaku.SharedKernel.Result<BranchDto>> CreateBranchAsync(IHost host, Guid tenant, string code)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        return await scope.ServiceProvider.GetRequiredService<CreateBranchHandler>()
            .HandleAsync(new CreateBranchCommand(code, Name, "Asia/Ho_Chi_Minh", false), Ct);
    }

    private static async Task<IReadOnlyList<string>> ListCodesAsync(IHost host, Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        var branches = await scope.ServiceProvider.GetRequiredService<ListBranchesHandler>().HandleAsync(Ct);
        return branches.Select(b => b.Code).ToList();
    }

    private static async Task<int> CountIgnoringFiltersAsync(IHost host, Guid? tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        if (tenant is { } id)
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(id);
        }

        return await scope.ServiceProvider.GetRequiredService<OrganizationDbContext>().Branches.IgnoreQueryFilters().CountAsync(Ct);
    }

    private sealed class RecordingHandler(ConcurrentBag<(BranchCreatedV1, Guid?)> received, ITenantContext tenant)
        : IIntegrationEventHandler<BranchCreatedV1>
    {
        public Task HandleAsync(BranchCreatedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
        {
            received.Add((@event, tenant.TenantId));
            return Task.CompletedTask;
        }
    }
}
