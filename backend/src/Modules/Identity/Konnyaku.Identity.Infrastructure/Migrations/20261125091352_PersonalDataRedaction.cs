using Konnyaku.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Konnyaku.Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Erasure support: <c>identity.redact_change_log</c> blanks the history values of an anonymized learner account, and
    /// names / emails logged before they were logged masked are blanked once.
    /// </summary>
    public partial class PersonalDataRedaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateChangeLogRedaction("identity", "kk_identity");
            migrationBuilder.RedactLoggedFields("identity", "displayName", "email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.redact_change_log(uuid);");
        }
    }
}
