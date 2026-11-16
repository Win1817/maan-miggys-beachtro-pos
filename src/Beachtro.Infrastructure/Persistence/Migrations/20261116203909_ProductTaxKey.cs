using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductTaxKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaxKey",
                table: "Products",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_TaxKey",
                table: "Products",
                sql: "length(trim(\"TaxKey\")) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_TaxKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TaxKey",
                table: "Products");
        }
    }
}
