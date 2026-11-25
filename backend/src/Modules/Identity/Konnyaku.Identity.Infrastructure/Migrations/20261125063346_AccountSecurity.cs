using System;
using System.Collections.Generic;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AccountSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "last_totp_step",
                schema: "identity",
                table: "password_credential",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                schema: "identity",
                table: "password_credential",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_changed_at",
                schema: "identity",
                table: "password_credential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_totp_secret",
                schema: "identity",
                table: "password_credential",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "recovery_code_hashes",
                schema: "identity",
                table: "password_credential",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                schema: "identity",
                table: "password_credential",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "totp_secret",
                schema: "identity",
                table: "password_credential",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "two_factor_enabled_at",
                schema: "identity",
                table: "password_credential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_changed_at",
                schema: "identity",
                table: "local_credential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                schema: "identity",
                table: "local_credential",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "password_reset_token",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_from_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_token", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "security_event",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_session",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audience = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    credential_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_session", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_tenant_id_user_id",
                schema: "identity",
                table: "password_reset_token",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_token_hash",
                schema: "identity",
                table: "password_reset_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_event_tenant_id_occurred_at",
                schema: "identity",
                table: "security_event",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_security_event_tenant_id_user_id",
                schema: "identity",
                table: "security_event",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_session_tenant_id_user_id",
                schema: "identity",
                table: "user_session",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.EnableTenantRls("identity", "user_session");
            migrationBuilder.EnableTenantRls("identity", "password_reset_token");
            migrationBuilder.EnableTenantRls("identity", "security_event");
            migrationBuilder.MakeAppendOnly("identity", "security_event", "kk_identity");

            // Existing credentials get their own stamp (new rows get one from the application).
            migrationBuilder.Sql("UPDATE identity.password_credential SET security_stamp = gen_random_uuid();");
            migrationBuilder.Sql("UPDATE identity.local_credential SET security_stamp = gen_random_uuid();");

            // Memberships of other centers are hidden by RLS; an administrator resetting a platform-wide staff password or
            // 2FA must know whether the person works for another center too. Runs as the migration owner; answers NULL
            // (treated as "shared", so the reset is refused) when that owner cannot bypass RLS.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION identity.member_tenant_count(p_user_id uuid) RETURNS integer
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = identity, pg_catalog, pg_temp
                AS $$
                    SELECT CASE WHEN (SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user)
                        THEN (SELECT count(DISTINCT m.tenant_id)::integer FROM identity.membership m WHERE m.user_id = p_user_id)
                        ELSE NULL END
                $$;
                REVOKE ALL ON FUNCTION identity.member_tenant_count(uuid) FROM PUBLIC;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kk_identity') THEN
                        GRANT EXECUTE ON FUNCTION identity.member_tenant_count(uuid) TO kk_identity;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.member_tenant_count(uuid);");

            migrationBuilder.DropTable(
                name: "password_reset_token",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "security_event",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_session",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "last_totp_step",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "must_change_password",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "password_changed_at",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "pending_totp_secret",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "recovery_code_hashes",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "totp_secret",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "two_factor_enabled_at",
                schema: "identity",
                table: "password_credential");

            migrationBuilder.DropColumn(
                name: "password_changed_at",
                schema: "identity",
                table: "local_credential");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "identity",
                table: "local_credential");
        }
    }
}
