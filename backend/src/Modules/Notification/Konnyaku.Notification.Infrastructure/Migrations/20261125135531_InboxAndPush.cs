using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Notification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InboxAndPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "inbox_seq",
                schema: "notification");

            migrationBuilder.AddColumn<string>(
                name: "digest_key",
                schema: "notification",
                table: "notification_message",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inbox_item",
                schema: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false),
                    link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    deduplication_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('notification.inbox_seq')"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preference",
                schema: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    email = table.Column<bool>(type: "boolean", nullable: false),
                    push = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preference", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "push_subscription",
                schema: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    p256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_subscription", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_message_tenant_id_digest_key_status",
                schema: "notification",
                table: "notification_message",
                columns: new[] { "tenant_id", "digest_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_item_tenant_id_deduplication_key",
                schema: "notification",
                table: "inbox_item",
                columns: new[] { "tenant_id", "deduplication_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_item_tenant_id_user_id_read_at",
                schema: "notification",
                table: "inbox_item",
                columns: new[] { "tenant_id", "user_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_item_tenant_id_user_id_seq",
                schema: "notification",
                table: "inbox_item",
                columns: new[] { "tenant_id", "user_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_preference_tenant_id_user_id_kind",
                schema: "notification",
                table: "notification_preference",
                columns: new[] { "tenant_id", "user_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_subscription_tenant_id_endpoint",
                schema: "notification",
                table: "push_subscription",
                columns: new[] { "tenant_id", "endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_subscription_tenant_id_user_id",
                schema: "notification",
                table: "push_subscription",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.EnableTenantRls("notification", "inbox_item");
            migrationBuilder.EnableTenantRls("notification", "notification_preference");
            migrationBuilder.EnableTenantRls("notification", "push_subscription");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("notification", "inbox_item");
            migrationBuilder.DisableTenantRls("notification", "notification_preference");
            migrationBuilder.DisableTenantRls("notification", "push_subscription");

            migrationBuilder.DropTable(
                name: "inbox_item",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "notification_preference",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "push_subscription",
                schema: "notification");

            migrationBuilder.DropIndex(
                name: "ix_notification_message_tenant_id_digest_key_status",
                schema: "notification",
                table: "notification_message");

            migrationBuilder.DropColumn(
                name: "digest_key",
                schema: "notification",
                table: "notification_message");

            migrationBuilder.DropSequence(
                name: "inbox_seq",
                schema: "notification");
        }
    }
}
