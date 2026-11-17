using Beachtro.Infrastructure.Persistence.Migrations.Sql;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Re-creates the immutability triggers of the tables rebuilt by <see cref="NoTaxationPolicy"/>,
    /// from the frozen baseline guard SQL (the statements are unchanged).
    /// </summary>
    public partial class NoTaxationGuards : Migration
    {
        private static readonly string[] RebuiltTables = ["TaxConfigurations", "OrderItems", "ItemTaxComponents"];

        private static void RestoreGuards(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in GuardSql.Load(GuardSql.BaselineFile)
                .Where(s => RebuiltTables.Any(t => s.Contains($"ON \"{t}\"", StringComparison.Ordinal))))
            {
                var name = statement.Split('"')[1];
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"{name}\";");
                migrationBuilder.Sql(statement);
            }
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => RestoreGuards(migrationBuilder);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the triggers existed before NoTaxationPolicy. Rolling that migration back
            // rebuilds the tables again and would drop them, so a rollback past it is not supported.
        }
    }
}
