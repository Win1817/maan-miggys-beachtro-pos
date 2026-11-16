using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShiftCloseSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloseSnapshotJson",
                table: "CashierShifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseSnapshotSha256",
                table: "CashierShifts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseSnapshotJson",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "CloseSnapshotSha256",
                table: "CashierShifts");
        }
    }
}
