using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "credit_balance",
                schema: "billing",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    bonus_credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    unit_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_balance", x => x.order_id);
                });

            migrationBuilder.CreateTable(
                name: "credit_ledger_entry",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entry_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    credit_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    unit_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    attendance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_local_date = table.Column<DateOnly>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_ledger_entry", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "billing",
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
                name: "order",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    paid_credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    bonus_credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    amount_received = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "billing",
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
                name: "package",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    paid_credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    bonus_credits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_package", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    method = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_balance_enrollment_id",
                schema: "billing",
                table: "credit_balance",
                column: "enrollment_id",
                unique: true,
                filter: "enrollment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_balance_tenant_id_student_id",
                schema: "billing",
                table: "credit_balance",
                columns: new[] { "tenant_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_entry_attendance_id",
                schema: "billing",
                table: "credit_ledger_entry",
                column: "attendance_id",
                unique: true,
                filter: "attendance_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_entry_tenant_id_entry_type_session_local_date",
                schema: "billing",
                table: "credit_ledger_entry",
                columns: new[] { "tenant_id", "entry_type", "session_local_date" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_entry_tenant_id_order_id_created_at",
                schema: "billing",
                table: "credit_ledger_entry",
                columns: new[] { "tenant_id", "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_tenant_id_student_id",
                schema: "billing",
                table: "order",
                columns: new[] { "tenant_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "billing",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_package_tenant_id_code",
                schema: "billing",
                table: "package",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_tenant_id_order_id",
                schema: "billing",
                table: "payment",
                columns: new[] { "tenant_id", "order_id" });

            // The ledger is append-only: the application role may insert and read, never update or delete.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kk_billing') THEN
                        REVOKE UPDATE, DELETE ON billing.credit_ledger_entry FROM kk_billing;
                    END IF;
                END $$;
                """);

            migrationBuilder.EnableTenantRls("billing", "package");
            migrationBuilder.EnableTenantRls("billing", "order");
            migrationBuilder.EnableTenantRls("billing", "payment");
            migrationBuilder.EnableTenantRls("billing", "credit_ledger_entry");
            migrationBuilder.EnableTenantRls("billing", "credit_balance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("billing", "package");
            migrationBuilder.DisableTenantRls("billing", "order");
            migrationBuilder.DisableTenantRls("billing", "payment");
            migrationBuilder.DisableTenantRls("billing", "credit_ledger_entry");
            migrationBuilder.DisableTenantRls("billing", "credit_balance");

            migrationBuilder.DropTable(
                name: "credit_balance",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "credit_ledger_entry",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "order",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "package",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "payment",
                schema: "billing");
        }
    }
}
