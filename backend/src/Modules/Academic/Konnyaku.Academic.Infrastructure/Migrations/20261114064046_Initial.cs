using System;
using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Academic.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "academic");

            migrationBuilder.CreateTable(
                name: "class",
                schema: "academic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    course_level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    enrolled_count = table.Column<int>(type: "integer", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_class", x => x.id);
                    table.CheckConstraint("ck_class_enrolled_within_capacity", "enrolled_count >= 0 AND enrolled_count <= capacity");
                });

            migrationBuilder.CreateTable(
                name: "enrollment",
                schema: "academic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    frozen_from = table.Column<DateOnly>(type: "date", nullable: true),
                    frozen_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "academic",
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
                schema: "academic",
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
                name: "program",
                schema: "academic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    language_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    framework_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    age_group = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    goal = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_program", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "course_level",
                schema: "academic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    target_level_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    session_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course_level", x => x.id);
                    table.ForeignKey(
                        name: "fk_course_level_program_program_id",
                        column: x => x.program_id,
                        principalSchema: "academic",
                        principalTable: "program",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "curriculum_lesson",
                schema: "academic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_curriculum_lesson", x => x.id);
                    table.ForeignKey(
                        name: "fk_curriculum_lesson_course_level_course_level_id",
                        column: x => x.course_level_id,
                        principalSchema: "academic",
                        principalTable: "course_level",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_class_tenant_id_branch_id_starts_on",
                schema: "academic",
                table: "class",
                columns: new[] { "tenant_id", "branch_id", "starts_on" });

            migrationBuilder.CreateIndex(
                name: "ix_class_tenant_id_code",
                schema: "academic",
                table: "class",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_course_level_program_id",
                schema: "academic",
                table: "course_level",
                column: "program_id");

            migrationBuilder.CreateIndex(
                name: "ix_course_level_tenant_id_program_id_code",
                schema: "academic",
                table: "course_level",
                columns: new[] { "tenant_id", "program_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_lesson_course_level_id",
                schema: "academic",
                table: "curriculum_lesson",
                column: "course_level_id");

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_lesson_tenant_id_course_level_id_ordinal",
                schema: "academic",
                table: "curriculum_lesson",
                columns: new[] { "tenant_id", "course_level_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_class_id_student_id",
                schema: "academic",
                table: "enrollment",
                columns: new[] { "class_id", "student_id" },
                unique: true,
                filter: "status IN ('Active', 'Frozen')");

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tenant_id_class_id",
                schema: "academic",
                table: "enrollment",
                columns: new[] { "tenant_id", "class_id" });

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tenant_id_student_id",
                schema: "academic",
                table: "enrollment",
                columns: new[] { "tenant_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "academic",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_program_tenant_id_code",
                schema: "academic",
                table: "program",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.EnableTenantRls("academic", "program");
            migrationBuilder.EnableTenantRls("academic", "course_level");
            migrationBuilder.EnableTenantRls("academic", "curriculum_lesson");
            migrationBuilder.EnableTenantRls("academic", "class");
            migrationBuilder.EnableTenantRls("academic", "enrollment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRls("academic", "program");
            migrationBuilder.DisableTenantRls("academic", "course_level");
            migrationBuilder.DisableTenantRls("academic", "curriculum_lesson");
            migrationBuilder.DisableTenantRls("academic", "class");
            migrationBuilder.DisableTenantRls("academic", "enrollment");

            migrationBuilder.DropTable(
                name: "class",
                schema: "academic");

            migrationBuilder.DropTable(
                name: "curriculum_lesson",
                schema: "academic");

            migrationBuilder.DropTable(
                name: "enrollment",
                schema: "academic");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "academic");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "academic");

            migrationBuilder.DropTable(
                name: "course_level",
                schema: "academic");

            migrationBuilder.DropTable(
                name: "program",
                schema: "academic");
        }
    }
}
