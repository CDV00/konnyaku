using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Organization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EditAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "org",
                table: "room",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "org",
                table: "branch",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "change_log",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_log_tenant_id_entity_id",
                schema: "org",
                table: "change_log",
                columns: new[] { "tenant_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_log_tenant_id_subject_id_occurred_at",
                schema: "org",
                table: "change_log",
                columns: new[] { "tenant_id", "subject_id", "occurred_at" });

            // Existing rows stay active; change history is tenant data like any other.
            migrationBuilder.EnableTenantRls("org", "change_log");
            migrationBuilder.MakeAppendOnly("org", "change_log", "kk_organization");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("org", "change_log");

            migrationBuilder.DropTable(
                name: "change_log",
                schema: "org");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "org",
                table: "room");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "org",
                table: "branch");
        }
    }
}
