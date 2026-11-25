using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlatformConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "platform",
                table: "tenant",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_at",
                schema: "platform",
                table: "tenant",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspended_reason",
                schema: "platform",
                table: "tenant",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_entry",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entry", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operator",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    totp_secret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    pending_totp_secret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_totp_step = table.Column<long>(type: "bigint", nullable: true),
                    recovery_code_hashes = table.Column<List<string>>(type: "text[]", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sign_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operator", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entry_at",
                schema: "platform",
                table: "audit_entry",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entry_tenant_id_at",
                schema: "platform",
                table: "audit_entry",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_operator_email",
                schema: "platform",
                table: "operator",
                column: "email",
                unique: true);

            // Audit is append-only: the module role may insert and read, never rewrite history.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON platform.audit_entry FROM kk_platform;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entry",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "operator",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "platform",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                schema: "platform",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "suspended_reason",
                schema: "platform",
                table: "tenant");
        }
    }
}
