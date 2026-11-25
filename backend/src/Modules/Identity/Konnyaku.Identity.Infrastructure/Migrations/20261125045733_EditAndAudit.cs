using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EditAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_log",
                schema: "identity",
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
                schema: "identity",
                table: "change_log",
                columns: new[] { "tenant_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_log_tenant_id_subject_id_occurred_at",
                schema: "identity",
                table: "change_log",
                columns: new[] { "tenant_id", "subject_id", "occurred_at" });

            // Change history is tenant data like any other, and is only ever appended to.
            migrationBuilder.EnableTenantRls("identity", "change_log");
            migrationBuilder.MakeAppendOnly("identity", "change_log", "kk_identity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("identity", "change_log");

            migrationBuilder.DropTable(
                name: "change_log",
                schema: "identity");
        }
    }
}
