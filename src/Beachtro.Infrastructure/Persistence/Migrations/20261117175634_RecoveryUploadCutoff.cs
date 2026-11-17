using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryUploadCutoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "UploadIdentityCutoffUtc",
                table: "RecoveryRuns",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadIdentityCutoffUtc",
                table: "RecoveryRuns");
        }
    }
}
