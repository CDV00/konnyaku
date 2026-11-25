using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ListsAndPrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The generated search columns call public.kk_unaccent (databases created before it was in the init script).
            migrationBuilder.EnsureUnaccent();

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymized_at",
                schema: "crm",
                table: "student_profile",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymized_at",
                schema: "crm",
                table: "lead",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "search_text",
                schema: "crm",
                table: "student_profile",
                type: "text",
                nullable: false,
                computedColumnSql: "public.kk_unaccent(lower(full_name || ' ' || coalesce(phone, '') || ' ' || coalesce(email, '') || ' ' || coalesce(guardian_name, '')))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "search_text",
                schema: "crm",
                table: "lead",
                type: "text",
                nullable: false,
                computedColumnSql: "public.kk_unaccent(lower(student_name || ' ' || coalesce(phone, '') || ' ' || coalesce(email, '') || ' ' || coalesce(guardian_name, '')))",
                stored: true);

            migrationBuilder.CreateTable(
                name: "data_subject_request",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_subject_request", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "personal_data_access",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    filter = table.Column<string>(type: "jsonb", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_data_access", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_student_profile_search_text",
                schema: "crm",
                table: "student_profile",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_student_profile_tenant_id_created_at",
                schema: "crm",
                table: "student_profile",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_student_profile_tenant_id_full_name_id",
                schema: "crm",
                table: "student_profile",
                columns: new[] { "tenant_id", "full_name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_student_profile_tenant_id_home_branch_id",
                schema: "crm",
                table: "student_profile",
                columns: new[] { "tenant_id", "home_branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_search_text",
                schema: "crm",
                table: "lead",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_tenant_id_created_at",
                schema: "crm",
                table: "lead",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_tenant_id_updated_at",
                schema: "crm",
                table: "lead",
                columns: new[] { "tenant_id", "updated_at" },
                filter: "anonymized_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_request_tenant_id_status_due_at",
                schema: "crm",
                table: "data_subject_request",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_request_tenant_id_subject_id",
                schema: "crm",
                table: "data_subject_request",
                columns: new[] { "tenant_id", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_personal_data_access_tenant_id_actor_id_occurred_at",
                schema: "crm",
                table: "personal_data_access",
                columns: new[] { "tenant_id", "actor_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_personal_data_access_tenant_id_occurred_at",
                schema: "crm",
                table: "personal_data_access",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_personal_data_access_tenant_id_subject_id",
                schema: "crm",
                table: "personal_data_access",
                columns: new[] { "tenant_id", "subject_id" });

            migrationBuilder.EnableTenantRls("crm", "data_subject_request");
            migrationBuilder.EnableTenantRls("crm", "personal_data_access");
            // The access log is evidence: the module role adds and reads entries, never edits or deletes them.
            migrationBuilder.MakeAppendOnly("crm", "personal_data_access", "kk_crm");
            migrationBuilder.CreateChangeLogRedaction("crm", "kk_crm");
            // History written before personal data was logged masked: blank those values once.
            migrationBuilder.RedactLoggedFields("crm", "fullName", "studentName", "guardianName", "phone", "email", "birthYear");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS crm.redact_change_log(uuid);");
            migrationBuilder.DisableTenantRls("crm", "personal_data_access");
            migrationBuilder.DisableTenantRls("crm", "data_subject_request");

            migrationBuilder.DropTable(
                name: "data_subject_request",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "personal_data_access",
                schema: "crm");

            migrationBuilder.DropIndex(
                name: "ix_student_profile_search_text",
                schema: "crm",
                table: "student_profile");

            migrationBuilder.DropIndex(
                name: "ix_student_profile_tenant_id_created_at",
                schema: "crm",
                table: "student_profile");

            migrationBuilder.DropIndex(
                name: "ix_student_profile_tenant_id_full_name_id",
                schema: "crm",
                table: "student_profile");

            migrationBuilder.DropIndex(
                name: "ix_student_profile_tenant_id_home_branch_id",
                schema: "crm",
                table: "student_profile");

            migrationBuilder.DropIndex(
                name: "ix_lead_search_text",
                schema: "crm",
                table: "lead");

            migrationBuilder.DropIndex(
                name: "ix_lead_tenant_id_created_at",
                schema: "crm",
                table: "lead");

            migrationBuilder.DropIndex(
                name: "ix_lead_tenant_id_updated_at",
                schema: "crm",
                table: "lead");

            migrationBuilder.DropColumn(
                name: "search_text",
                schema: "crm",
                table: "student_profile");

            migrationBuilder.DropColumn(
                name: "search_text",
                schema: "crm",
                table: "lead");

            migrationBuilder.DropColumn(
                name: "anonymized_at",
                schema: "crm",
                table: "student_profile");

            migrationBuilder.DropColumn(
                name: "anonymized_at",
                schema: "crm",
                table: "lead");
        }
    }
}
