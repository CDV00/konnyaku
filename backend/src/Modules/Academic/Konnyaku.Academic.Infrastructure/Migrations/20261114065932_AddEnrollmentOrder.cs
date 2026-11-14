using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Academic.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrollmentOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                schema: "academic",
                table: "enrollment",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "order_id",
                schema: "academic",
                table: "enrollment");
        }
    }
}
