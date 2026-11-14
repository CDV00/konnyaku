using System;
using System.Collections.Generic;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Scheduling.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "scheduling",
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
                schema: "scheduling",
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
                name: "resource_booking",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_booking", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schedule_pattern",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    iso_days = table.Column<List<int>>(type: "integer[]", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: true),
                    room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_pattern", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pattern_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence_no = table.Column<int>(type: "integer", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivery_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    planned_teacher_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actual_teacher_id = table.Column<Guid>(type: "uuid", nullable: true),
                    room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "scheduling",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_resource_booking_tenant_id_session_id",
                schema: "scheduling",
                table: "resource_booking",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_schedule_pattern_tenant_id_class_id",
                schema: "scheduling",
                table: "schedule_pattern",
                columns: new[] { "tenant_id", "class_id" });

            migrationBuilder.CreateIndex(
                name: "ix_session_status_starts_at",
                schema: "scheduling",
                table: "session",
                columns: new[] { "status", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_tenant_id_actual_teacher_id_starts_at",
                schema: "scheduling",
                table: "session",
                columns: new[] { "tenant_id", "actual_teacher_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_tenant_id_class_id_starts_at",
                schema: "scheduling",
                table: "session",
                columns: new[] { "tenant_id", "class_id", "starts_at" });

            // A teacher or room cannot hold two overlapping active bookings — enforced by PostgreSQL, even under concurrency.
            migrationBuilder.Sql("""
                ALTER TABLE scheduling.resource_booking
                    ADD COLUMN during tstzrange GENERATED ALWAYS AS (tstzrange(starts_at, ends_at, '[)')) STORED;
                ALTER TABLE scheduling.resource_booking
                    ADD CONSTRAINT ex_resource_booking_no_overlap EXCLUDE USING gist (
                        resource_type WITH =, resource_id WITH =, during WITH &&) WHERE (is_active);
                """);

            migrationBuilder.EnableTenantRls("scheduling", "schedule_pattern");
            migrationBuilder.EnableTenantRls("scheduling", "session");
            migrationBuilder.EnableTenantRls("scheduling", "resource_booking");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("scheduling", "schedule_pattern");
            migrationBuilder.DisableTenantRls("scheduling", "session");
            migrationBuilder.DisableTenantRls("scheduling", "resource_booking");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "resource_booking",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "schedule_pattern",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "session",
                schema: "scheduling");
        }
    }
}
