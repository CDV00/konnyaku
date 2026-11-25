using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Academic.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BookingModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "preferred_mode",
                schema: "academic",
                table: "enrollment",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "booking_mode",
                schema: "academic",
                table: "class",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Fixed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "preferred_mode",
                schema: "academic",
                table: "enrollment");

            migrationBuilder.DropColumn(
                name: "booking_mode",
                schema: "academic",
                table: "class");
        }
    }
}
