using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefundsAndReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "receipt_no",
                schema: "billing",
                table: "payment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "received_by_id",
                schema: "billing",
                table: "payment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "received_by_name",
                schema: "billing",
                table: "payment",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "amount_refunded",
                schema: "billing",
                table: "order",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refunded_at",
                schema: "billing",
                table: "order",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_sequence",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_sequence", x => new { x.tenant_id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "receipt_print_log",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_no = table.Column<long>(type: "bigint", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    audience = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    client_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_print_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refund",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    receipt_no = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    suggested_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    unused_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    retained_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    paid_credits_returned = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    bonus_credits_forfeited = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    approved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund", x => x.id);
                });

            // Existing payments get receipt numbers in the order they were received (per center), their collector from the
            // change history, and each center's counter starts after its last number. The owner bypasses RLS meanwhile.
            migrationBuilder.Sql("""
                ALTER TABLE billing.payment NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE billing.change_log NO FORCE ROW LEVEL SECURITY;
                UPDATE billing.payment p SET receipt_no = n.rn
                FROM (SELECT id, row_number() OVER (PARTITION BY tenant_id ORDER BY received_at, id) AS rn FROM billing.payment) n
                WHERE p.id = n.id;
                UPDATE billing.payment p SET received_by_id = c.actor_id, received_by_name = c.actor_name
                FROM billing.change_log c
                WHERE c.tenant_id = p.tenant_id AND c.entity_id = p.id AND c.entity_type = 'Payment' AND c.action = 'Created';
                INSERT INTO billing.document_sequence (tenant_id, kind, last_value)
                SELECT tenant_id, 'receipt', max(receipt_no) FROM billing.payment GROUP BY tenant_id;
                ALTER TABLE billing.payment FORCE ROW LEVEL SECURITY;
                ALTER TABLE billing.change_log FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_payment_tenant_id_receipt_no",
                schema: "billing",
                table: "payment",
                columns: new[] { "tenant_id", "receipt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_receipt_print_log_tenant_id_document_id_occurred_at",
                schema: "billing",
                table: "receipt_print_log",
                columns: new[] { "tenant_id", "document_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_tenant_id_local_date",
                schema: "billing",
                table: "refund",
                columns: new[] { "tenant_id", "local_date" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_tenant_id_order_id",
                schema: "billing",
                table: "refund",
                columns: new[] { "tenant_id", "order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_tenant_id_receipt_no",
                schema: "billing",
                table: "refund",
                columns: new[] { "tenant_id", "receipt_no" },
                unique: true);

            migrationBuilder.EnableTenantRls("billing", "document_sequence");
            migrationBuilder.EnableTenantRls("billing", "refund");
            migrationBuilder.EnableTenantRls("billing", "receipt_print_log");

            // A refund and a print log are records of what happened: never edited or deleted.
            migrationBuilder.MakeAppendOnly("billing", "refund", "kk_billing");
            migrationBuilder.MakeAppendOnly("billing", "receipt_print_log", "kk_billing");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("billing", "receipt_print_log");
            migrationBuilder.DisableTenantRls("billing", "refund");
            migrationBuilder.DisableTenantRls("billing", "document_sequence");

            migrationBuilder.DropTable(
                name: "document_sequence",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "receipt_print_log",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "refund",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_payment_tenant_id_receipt_no",
                schema: "billing",
                table: "payment");

            migrationBuilder.DropColumn(
                name: "receipt_no",
                schema: "billing",
                table: "payment");

            migrationBuilder.DropColumn(
                name: "received_by_id",
                schema: "billing",
                table: "payment");

            migrationBuilder.DropColumn(
                name: "received_by_name",
                schema: "billing",
                table: "payment");

            migrationBuilder.DropColumn(
                name: "amount_refunded",
                schema: "billing",
                table: "order");

            migrationBuilder.DropColumn(
                name: "refunded_at",
                schema: "billing",
                table: "order");
        }
    }
}
