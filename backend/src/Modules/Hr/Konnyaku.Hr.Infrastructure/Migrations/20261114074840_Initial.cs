using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Hr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hr");

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "hr",
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
                schema: "hr",
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
                name: "pay_rate",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hourly_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pay_rate", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_period",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<DateOnly>(type: "date", nullable: false),
                    to = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payroll_period", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "timesheet",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    planned_minutes = table.Column<int>(type: "integer", nullable: false),
                    actual_minutes = table.Column<int>(type: "integer", nullable: false),
                    late_minutes = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hourly_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payroll_period_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timesheet", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "hr",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pay_rate_tenant_id_teacher_id_effective_from",
                schema: "hr",
                table: "pay_rate",
                columns: new[] { "tenant_id", "teacher_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_period_tenant_id_from",
                schema: "hr",
                table: "payroll_period",
                columns: new[] { "tenant_id", "from" });

            migrationBuilder.CreateIndex(
                name: "ix_timesheet_session_id_teacher_id",
                schema: "hr",
                table: "timesheet",
                columns: new[] { "session_id", "teacher_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_timesheet_tenant_id_status_local_date",
                schema: "hr",
                table: "timesheet",
                columns: new[] { "tenant_id", "status", "local_date" });

            migrationBuilder.CreateIndex(
                name: "ix_timesheet_tenant_id_teacher_id_local_date",
                schema: "hr",
                table: "timesheet",
                columns: new[] { "tenant_id", "teacher_id", "local_date" });

            migrationBuilder.EnableTenantRls("hr", "pay_rate");
            migrationBuilder.EnableTenantRls("hr", "timesheet");
            migrationBuilder.EnableTenantRls("hr", "payroll_period");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("hr", "pay_rate");
            migrationBuilder.DisableTenantRls("hr", "timesheet");
            migrationBuilder.DisableTenantRls("hr", "payroll_period");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "pay_rate",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "payroll_period",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "timesheet",
                schema: "hr");
        }
    }
}
