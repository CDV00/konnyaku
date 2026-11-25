using System;
using System.Collections.Generic;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Assessment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assessment");

            migrationBuilder.CreateTable(
                name: "assessment",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    language_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    framework_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    show_ruby = table.Column<bool>(type: "boolean", nullable: false),
                    layout = table.Column<string>(type: "jsonb", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assessment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assignment",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paper_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    student_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opens_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    late_policy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    late_penalty_percent = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: true),
                    scoring_rule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    grader = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    score_release = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    release_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    answer_visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    time_limit_minutes = table.Column<int>(type: "integer", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attempt",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    layout = table.Column<string>(type: "jsonb", nullable: false),
                    section_index = table.Column<int>(type: "integer", nullable: false),
                    section_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lock_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_submitted = table.Column<bool>(type: "boolean", nullable: false),
                    late = table.Column<bool>(type: "boolean", nullable: false),
                    max_score = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    raw_score = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    score = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    penalty_percent = table.Column<int>(type: "integer", nullable: false),
                    pending_items = table.Column<int>(type: "integer", nullable: false),
                    graded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    graded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    last_saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempt", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attempt_event",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    value = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempt_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attempt_item",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    section_index = table.Column<int>(type: "integer", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    response = table.Column<string>(type: "jsonb", nullable: false),
                    flagged = table.Column<bool>(type: "boolean", nullable: false),
                    score = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bands = table.Column<string>(type: "jsonb", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    graded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    graded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempt_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "assessment",
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
                schema: "assessment",
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
                name: "question",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stem = table.Column<string>(type: "text", nullable: false),
                    media = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    answer_key = table.Column<string>(type: "jsonb", nullable: false),
                    points = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    framework_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    skill_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    level_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "question_bank",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    language_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_bank", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "question_group",
                schema: "assessment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    passage = table.Column<string>(type: "text", nullable: false),
                    media = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_group", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assessment_tenant_id_updated_at",
                schema: "assessment",
                table: "assessment",
                columns: new[] { "tenant_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_tenant_id_branch_id_opens_at",
                schema: "assessment",
                table: "assignment",
                columns: new[] { "tenant_id", "branch_id", "opens_at" });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_tenant_id_class_id_opens_at",
                schema: "assessment",
                table: "assignment",
                columns: new[] { "tenant_id", "class_id", "opens_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attempt_assignment_id_student_id_number",
                schema: "assessment",
                table: "attempt",
                columns: new[] { "assignment_id", "student_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attempt_status_deadline_at",
                schema: "assessment",
                table: "attempt",
                columns: new[] { "status", "deadline_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attempt_tenant_id_user_id",
                schema: "assessment",
                table: "attempt",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_attempt_open",
                schema: "assessment",
                table: "attempt",
                columns: new[] { "assignment_id", "student_id" },
                unique: true,
                filter: "status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "ix_attempt_event_attempt_id_occurred_at",
                schema: "assessment",
                table: "attempt_event",
                columns: new[] { "attempt_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attempt_item_attempt_id_ordinal",
                schema: "assessment",
                table: "attempt_item",
                columns: new[] { "attempt_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "assessment",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_question_group_id",
                schema: "assessment",
                table: "question",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_question_tags",
                schema: "assessment",
                table: "question",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_bank_id_archived",
                schema: "assessment",
                table: "question",
                columns: new[] { "tenant_id", "bank_id", "archived" });

            migrationBuilder.CreateIndex(
                name: "ix_question_bank_tenant_id_language_code_name",
                schema: "assessment",
                table: "question_bank",
                columns: new[] { "tenant_id", "language_code", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_question_group_tenant_id_bank_id",
                schema: "assessment",
                table: "question_group",
                columns: new[] { "tenant_id", "bank_id" });

            migrationBuilder.EnableTenantRls("assessment", "question_bank");
            migrationBuilder.EnableTenantRls("assessment", "question_group");
            migrationBuilder.EnableTenantRls("assessment", "question");
            migrationBuilder.EnableTenantRls("assessment", "assessment");
            migrationBuilder.EnableTenantRls("assessment", "assignment");
            migrationBuilder.EnableTenantRls("assessment", "attempt");
            migrationBuilder.EnableTenantRls("assessment", "attempt_item");
            migrationBuilder.EnableTenantRls("assessment", "attempt_event");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("assessment", "question_bank");
            migrationBuilder.DisableTenantRls("assessment", "question_group");
            migrationBuilder.DisableTenantRls("assessment", "question");
            migrationBuilder.DisableTenantRls("assessment", "assessment");
            migrationBuilder.DisableTenantRls("assessment", "assignment");
            migrationBuilder.DisableTenantRls("assessment", "attempt");
            migrationBuilder.DisableTenantRls("assessment", "attempt_item");
            migrationBuilder.DisableTenantRls("assessment", "attempt_event");

            migrationBuilder.DropTable(
                name: "assessment",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "assignment",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "attempt",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "attempt_event",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "attempt_item",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "question",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "question_bank",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "question_group",
                schema: "assessment");
        }
    }
}
