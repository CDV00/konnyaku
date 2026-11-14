using Microsoft.EntityFrameworkCore.Migrations;

namespace Konnyaku.Infrastructure.Persistence;

public static class MigrationBuilderExtensions
{
    private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

    /// <summary>Enables and forces Row-Level Security with the standard tenant policy on a table.</summary>
    public static void EnableTenantRls(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        var name = $"\"{schema}\".\"{table}\"";
        migrationBuilder.Sql($"ALTER TABLE {name} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {name} FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {name} USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
    }

    public static void DisableTenantRls(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        var name = $"\"{schema}\".\"{table}\"";
        migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {name};");
        migrationBuilder.Sql($"ALTER TABLE {name} NO FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {name} DISABLE ROW LEVEL SECURITY;");
    }
}
