using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseItemTypeNameLowerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Local person creation falls back to Type + lower(Name). The CleanName index
            // only filters Type and otherwise reads every person for each missing name.
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_BaseItems_Type_NameLower\" ON \"BaseItems\" (\"Type\", lower(\"Name\"));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_BaseItems_Type_NameLower\";");
        }
    }
}
