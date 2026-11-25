using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Workspace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RoomPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "host_account_id",
                schema: "workspace",
                table: "host_lease",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "room_presence",
                schema: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_room_presence", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_room_presence_tenant_id_connection_id",
                schema: "workspace",
                table: "room_presence",
                columns: new[] { "tenant_id", "connection_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_room_presence_tenant_id_session_id",
                schema: "workspace",
                table: "room_presence",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.EnableTenantRls("workspace", "room_presence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("workspace", "room_presence");

            migrationBuilder.DropTable(
                name: "room_presence",
                schema: "workspace");

            migrationBuilder.AlterColumn<Guid>(
                name: "host_account_id",
                schema: "workspace",
                table: "host_lease",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
