using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncAndArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN with an inline CHECK: SQLite allows it, and it avoids the table rebuild (run outside
            // a transaction) that a separate AddCheckConstraint would cause. Same constraint as the model.
            migrationBuilder.Sql(
                "ALTER TABLE \"CloudStorageSettings\" ADD COLUMN \"BindingId\" TEXT NULL CONSTRAINT \"CK_CloudStorageSettings_BindingId_Domain\" "
                + "CHECK (\"BindingId\" IS NULL OR (length(\"BindingId\") = 36 AND \"BindingId\" = lower(\"BindingId\")));");

            migrationBuilder.CreateTable(
                name: "ArtifactHolds",
                columns: table => new
                {
                    ArtifactId = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactHolds", x => x.ArtifactId);
                    table.CheckConstraint("CK_ArtifactHolds_ArtifactId_Domain", "length(\"ArtifactId\") = 36 AND \"ArtifactId\" = lower(\"ArtifactId\")");
                    table.CheckConstraint("CK_ArtifactHolds_Code", "length(trim(\"Code\")) > 0");
                    table.CheckConstraint("CK_ArtifactHolds_Resolved", " \"ResolvedUtc\" IS NULL OR \"ResolvedUtc\" >= \"DetectedUtc\" ");
                    table.ForeignKey(
                        name: "FK_ArtifactHolds_ArtifactRecords_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "ArtifactRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtifactHolds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CloudStorageSettings_BindingId_Domain",
                table: "CloudStorageSettings");

            migrationBuilder.DropColumn(
                name: "BindingId",
                table: "CloudStorageSettings");
        }
    }
}
