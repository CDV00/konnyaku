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

    /// <summary>The module role may insert and read rows of the table, never update or delete them (audit trails, ledgers).</summary>
    public static void MakeAppendOnly(this MigrationBuilder migrationBuilder, string schema, string table, string role) =>
        migrationBuilder.Sql($"""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN
                    REVOKE UPDATE, DELETE ON "{schema}"."{table}" FROM {role};
                END IF;
            END $$;
            """);
}
