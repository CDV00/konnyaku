using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Scheduling.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SessionBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "booking_settings",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    postpone_deadline_hours = table.Column<int>(type: "integer", nullable: false),
                    max_postpones_per_enrollment = table.Column<int>(type: "integer", nullable: false),
                    entitlement_days = table.Column<int>(type: "integer", nullable: false),
                    mode_switch_deadline_hours = table.Column<int>(type: "integer", nullable: false),
                    makeup_seats_per_session = table.Column<int>(type: "integer", nullable: false),
                    waitlist_hold_hours = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "makeup_entitlement",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_by_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    request_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    declined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_makeup_entitlement", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session_booking",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    postponed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    placed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_booking", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session_seat",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    online_capacity = table.Column<int>(type: "integer", nullable: false),
                    offline_capacity = table.Column<int>(type: "integer", nullable: false),
                    online_booked = table.Column<int>(type: "integer", nullable: false),
                    offline_booked = table.Column<int>(type: "integer", nullable: false),
                    makeup_capacity = table.Column<int>(type: "integer", nullable: false),
                    makeup_booked = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_seat", x => x.id);
                    table.CheckConstraint("ck_session_seat_counts", "online_booked >= 0 AND offline_booked >= 0 AND makeup_booked >= 0");
                });

            migrationBuilder.CreateTable(
                name: "session_waitlist",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    offer_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_waitlist", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_booking_settings_tenant_id",
                schema: "scheduling",
                table: "booking_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_makeup_entitlement_tenant_id_requested_at",
                schema: "scheduling",
                table: "makeup_entitlement",
                columns: new[] { "tenant_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_makeup_entitlement_tenant_id_source_booking_id",
                schema: "scheduling",
                table: "makeup_entitlement",
                columns: new[] { "tenant_id", "source_booking_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_makeup_entitlement_tenant_id_student_id_expires_at",
                schema: "scheduling",
                table: "makeup_entitlement",
                columns: new[] { "tenant_id", "student_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_booking_tenant_id_enrollment_id_status",
                schema: "scheduling",
                table: "session_booking",
                columns: new[] { "tenant_id", "enrollment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_session_booking_tenant_id_session_id_student_id",
                schema: "scheduling",
                table: "session_booking",
                columns: new[] { "tenant_id", "session_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_booking_tenant_id_student_id_status",
                schema: "scheduling",
                table: "session_booking",
                columns: new[] { "tenant_id", "student_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_session_seat_tenant_id_session_id",
                schema: "scheduling",
                table: "session_seat",
                columns: new[] { "tenant_id", "session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_waitlist_status_offer_expires_at",
                schema: "scheduling",
                table: "session_waitlist",
                columns: new[] { "status", "offer_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_waitlist_tenant_id_session_id_status_created_at",
                schema: "scheduling",
                table: "session_waitlist",
                columns: new[] { "tenant_id", "session_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_waitlist_tenant_id_session_id_student_id",
                schema: "scheduling",
                table: "session_waitlist",
                columns: new[] { "tenant_id", "session_id", "student_id" },
                unique: true,
                filter: "status IN ('Waiting', 'Offered')");

            migrationBuilder.EnableTenantRls("scheduling", "booking_settings");
            migrationBuilder.EnableTenantRls("scheduling", "makeup_entitlement");
            migrationBuilder.EnableTenantRls("scheduling", "session_booking");
            migrationBuilder.EnableTenantRls("scheduling", "session_seat");
            migrationBuilder.EnableTenantRls("scheduling", "session_waitlist");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("scheduling", "booking_settings");
            migrationBuilder.DisableTenantRls("scheduling", "makeup_entitlement");
            migrationBuilder.DisableTenantRls("scheduling", "session_booking");
            migrationBuilder.DisableTenantRls("scheduling", "session_seat");
            migrationBuilder.DisableTenantRls("scheduling", "session_waitlist");

            migrationBuilder.DropTable(
                name: "booking_settings",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "makeup_entitlement",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "session_booking",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "session_seat",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "session_waitlist",
                schema: "scheduling");
        }
    }
}
