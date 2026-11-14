using System;
using System.Collections.Generic;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Workspace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workspace");

            migrationBuilder.CreateTable(
                name: "host_account",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_host_account", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "host_lease",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    space_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    meeting_uri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_host_lease", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "workspace",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_message", x => new { x.event_id, x.consumer });
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recording",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    viewer_user_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recording", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_connection",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_connection", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_host_account_tenant_id_email",
                schema: "workspace",
                table: "host_account",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_host_lease_tenant_id_host_account_id_starts_at",
                schema: "workspace",
                table: "host_lease",
                columns: new[] { "tenant_id", "host_account_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_host_lease_tenant_id_session_id",
                schema: "workspace",
                table: "host_lease",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "workspace",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_recording_tenant_id_session_id",
                schema: "workspace",
                table: "recording",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_connection_tenant_id",
                schema: "workspace",
                table: "workspace_connection",
                column: "tenant_id",
                unique: true);

            // A host account can never be leased to two overlapping sessions — enforced by PostgreSQL.
            migrationBuilder.Sql("""
                ALTER TABLE workspace.host_lease
                    ADD COLUMN during tstzrange GENERATED ALWAYS AS (tstzrange(starts_at, ends_at, '[)')) STORED;
                ALTER TABLE workspace.host_lease
                    ADD CONSTRAINT ex_host_lease_no_overlap EXCLUDE USING gist (
                        host_account_id WITH =, during WITH &&) WHERE (status IN ('Reserved', 'Provisioned'));
                """);

            migrationBuilder.EnableTenantRls("workspace", "workspace_connection");
            migrationBuilder.EnableTenantRls("workspace", "host_account");
            migrationBuilder.EnableTenantRls("workspace", "host_lease");
            migrationBuilder.EnableTenantRls("workspace", "recording");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("workspace", "workspace_connection");
            migrationBuilder.DisableTenantRls("workspace", "host_account");
            migrationBuilder.DisableTenantRls("workspace", "host_lease");
            migrationBuilder.DisableTenantRls("workspace", "recording");

            migrationBuilder.DropTable(
                name: "host_account",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "host_lease",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "recording",
                schema: "workspace");

            migrationBuilder.DropTable(
                name: "workspace_connection",
                schema: "workspace");
        }
    }
}
