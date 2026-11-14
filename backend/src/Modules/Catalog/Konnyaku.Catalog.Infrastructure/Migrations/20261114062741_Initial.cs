using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Konnyaku.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "catalog",
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
                name: "language",
                schema: "catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_language", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "catalog",
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
                name: "proficiency_framework",
                schema: "catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    language_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proficiency_framework", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "proficiency_level",
                schema: "catalog",
                columns: table => new
                {
                    framework_code = table.Column<string>(type: "character varying(30)", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    cefr_approx = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proficiency_level", x => new { x.framework_code, x.code });
                    table.ForeignKey(
                        name: "fk_proficiency_level_proficiency_framework_framework_code",
                        column: x => x.framework_code,
                        principalSchema: "catalog",
                        principalTable: "proficiency_framework",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skill_dimension",
                schema: "catalog",
                columns: table => new
                {
                    framework_code = table.Column<string>(type: "character varying(30)", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skill_dimension", x => new { x.framework_code, x.code });
                    table.ForeignKey(
                        name: "fk_skill_dimension_proficiency_framework_framework_code",
                        column: x => x.framework_code,
                        principalSchema: "catalog",
                        principalTable: "proficiency_framework",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "language",
                columns: new[] { "code", "name" },
                values: new object[,]
                {
                    { "de", "{\"vi\":\"Ti\\u1EBFng \\u0110\\u1EE9c\",\"en\":\"German\"}" },
                    { "en", "{\"vi\":\"Ti\\u1EBFng Anh\",\"en\":\"English\"}" },
                    { "fr", "{\"vi\":\"Ti\\u1EBFng Ph\\u00E1p\",\"en\":\"French\"}" },
                    { "ja", "{\"vi\":\"Ti\\u1EBFng Nh\\u1EADt\",\"en\":\"Japanese\"}" },
                    { "ko", "{\"vi\":\"Ti\\u1EBFng H\\u00E0n\",\"en\":\"Korean\"}" },
                    { "vi", "{\"vi\":\"Ti\\u1EBFng Vi\\u1EC7t\",\"en\":\"Vietnamese\"}" },
                    { "zh-Hans", "{\"vi\":\"Ti\\u1EBFng Trung (gi\\u1EA3n th\\u1EC3)\",\"en\":\"Chinese (Simplified)\"}" }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "proficiency_framework",
                columns: new[] { "code", "language_code", "name" },
                values: new object[,]
                {
                    { "CAMBRIDGE_YLE", "en", "{\"vi\":\"Cambridge cho tr\\u1EBB em (YLE)\",\"en\":\"Cambridge Young Learners (YLE)\"}" },
                    { "CEFR", null, "{\"vi\":\"Khung tham chi\\u1EBFu ch\\u00E2u \\u00C2u (CEFR)\",\"en\":\"Common European Framework (CEFR)\"}" },
                    { "HSK", "zh-Hans", "{\"vi\":\"N\\u0103ng l\\u1EF1c H\\u00E1n ng\\u1EEF (HSK)\",\"en\":\"Chinese Proficiency Test (HSK)\"}" },
                    { "IELTS", "en", "{\"vi\":\"IELTS\",\"en\":\"IELTS\"}" },
                    { "JLPT", "ja", "{\"vi\":\"N\\u0103ng l\\u1EF1c ti\\u1EBFng Nh\\u1EADt (JLPT)\",\"en\":\"Japanese-Language Proficiency Test (JLPT)\"}" },
                    { "TOPIK", "ko", "{\"vi\":\"N\\u0103ng l\\u1EF1c ti\\u1EBFng H\\u00E0n (TOPIK)\",\"en\":\"Test of Proficiency in Korean (TOPIK)\"}" }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "proficiency_level",
                columns: new[] { "code", "framework_code", "cefr_approx", "ordinal" },
                values: new object[,]
                {
                    { "FLYERS", "CAMBRIDGE_YLE", "A2", 3 },
                    { "MOVERS", "CAMBRIDGE_YLE", "A1", 2 },
                    { "STARTERS", "CAMBRIDGE_YLE", "PreA1", 1 },
                    { "A1", "CEFR", "A1", 1 },
                    { "A2", "CEFR", "A2", 2 },
                    { "B1", "CEFR", "B1", 3 },
                    { "B2", "CEFR", "B2", 4 },
                    { "C1", "CEFR", "C1", 5 },
                    { "C2", "CEFR", "C2", 6 },
                    { "HSK1", "HSK", null, 1 },
                    { "HSK2", "HSK", null, 2 },
                    { "HSK3", "HSK", null, 3 },
                    { "HSK4", "HSK", null, 4 },
                    { "HSK5", "HSK", null, 5 },
                    { "HSK6", "HSK", null, 6 },
                    { "4.0", "IELTS", "B1", 1 },
                    { "4.5", "IELTS", "B1", 2 },
                    { "5.0", "IELTS", "B1", 3 },
                    { "5.5", "IELTS", "B2", 4 },
                    { "6.0", "IELTS", "B2", 5 },
                    { "6.5", "IELTS", "B2", 6 },
                    { "7.0", "IELTS", "C1", 7 },
                    { "7.5", "IELTS", "C1", 8 },
                    { "8.0", "IELTS", "C1", 9 },
                    { "8.5", "IELTS", "C2", 10 },
                    { "9.0", "IELTS", "C2", 11 },
                    { "N1", "JLPT", "C1", 5 },
                    { "N2", "JLPT", "B2", 4 },
                    { "N3", "JLPT", "B1", 3 },
                    { "N4", "JLPT", "A2", 2 },
                    { "N5", "JLPT", "A1", 1 },
                    { "TOPIK1", "TOPIK", null, 1 },
                    { "TOPIK2", "TOPIK", null, 2 },
                    { "TOPIK3", "TOPIK", null, 3 },
                    { "TOPIK4", "TOPIK", null, 4 },
                    { "TOPIK5", "TOPIK", null, 5 },
                    { "TOPIK6", "TOPIK", null, 6 }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "skill_dimension",
                columns: new[] { "code", "framework_code", "name" },
                values: new object[,]
                {
                    { "LISTENING", "CAMBRIDGE_YLE", "{\"vi\":\"Nghe\",\"en\":\"Listening\"}" },
                    { "READING_WRITING", "CAMBRIDGE_YLE", "{\"vi\":\"\\u0110\\u1ECDc \\u0026 Vi\\u1EBFt\",\"en\":\"Reading \\u0026 Writing\"}" },
                    { "SPEAKING", "CAMBRIDGE_YLE", "{\"vi\":\"N\\u00F3i\",\"en\":\"Speaking\"}" },
                    { "LISTENING", "CEFR", "{\"vi\":\"Nghe\",\"en\":\"Listening\"}" },
                    { "READING", "CEFR", "{\"vi\":\"\\u0110\\u1ECDc\",\"en\":\"Reading\"}" },
                    { "SPEAKING", "CEFR", "{\"vi\":\"N\\u00F3i\",\"en\":\"Speaking\"}" },
                    { "WRITING", "CEFR", "{\"vi\":\"Vi\\u1EBFt\",\"en\":\"Writing\"}" },
                    { "CHARACTERS", "HSK", "{\"vi\":\"Ch\\u1EEF H\\u00E1n\",\"en\":\"Characters\"}" },
                    { "LISTENING", "HSK", "{\"vi\":\"Nghe\",\"en\":\"Listening\"}" },
                    { "READING", "HSK", "{\"vi\":\"\\u0110\\u1ECDc\",\"en\":\"Reading\"}" },
                    { "WRITING", "HSK", "{\"vi\":\"Vi\\u1EBFt\",\"en\":\"Writing\"}" },
                    { "LISTENING", "IELTS", "{\"vi\":\"Nghe\",\"en\":\"Listening\"}" },
                    { "READING", "IELTS", "{\"vi\":\"\\u0110\\u1ECDc\",\"en\":\"Reading\"}" },
                    { "SPEAKING", "IELTS", "{\"vi\":\"N\\u00F3i\",\"en\":\"Speaking\"}" },
                    { "WRITING", "IELTS", "{\"vi\":\"Vi\\u1EBFt\",\"en\":\"Writing\"}" },
                    { "GRAMMAR", "JLPT", "{\"vi\":\"Ng\\u1EEF ph\\u00E1p\",\"en\":\"Grammar\"}" },
                    { "LISTENING", "JLPT", "{\"vi\":\"Nghe\",\"en\":\"Listening\"}" },
                    { "READING", "JLPT", "{\"vi\":\"\\u0110\\u1ECDc\",\"en\":\"Reading\"}" },
                    { "VOCABULARY_KANJI", "JLPT", "{\"vi\":\"T\\u1EEB v\\u1EF1ng \\u0026 Kanji\",\"en\":\"Vocabulary \\u0026 Kanji\"}" },
                    { "LISTENING", "TOPIK", "{\"vi\":\"Nghe\",\"en\":\"Listening\"}" },
                    { "READING", "TOPIK", "{\"vi\":\"\\u0110\\u1ECDc\",\"en\":\"Reading\"}" },
                    { "WRITING", "TOPIK", "{\"vi\":\"Vi\\u1EBFt\",\"en\":\"Writing\"}" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_occurred_at",
                schema: "catalog",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "language",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "proficiency_level",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "skill_dimension",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "proficiency_framework",
                schema: "catalog");
        }
    }
}
