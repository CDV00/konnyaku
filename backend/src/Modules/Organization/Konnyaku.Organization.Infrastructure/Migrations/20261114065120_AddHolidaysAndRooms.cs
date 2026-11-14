using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Organization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHolidaysAndRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holiday",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holiday", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "room",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_room", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_holiday_tenant_id_date",
                schema: "org",
                table: "holiday",
                columns: new[] { "tenant_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_room_tenant_id_branch_id_code",
                schema: "org",
                table: "room",
                columns: new[] { "tenant_id", "branch_id", "code" },
                unique: true);

            migrationBuilder.EnableTenantRls("org", "holiday");
            migrationBuilder.EnableTenantRls("org", "room");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("org", "holiday");
            migrationBuilder.DisableTenantRls("org", "room");

            migrationBuilder.DropTable(
                name: "holiday",
                schema: "org");

            migrationBuilder.DropTable(
                name: "room",
                schema: "org");
        }
    }
}
