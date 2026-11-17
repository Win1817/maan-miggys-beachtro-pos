using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// POS-DEC-009: adds the NotTaxed treatment to the three TreatmentCode CHECK constraints. SQLite
    /// changes a CHECK by rebuilding the table (EF runs rebuilds at the end of the migration), which
    /// drops that table's immutability triggers; <see cref="NoTaxationGuards"/> re-creates them.
    /// </summary>
    public partial class NoTaxationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TaxConfigurations_TreatmentCode_Domain",
                table: "TaxConfigurations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_TreatmentCode_Domain",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemTaxComponents_TreatmentCode_Domain",
                table: "ItemTaxComponents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TaxConfigurations_TreatmentCode_Domain",
                table: "TaxConfigurations",
                sql: "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax', 'NotTaxed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_TreatmentCode_Domain",
                table: "OrderItems",
                sql: "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax', 'NotTaxed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemTaxComponents_TreatmentCode_Domain",
                table: "ItemTaxComponents",
                sql: "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax', 'NotTaxed')");
        
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TaxConfigurations_TreatmentCode_Domain",
                table: "TaxConfigurations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_TreatmentCode_Domain",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemTaxComponents_TreatmentCode_Domain",
                table: "ItemTaxComponents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TaxConfigurations_TreatmentCode_Domain",
                table: "TaxConfigurations",
                sql: "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_TreatmentCode_Domain",
                table: "OrderItems",
                sql: "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemTaxComponents_TreatmentCode_Domain",
                table: "ItemTaxComponents",
                sql: "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax')");

        }
    }
}
