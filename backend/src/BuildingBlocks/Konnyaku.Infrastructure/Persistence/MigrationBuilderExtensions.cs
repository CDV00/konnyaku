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

    /// <summary>
    /// <c>&lt;schema&gt;.redact_change_log(subject)</c>: blanks the old and new values of every change log entry of one
    /// record of the current tenant, for an erasure request. The table stays append-only for the module role; this
    /// function (owned by the schema owner) is the only way to touch past entries, and it can only remove values.
    /// </summary>
    public static void CreateChangeLogRedaction(this MigrationBuilder migrationBuilder, string schema, string role) =>
        migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION "{{schema}}".redact_change_log(subject uuid) RETURNS integer
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                redacted integer;
            BEGIN
                UPDATE "{{schema}}".change_log
                   SET changes = COALESCE((SELECT jsonb_agg(jsonb_build_object('Field', c->'Field', 'Old', NULL, 'New', NULL))
                                             FROM jsonb_array_elements(changes) c), '[]'::jsonb)
                 WHERE (subject_id = subject OR entity_id = subject)
                   AND tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid;
                GET DIAGNOSTICS redacted = ROW_COUNT;
                RETURN redacted;
            END $fn$;
            REVOKE ALL ON FUNCTION "{{schema}}".redact_change_log(uuid) FROM PUBLIC;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{role}}') THEN
                    GRANT EXECUTE ON FUNCTION "{{schema}}".redact_change_log(uuid) TO {{role}};
                END IF;
            END $$;
            """);

    /// <summary>One-off clean-up: blanks the values logged for <paramref name="fields"/> before they were logged masked.</summary>
    public static void RedactLoggedFields(this MigrationBuilder migrationBuilder, string schema, params string[] fields) =>
        migrationBuilder.Sql($"""
            UPDATE "{schema}".change_log
               SET changes = (SELECT jsonb_agg(CASE WHEN c->>'Field' IN ({string.Join(", ", fields.Select(f => $"'{f}'"))})
                                                    THEN jsonb_build_object('Field', c->'Field', 'Old', NULL, 'New', NULL) ELSE c END)
                                FROM jsonb_array_elements(changes) c)
             WHERE jsonb_array_length(changes) > 0;
            """);

    /// <summary>
    /// <c>public.kk_unaccent(text)</c>: <c>unaccent</c> declared IMMUTABLE so generated search columns and indexes may use
    /// it ("Ân" → "An"). Idempotent; the init scripts create it too.
    /// </summary>
    public static void EnsureUnaccent(this MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE EXTENSION IF NOT EXISTS unaccent SCHEMA public;
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE OR REPLACE FUNCTION public.kk_unaccent(text) RETURNS text
            LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
            AS $fn$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $fn$;
            """);
}
