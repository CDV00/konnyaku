using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CaptchaRedemption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "captcha_redemption",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_captcha_redemption", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_captcha_redemption_tenant_id_challenge",
                schema: "crm",
                table: "captcha_redemption",
                columns: new[] { "tenant_id", "challenge" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_captcha_redemption_tenant_id_expires_at",
                schema: "crm",
                table: "captcha_redemption",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.EnableTenantRls("crm", "captcha_redemption");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("crm", "captcha_redemption");

            migrationBuilder.DropTable(
                name: "captcha_redemption",
                schema: "crm");
        }
    }
}
