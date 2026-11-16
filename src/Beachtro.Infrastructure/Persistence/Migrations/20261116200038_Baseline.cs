using Beachtro.Infrastructure.Persistence.Migrations.Sql;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beachtro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Backups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastSaleId = table.Column<string>(type: "TEXT", nullable: true),
                    LastSaleNumber = table.Column<string>(type: "TEXT", nullable: true),
                    VerifiedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    VerificationState = table.Column<string>(type: "TEXT", nullable: false),
                    UploadState = table.Column<string>(type: "TEXT", nullable: false),
                    RetentionProtectUntilUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Backups", x => x.Id);
                    table.CheckConstraint("CK_Backups_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Backups_LastSaleId_Domain", "\"LastSaleId\" IS NULL OR (length(\"LastSaleId\") = 36 AND \"LastSaleId\" = lower(\"LastSaleId\"))");
                    table.CheckConstraint("CK_Backups_Sha256", "length(\"Sha256\") = 64 AND \"Sha256\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_Backups_Size", " \"SizeBytes\" >= 0 AND \"SchemaVersion\" >= 1 ");
                    table.CheckConstraint("CK_Backups_UploadState_Domain", "\"UploadState\" IN ('NotQueued', 'Pending', 'Uploaded', 'Failed')");
                    table.CheckConstraint("CK_Backups_VerificationState_Domain", "\"VerificationState\" IN ('Created', 'Verified', 'Failed')");
                    table.CheckConstraint("CK_Backups_Verified", " \"VerificationState\" <> 'Verified' OR \"VerifiedUtc\" IS NOT NULL ");
                });

            migrationBuilder.CreateTable(
                name: "BusinessProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    RegisteredName = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    Contact = table.Column<string>(type: "TEXT", nullable: true),
                    TaxIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    RegistrationJson = table.Column<string>(type: "TEXT", nullable: false),
                    LogoArtifactRef = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ApprovalState = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessProfiles", x => x.Id);
                    table.CheckConstraint("CK_BusinessProfiles_ApprovalState_Domain", "\"ApprovalState\" IN ('Draft', 'Test', 'Approved')");
                    table.CheckConstraint("CK_BusinessProfiles_ApprovedIdentity", " \"ApprovalState\" <> 'Approved' OR (length(trim(\"RegisteredName\")) > 0 AND length(trim(\"Address\")) > 0) ");
                    table.CheckConstraint("CK_BusinessProfiles_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_BusinessProfiles_Revision", " \"Revision\" >= 1 AND \"TemplateVersion\" >= 1 ");
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.CheckConstraint("CK_Categories_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Categories_IsActive_Domain", "\"IsActive\" IN (0, 1)");
                    table.CheckConstraint("CK_Categories_Name", "length(trim(\"Name\")) > 0");
                    table.CheckConstraint("CK_Categories_Version", " \"Version\" >= 1 ");
                });

            migrationBuilder.CreateTable(
                name: "CloudStorageSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    AccountLabel = table.Column<string>(type: "TEXT", nullable: true),
                    RootFolderId = table.Column<string>(type: "TEXT", nullable: true),
                    OAuthClientConfigRef = table.Column<string>(type: "TEXT", nullable: true),
                    TokenSecretRef = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastConnectedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudStorageSettings", x => x.Id);
                    table.CheckConstraint("CK_CloudStorageSettings_Enabled_Domain", "\"Enabled\" IN (0, 1)");
                    table.CheckConstraint("CK_CloudStorageSettings_Singleton", " \"Id\" = 1 ");
                });

            migrationBuilder.CreateTable(
                name: "Discounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    ValueScaled = table.Column<long>(type: "INTEGER", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Discounts", x => x.Id);
                    table.CheckConstraint("CK_Discounts_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Discounts_IsActive_Domain", "\"IsActive\" IN (0, 1)");
                    table.CheckConstraint("CK_Discounts_Name", "length(trim(\"Name\")) > 0");
                    table.CheckConstraint("CK_Discounts_RequiresApproval_Domain", "\"RequiresApproval\" IN (0, 1)");
                    table.CheckConstraint("CK_Discounts_Type_Domain", "\"Type\" IN ('FixedM', 'PercentPpm')");
                    table.CheckConstraint("CK_Discounts_Value", "(\"Type\" = 'FixedM' AND \"ValueScaled\" > 0 AND \"ValueScaled\" <= 100000000000)\nOR (\"Type\" = 'PercentPpm' AND \"ValueScaled\" > 0 AND \"ValueScaled\" <= 1000000)");
                });

            migrationBuilder.CreateTable(
                name: "NumberSeries",
                columns: table => new
                {
                    SeriesKey = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessDate = table.Column<string>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", nullable: false),
                    LastReservedSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastReconciledUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    EvidenceRef = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSeries", x => x.SeriesKey);
                    table.CheckConstraint("CK_NumberSeries_BusinessDate", "\"BusinessDate\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'");
                    table.CheckConstraint("CK_NumberSeries_Prefix", "length(trim(\"Prefix\")) > 0");
                    table.CheckConstraint("CK_NumberSeries_Sequence", " \"LastReservedSequence\" BETWEEN 0 AND 999999 ");
                    table.CheckConstraint("CK_NumberSeries_Status_Domain", "\"Status\" IN ('Ready', 'Blocked', 'Exhausted')");
                    table.CheckConstraint("CK_NumberSeries_Version", " \"Version\" >= 1 ");
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                    table.CheckConstraint("CK_Permissions_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Permissions_Key", "length(trim(\"Key\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.CheckConstraint("CK_Roles_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Roles_Key", "length(trim(\"Key\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobs",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    LastDueKey = table.Column<string>(type: "TEXT", nullable: true),
                    LastCompletedKey = table.Column<string>(type: "TEXT", nullable: true),
                    NextDueUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobs", x => x.Kind);
                    table.CheckConstraint("CK_ScheduledJobs_Kind_Domain", "\"Kind\" IN ('DailyBackup', 'DailyReport', 'MonthlyReport')");
                    table.CheckConstraint("CK_ScheduledJobs_State_Domain", "\"State\" IN ('Idle', 'Running', 'Failed')");
                    table.CheckConstraint("CK_ScheduledJobs_Version", " \"Version\" >= 1 ");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SecurityVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_Counters", " \"FailedCount\" >= 0 AND \"SecurityVersion\" >= 1 AND \"Version\" >= 1 ");
                    table.CheckConstraint("CK_Users_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Users_IsActive_Domain", "\"IsActive\" IN (0, 1)");
                    table.CheckConstraint("CK_Users_MustChangePassword_Domain", "\"MustChangePassword\" IN (0, 1)");
                    table.CheckConstraint("CK_Users_PasswordHash", "length(trim(\"PasswordHash\")) > 0");
                    table.CheckConstraint("CK_Users_Username", "length(trim(\"Username\")) > 0 AND length(trim(\"NormalizedUsername\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<string>(type: "TEXT", nullable: false),
                    Sku = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedSku = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    UnitPriceM = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", nullable: false),
                    CurrencyExponent = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitStepQ = table.Column<long>(type: "INTEGER", nullable: false),
                    TrackStock = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_CategoryId_Domain", "length(\"CategoryId\") = 36 AND \"CategoryId\" = lower(\"CategoryId\")");
                    table.CheckConstraint("CK_Products_Currency", " length(\"CurrencyCode\") = 3 AND \"CurrencyCode\" = upper(\"CurrencyCode\") AND \"CurrencyExponent\" BETWEEN 0 AND 4 ");
                    table.CheckConstraint("CK_Products_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Products_IsActive_Domain", "\"IsActive\" IN (0, 1)");
                    table.CheckConstraint("CK_Products_Name", "length(trim(\"Name\")) > 0");
                    table.CheckConstraint("CK_Products_Price", " \"UnitPriceM\" > 0 AND \"UnitPriceM\" <= 100000000000 ");
                    table.CheckConstraint("CK_Products_Sku", "length(trim(\"Sku\")) > 0 AND length(trim(\"NormalizedSku\")) > 0");
                    table.CheckConstraint("CK_Products_TrackStock_Domain", "\"TrackStock\" IN (0, 1)");
                    table.CheckConstraint("CK_Products_UnitStep", " \"UnitStepQ\" IN (1, 1000) ");
                    table.CheckConstraint("CK_Products_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_Products_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    PermissionId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.PermissionId });
                    table.CheckConstraint("CK_RolePermissions_PermissionId_Domain", "length(\"PermissionId\") = 36 AND \"PermissionId\" = lower(\"PermissionId\")");
                    table.CheckConstraint("CK_RolePermissions_RoleId_Domain", "length(\"RoleId\") = 36 AND \"RoleId\" = lower(\"RoleId\")");
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: true),
                    ActorNameSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    SafeDetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.CheckConstraint("CK_AuditLogs_Action", "length(trim(\"Action\")) > 0 AND length(trim(\"EntityType\")) > 0");
                    table.CheckConstraint("CK_AuditLogs_Actor", "length(trim(\"ActorNameSnapshot\")) > 0");
                    table.CheckConstraint("CK_AuditLogs_ActorUserId_Domain", "\"ActorUserId\" IS NULL OR (length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\"))");
                    table.CheckConstraint("CK_AuditLogs_CorrelationId_Domain", "length(\"CorrelationId\") = 36 AND \"CorrelationId\" = lower(\"CorrelationId\")");
                    table.CheckConstraint("CK_AuditLogs_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_AuditLogs_RequestId_Domain", "\"RequestId\" IS NULL OR (length(\"RequestId\") = 36 AND \"RequestId\" = lower(\"RequestId\"))");
                    table.ForeignKey(
                        name: "FK_AuditLogs_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessSettings", x => x.Key);
                    table.CheckConstraint("CK_BusinessSettings_Key", "length(trim(\"Key\")) > 0");
                    table.CheckConstraint("CK_BusinessSettings_UpdatedByUserId_Domain", "length(\"UpdatedByUserId\") = 36 AND \"UpdatedByUserId\" = lower(\"UpdatedByUserId\")");
                    table.CheckConstraint("CK_BusinessSettings_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_BusinessSettings_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashierShifts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    OpeningCashM = table.Column<long>(type: "INTEGER", nullable: false),
                    CountedCashM = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpectedCashM = table.Column<long>(type: "INTEGER", nullable: true),
                    VarianceM = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CloseReason = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashierShifts", x => x.Id);
                    table.CheckConstraint("CK_CashierShifts_CashierUserId_Domain", "length(\"CashierUserId\") = 36 AND \"CashierUserId\" = lower(\"CashierUserId\")");
                    table.CheckConstraint("CK_CashierShifts_Close", "(\"ClosedUtc\" IS NULL AND \"CountedCashM\" IS NULL AND \"ExpectedCashM\" IS NULL AND \"VarianceM\" IS NULL AND \"ClosedByUserId\" IS NULL)\nOR (\"ClosedUtc\" IS NOT NULL AND \"CountedCashM\" IS NOT NULL AND \"ExpectedCashM\" IS NOT NULL AND \"VarianceM\" IS NOT NULL\n    AND \"ClosedByUserId\" IS NOT NULL AND \"CountedCashM\" >= 0 AND \"ClosedUtc\" >= \"OpenedUtc\"\n    AND \"VarianceM\" = \"CountedCashM\" - \"ExpectedCashM\")");
                    table.CheckConstraint("CK_CashierShifts_ClosedByUserId_Domain", "\"ClosedByUserId\" IS NULL OR (length(\"ClosedByUserId\") = 36 AND \"ClosedByUserId\" = lower(\"ClosedByUserId\"))");
                    table.CheckConstraint("CK_CashierShifts_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_CashierShifts_Opening", " \"OpeningCashM\" >= 0 ");
                    table.CheckConstraint("CK_CashierShifts_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_CashierShifts_Users_CashierUserId",
                        column: x => x.CashierUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashierShifts_Users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Drafts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CartJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.Id);
                    table.CheckConstraint("CK_Drafts_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Drafts_OwnerUserId_Domain", "length(\"OwnerUserId\") = 36 AND \"OwnerUserId\" = lower(\"OwnerUserId\")");
                    table.CheckConstraint("CK_Drafts_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_Drafts_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyVersions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveToUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ApprovalState = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyVersions", x => x.Id);
                    table.CheckConstraint("CK_PolicyVersions_ApprovalState_Domain", "\"ApprovalState\" IN ('Draft', 'Test', 'Approved')");
                    table.CheckConstraint("CK_PolicyVersions_ApprovedBy", " \"ApprovalState\" <> 'Approved' OR \"ApprovedByUserId\" IS NOT NULL ");
                    table.CheckConstraint("CK_PolicyVersions_ApprovedByUserId_Domain", "\"ApprovedByUserId\" IS NULL OR (length(\"ApprovedByUserId\") = 36 AND \"ApprovedByUserId\" = lower(\"ApprovedByUserId\"))");
                    table.CheckConstraint("CK_PolicyVersions_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_PolicyVersions_Interval", " \"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\" ");
                    table.CheckConstraint("CK_PolicyVersions_Kind_Domain", "\"Kind\" IN ('Calculation', 'Calendar', 'Retention', 'AccessPolicy')");
                    table.CheckConstraint("CK_PolicyVersions_PayloadSha256", "length(\"PayloadSha256\") = 64 AND \"PayloadSha256\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_PolicyVersions_Revision", " \"Revision\" >= 1 AND \"PayloadSchemaVersion\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_PolicyVersions_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceBackupId = table.Column<string>(type: "TEXT", nullable: true),
                    SourceChecksum = table.Column<string>(type: "TEXT", nullable: false),
                    PreservedDbPath = table.Column<string>(type: "TEXT", nullable: false),
                    NumberEvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReconciledLastSale = table.Column<string>(type: "TEXT", nullable: true),
                    Disposition = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    NewGenerationId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryRuns", x => x.Id);
                    table.CheckConstraint("CK_RecoveryRuns_ActorUserId_Domain", "length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\")");
                    table.CheckConstraint("CK_RecoveryRuns_Disposition_Domain", "\"Disposition\" IN ('Hold', 'Completed', 'Failed')");
                    table.CheckConstraint("CK_RecoveryRuns_Finished", " (\"Disposition\" = 'Hold') OR \"FinishedUtc\" IS NOT NULL ");
                    table.CheckConstraint("CK_RecoveryRuns_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_RecoveryRuns_NewGenerationId_Domain", "length(\"NewGenerationId\") = 36 AND \"NewGenerationId\" = lower(\"NewGenerationId\")");
                    table.CheckConstraint("CK_RecoveryRuns_SourceBackupId_Domain", "\"SourceBackupId\" IS NULL OR (length(\"SourceBackupId\") = 36 AND \"SourceBackupId\" = lower(\"SourceBackupId\"))");
                    table.CheckConstraint("CK_RecoveryRuns_Times", " \"FinishedUtc\" IS NULL OR \"FinishedUtc\" >= \"StartedUtc\" ");
                    table.ForeignKey(
                        name: "FK_RecoveryRuns_Backups_SourceBackupId",
                        column: x => x.SourceBackupId,
                        principalTable: "Backups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecoveryRuns_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.CheckConstraint("CK_UserRoles_RoleId_Domain", "length(\"RoleId\") = 36 AND \"RoleId\" = lower(\"RoleId\")");
                    table.CheckConstraint("CK_UserRoles_UserId_Domain", "length(\"UserId\") = 36 AND \"UserId\" = lower(\"UserId\")");
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Inventory",
                columns: table => new
                {
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    QuantityQ = table.Column<long>(type: "INTEGER", nullable: false),
                    ReorderQ = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventory", x => x.ProductId);
                    table.CheckConstraint("CK_Inventory_ProductId_Domain", "length(\"ProductId\") = 36 AND \"ProductId\" = lower(\"ProductId\")");
                    table.CheckConstraint("CK_Inventory_Reorder", " \"ReorderQ\" >= 0 ");
                    table.CheckConstraint("CK_Inventory_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_Inventory_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashMovements",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    AmountM = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Id);
                    table.CheckConstraint("CK_CashMovements_ActorUserId_Domain", "length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\")");
                    table.CheckConstraint("CK_CashMovements_Amount", " \"AmountM\" > 0 AND \"AmountM\" <= 100000000000 ");
                    table.CheckConstraint("CK_CashMovements_ApprovedByUserId_Domain", "\"ApprovedByUserId\" IS NULL OR (length(\"ApprovedByUserId\") = 36 AND \"ApprovedByUserId\" = lower(\"ApprovedByUserId\"))");
                    table.CheckConstraint("CK_CashMovements_Direction_Domain", "\"Direction\" IN ('In', 'Out')");
                    table.CheckConstraint("CK_CashMovements_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_CashMovements_Reason", "length(trim(\"Reason\")) > 0");
                    table.CheckConstraint("CK_CashMovements_RequestId_Domain", "length(\"RequestId\") = 36 AND \"RequestId\" = lower(\"RequestId\")");
                    table.CheckConstraint("CK_CashMovements_ShiftId_Domain", "length(\"ShiftId\") = 36 AND \"ShiftId\" = lower(\"ShiftId\")");
                    table.ForeignKey(
                        name: "FK_CashMovements_CashierShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "CashierShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InstallationState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    ProductionEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentCalendarPolicyId = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentCalculationPolicyId = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentBusinessProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    LastObservedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastFinancialBusinessDate = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallationState", x => x.Id);
                    table.CheckConstraint("CK_InstallationState_BusinessDate", " \"LastFinancialBusinessDate\" IS NULL OR (\"LastFinancialBusinessDate\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]')");
                    table.CheckConstraint("CK_InstallationState_CurrentBusinessProfileId_Domain", "\"CurrentBusinessProfileId\" IS NULL OR (length(\"CurrentBusinessProfileId\") = 36 AND \"CurrentBusinessProfileId\" = lower(\"CurrentBusinessProfileId\"))");
                    table.CheckConstraint("CK_InstallationState_CurrentCalculationPolicyId_Domain", "\"CurrentCalculationPolicyId\" IS NULL OR (length(\"CurrentCalculationPolicyId\") = 36 AND \"CurrentCalculationPolicyId\" = lower(\"CurrentCalculationPolicyId\"))");
                    table.CheckConstraint("CK_InstallationState_CurrentCalendarPolicyId_Domain", "\"CurrentCalendarPolicyId\" IS NULL OR (length(\"CurrentCalendarPolicyId\") = 36 AND \"CurrentCalendarPolicyId\" = lower(\"CurrentCalendarPolicyId\"))");
                    table.CheckConstraint("CK_InstallationState_InstallationId_Domain", "length(\"InstallationId\") = 36 AND \"InstallationId\" = lower(\"InstallationId\")");
                    table.CheckConstraint("CK_InstallationState_Mode_Domain", "\"Mode\" IN ('Normal', 'Maintenance', 'RecoveryRequired')");
                    table.CheckConstraint("CK_InstallationState_ProductionEnabled_Domain", "\"ProductionEnabled\" IN (0, 1)");
                    table.CheckConstraint("CK_InstallationState_Singleton", " \"Id\" = 1 ");
                    table.CheckConstraint("CK_InstallationState_Version", " \"Version\" >= 1 AND \"SchemaVersion\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_InstallationState_BusinessProfiles_CurrentBusinessProfileId",
                        column: x => x.CurrentBusinessProfileId,
                        principalTable: "BusinessProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstallationState_PolicyVersions_CurrentCalculationPolicyId",
                        column: x => x.CurrentCalculationPolicyId,
                        principalTable: "PolicyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstallationState_PolicyVersions_CurrentCalendarPolicyId",
                        column: x => x.CurrentCalendarPolicyId,
                        principalTable: "PolicyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    RangeStartDate = table.Column<string>(type: "TEXT", nullable: false),
                    RangeEndExclusiveDate = table.Column<string>(type: "TEXT", nullable: false),
                    CalendarPolicyId = table.Column<string>(type: "TEXT", nullable: false),
                    FiltersJson = table.Column<string>(type: "TEXT", nullable: false),
                    FiltersSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    LedgerWatermarkJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    GeneratedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRuns", x => x.Id);
                    table.CheckConstraint("CK_ReportRuns_CalendarPolicyId_Domain", "length(\"CalendarPolicyId\") = 36 AND \"CalendarPolicyId\" = lower(\"CalendarPolicyId\")");
                    table.CheckConstraint("CK_ReportRuns_GeneratedByUserId_Domain", "length(\"GeneratedByUserId\") = 36 AND \"GeneratedByUserId\" = lower(\"GeneratedByUserId\")");
                    table.CheckConstraint("CK_ReportRuns_Hashes", "length(\"FiltersSha256\") = 64 AND \"FiltersSha256\" NOT GLOB '*[^0-9a-f]*' AND length(\"SnapshotSha256\") = 64 AND \"SnapshotSha256\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_ReportRuns_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_ReportRuns_Range", "\"RangeStartDate\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]' AND \"RangeEndExclusiveDate\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]' AND \"RangeEndExclusiveDate\" > \"RangeStartDate\" ");
                    table.CheckConstraint("CK_ReportRuns_Revision", " \"Revision\" >= 1 ");
                    table.CheckConstraint("CK_ReportRuns_Type_Domain", "\"Type\" IN ('DailySales', 'PaymentSummary', 'ProductSales', 'InventoryStatus', 'CashierShift', 'MonthlySales', 'TransactionDetail')");
                    table.ForeignKey(
                        name: "FK_ReportRuns_PolicyVersions_CalendarPolicyId",
                        column: x => x.CalendarPolicyId,
                        principalTable: "PolicyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportRuns_Users_GeneratedByUserId",
                        column: x => x.GeneratedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxConfigurations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    TaxKey = table.Column<string>(type: "TEXT", nullable: false),
                    RatePpm = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceMode = table.Column<string>(type: "TEXT", nullable: false),
                    TreatmentCode = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxConfigurations", x => x.Id);
                    table.CheckConstraint("CK_TaxConfigurations_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_TaxConfigurations_PolicyVersionId_Domain", "length(\"PolicyVersionId\") = 36 AND \"PolicyVersionId\" = lower(\"PolicyVersionId\")");
                    table.CheckConstraint("CK_TaxConfigurations_PriceMode_Domain", "\"PriceMode\" IN ('Inclusive', 'Exclusive')");
                    table.CheckConstraint("CK_TaxConfigurations_Rate", " \"RatePpm\" BETWEEN 0 AND 1000000 ");
                    table.CheckConstraint("CK_TaxConfigurations_TaxKey", "length(trim(\"TaxKey\")) > 0");
                    table.CheckConstraint("CK_TaxConfigurations_TreatmentCode_Domain", "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax')");
                    table.ForeignKey(
                        name: "FK_TaxConfigurations_PolicyVersions_PolicyVersionId",
                        column: x => x.PolicyVersionId,
                        principalTable: "PolicyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LogicalKey = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    CorrectionId = table.Column<string>(type: "TEXT", nullable: true),
                    ReportRunId = table.Column<string>(type: "TEXT", nullable: true),
                    BackupId = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    MaterializedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactRecords", x => x.Id);
                    table.CheckConstraint("CK_ArtifactRecords_BackupId_Domain", "\"BackupId\" IS NULL OR (length(\"BackupId\") = 36 AND \"BackupId\" = lower(\"BackupId\"))");
                    table.CheckConstraint("CK_ArtifactRecords_CorrectionId_Domain", "\"CorrectionId\" IS NULL OR (length(\"CorrectionId\") = 36 AND \"CorrectionId\" = lower(\"CorrectionId\"))");
                    table.CheckConstraint("CK_ArtifactRecords_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_ArtifactRecords_LogicalKey", "length(trim(\"LogicalKey\")) > 0");
                    table.CheckConstraint("CK_ArtifactRecords_Materialized", "(\"State\" = 'Intent' AND \"Sha256\" IS NULL AND \"SizeBytes\" IS NULL AND \"MaterializedUtc\" IS NULL)\nOR (\"State\" = 'Ready' AND \"RelativePath\" IS NOT NULL AND \"Sha256\" IS NOT NULL AND \"SizeBytes\" IS NOT NULL\n    AND \"SizeBytes\" >= 0 AND \"MaterializedUtc\" IS NOT NULL\n    AND length(\"Sha256\") = 64 AND \"Sha256\" NOT GLOB '*[^0-9a-f]*')");
                    table.CheckConstraint("CK_ArtifactRecords_OrderId_Domain", "\"OrderId\" IS NULL OR (length(\"OrderId\") = 36 AND \"OrderId\" = lower(\"OrderId\"))");
                    table.CheckConstraint("CK_ArtifactRecords_ReportRunId_Domain", "\"ReportRunId\" IS NULL OR (length(\"ReportRunId\") = 36 AND \"ReportRunId\" = lower(\"ReportRunId\"))");
                    table.CheckConstraint("CK_ArtifactRecords_Source", "(\"Type\" = 'SaleJson' AND \"OrderId\" IS NOT NULL AND \"CorrectionId\" IS NULL AND \"ReportRunId\" IS NULL AND \"BackupId\" IS NULL AND \"SourceId\" = \"OrderId\")\nOR (\"Type\" = 'CorrectionJson' AND \"CorrectionId\" IS NOT NULL AND \"OrderId\" IS NULL AND \"ReportRunId\" IS NULL AND \"BackupId\" IS NULL AND \"SourceId\" = \"CorrectionId\")\nOR (\"Type\" IN ('ReportCsv', 'ReportPdf') AND \"ReportRunId\" IS NOT NULL AND \"OrderId\" IS NULL AND \"CorrectionId\" IS NULL AND \"BackupId\" IS NULL AND \"SourceId\" = \"ReportRunId\")\nOR (\"Type\" = 'Backup' AND \"BackupId\" IS NOT NULL AND \"OrderId\" IS NULL AND \"CorrectionId\" IS NULL AND \"ReportRunId\" IS NULL AND \"SourceId\" = \"BackupId\")");
                    table.CheckConstraint("CK_ArtifactRecords_SourceId_Domain", "length(\"SourceId\") = 36 AND \"SourceId\" = lower(\"SourceId\")");
                    table.CheckConstraint("CK_ArtifactRecords_State_Domain", "\"State\" IN ('Intent', 'Ready')");
                    table.CheckConstraint("CK_ArtifactRecords_Type_Domain", "\"Type\" IN ('SaleJson', 'CorrectionJson', 'ReportCsv', 'ReportPdf', 'Backup')");
                    table.ForeignKey(
                        name: "FK_ArtifactRecords_Backups_BackupId",
                        column: x => x.BackupId,
                        principalTable: "Backups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArtifactRecords_ReportRuns_ReportRunId",
                        column: x => x.ReportRunId,
                        principalTable: "ReportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncQueue",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ArtifactId = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationFolderKey = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteFileId = table.Column<string>(type: "TEXT", nullable: true),
                    AccountBindingId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncQueue", x => x.Id);
                    table.CheckConstraint("CK_SyncQueue_AccountBindingId_Domain", "length(\"AccountBindingId\") = 36 AND \"AccountBindingId\" = lower(\"AccountBindingId\")");
                    table.CheckConstraint("CK_SyncQueue_ArtifactId_Domain", "length(\"ArtifactId\") = 36 AND \"ArtifactId\" = lower(\"ArtifactId\")");
                    table.CheckConstraint("CK_SyncQueue_Counters", " \"AttemptCount\" >= 0 AND \"Version\" >= 1 ");
                    table.CheckConstraint("CK_SyncQueue_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_SyncQueue_Lease", "(\"State\" = 'InProgress' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresUtc\" IS NOT NULL)\nOR (\"State\" <> 'InProgress' AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresUtc\" IS NULL)");
                    table.CheckConstraint("CK_SyncQueue_State_Domain", "\"State\" IN ('Pending', 'InProgress', 'Succeeded', 'Failed', 'RecoveryHold')");
                    table.CheckConstraint("CK_SyncQueue_Succeeded", " \"State\" <> 'Succeeded' OR (\"RemoteFileId\" IS NOT NULL AND \"CompletedUtc\" IS NOT NULL) ");
                    table.ForeignKey(
                        name: "FK_SyncQueue_ArtifactRecords_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "ArtifactRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncHistory",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    QueueId = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptNo = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    HttpCode = table.Column<int>(type: "INTEGER", nullable: true),
                    SafeError = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteFileId = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncHistory", x => x.Id);
                    table.CheckConstraint("CK_SyncHistory_AttemptNo", " \"AttemptNo\" >= 1 ");
                    table.CheckConstraint("CK_SyncHistory_CorrelationId_Domain", "length(\"CorrelationId\") = 36 AND \"CorrelationId\" = lower(\"CorrelationId\")");
                    table.CheckConstraint("CK_SyncHistory_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_SyncHistory_Outcome", " (\"FinishedUtc\" IS NULL) = (\"Outcome\" IS NULL) ");
                    table.CheckConstraint("CK_SyncHistory_Outcome_Domain", "\"Outcome\" IS NULL OR (\"Outcome\" IN ('Succeeded', 'Failed', 'Unknown'))");
                    table.CheckConstraint("CK_SyncHistory_QueueId_Domain", "length(\"QueueId\") = 36 AND \"QueueId\" = lower(\"QueueId\")");
                    table.ForeignKey(
                        name: "FK_SyncHistory_SyncQueue_QueueId",
                        column: x => x.QueueId,
                        principalTable: "SyncQueue",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CheckoutRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    QuoteJson = table.Column<string>(type: "TEXT", nullable: false),
                    QuoteSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    PreparedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalEvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutRequests", x => x.Id);
                    table.CheckConstraint("CK_CheckoutRequests_ActorUserId_Domain", "length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\")");
                    table.CheckConstraint("CK_CheckoutRequests_Completion", "(\"State\" = 'Completed' AND \"OrderId\" IS NOT NULL AND \"ResolvedUtc\" IS NOT NULL)\nOR (\"State\" = 'Abandoned' AND \"OrderId\" IS NULL AND \"ResolvedUtc\" IS NOT NULL)\nOR (\"State\" = 'Prepared' AND \"OrderId\" IS NULL AND \"ResolvedUtc\" IS NULL)");
                    table.CheckConstraint("CK_CheckoutRequests_Fingerprint", "length(trim(\"RequestFingerprint\")) > 0");
                    table.CheckConstraint("CK_CheckoutRequests_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_CheckoutRequests_OrderId_Domain", "\"OrderId\" IS NULL OR (length(\"OrderId\") = 36 AND \"OrderId\" = lower(\"OrderId\"))");
                    table.CheckConstraint("CK_CheckoutRequests_QuoteSha256", "length(\"QuoteSha256\") = 64 AND \"QuoteSha256\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_CheckoutRequests_State_Domain", "\"State\" IN ('Prepared', 'Completed', 'Abandoned')");
                    table.CheckConstraint("CK_CheckoutRequests_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_CheckoutRequests_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CheckoutRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    TransactionNumber = table.Column<string>(type: "TEXT", nullable: false),
                    NumberSeriesKey = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderType = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    BusinessDate = table.Column<string>(type: "TEXT", nullable: false),
                    TimezoneId = table.Column<string>(type: "TEXT", nullable: false),
                    CutoffMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", nullable: false),
                    CurrencyExponent = table.Column<int>(type: "INTEGER", nullable: false),
                    CalculationPolicyId = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    NetM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountExTaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalM = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceBasisSubtotalM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountBasisM = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    LegalInvoiceNumber = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_Amounts", "\"NetM\" >= 0 AND \"DiscountExTaxM\" >= 0 AND \"TaxM\" >= 0 AND \"PriceBasisSubtotalM\" >= 0 AND \"DiscountBasisM\" >= 0 AND \"TotalM\" > 0 AND \"TotalM\" <= 100000000000 AND \"DiscountBasisM\" <= \"PriceBasisSubtotalM\" ");
                    table.CheckConstraint("CK_Orders_BusinessDate", "\"BusinessDate\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'");
                    table.CheckConstraint("CK_Orders_BusinessProfileId_Domain", "length(\"BusinessProfileId\") = 36 AND \"BusinessProfileId\" = lower(\"BusinessProfileId\")");
                    table.CheckConstraint("CK_Orders_CalculationPolicyId_Domain", "length(\"CalculationPolicyId\") = 36 AND \"CalculationPolicyId\" = lower(\"CalculationPolicyId\")");
                    table.CheckConstraint("CK_Orders_CashierUserId_Domain", "length(\"CashierUserId\") = 36 AND \"CashierUserId\" = lower(\"CashierUserId\")");
                    table.CheckConstraint("CK_Orders_CheckoutRequestId_Domain", "length(\"CheckoutRequestId\") = 36 AND \"CheckoutRequestId\" = lower(\"CheckoutRequestId\")");
                    table.CheckConstraint("CK_Orders_Currency", " length(\"CurrencyCode\") = 3 AND \"CurrencyExponent\" BETWEEN 0 AND 4 ");
                    table.CheckConstraint("CK_Orders_Cutoff", " \"CutoffMinute\" BETWEEN 0 AND 1439 ");
                    table.CheckConstraint("CK_Orders_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Orders_OrderType_Domain", "\"OrderType\" IN ('DineIn', 'Takeout')");
                    table.CheckConstraint("CK_Orders_Sequence", " \"Sequence\" BETWEEN 1 AND 999999 ");
                    table.CheckConstraint("CK_Orders_ShiftId_Domain", "\"ShiftId\" IS NULL OR (length(\"ShiftId\") = 36 AND \"ShiftId\" = lower(\"ShiftId\"))");
                    table.CheckConstraint("CK_Orders_Snapshot", "length(\"SnapshotSha256\") = 64 AND \"SnapshotSha256\" NOT GLOB '*[^0-9a-f]*' AND \"SnapshotSchemaVersion\" >= 1 ");
                    table.CheckConstraint("CK_Orders_State_Domain", "\"State\" IN ('Completed')");
                    table.CheckConstraint("CK_Orders_Times", " \"CompletedUtc\" >= \"CreatedUtc\" ");
                    table.CheckConstraint("CK_Orders_TotalIdentity", " \"TotalM\" = \"NetM\" + \"TaxM\" ");
                    table.CheckConstraint("CK_Orders_TransactionNumber", "length(trim(\"TransactionNumber\")) > 0");
                    table.ForeignKey(
                        name: "FK_Orders_BusinessProfiles_BusinessProfileId",
                        column: x => x.BusinessProfileId,
                        principalTable: "BusinessProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_CashierShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "CashierShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_CheckoutRequests_CheckoutRequestId",
                        column: x => x.CheckoutRequestId,
                        principalTable: "CheckoutRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_NumberSeries_NumberSeriesKey",
                        column: x => x.NumberSeriesKey,
                        principalTable: "NumberSeries",
                        principalColumn: "SeriesKey",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_PolicyVersions_CalculationPolicyId",
                        column: x => x.CalculationPolicyId,
                        principalTable: "PolicyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_Users_CashierUserId",
                        column: x => x.CashierUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    SkuSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    NameSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    UnitPriceM = table.Column<long>(type: "INTEGER", nullable: false),
                    QuantityQ = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitStepQ = table.Column<long>(type: "INTEGER", nullable: false),
                    TrackStockSnapshot = table.Column<bool>(type: "INTEGER", nullable: false),
                    PriceMode = table.Column<string>(type: "TEXT", nullable: false),
                    TreatmentCode = table.Column<string>(type: "TEXT", nullable: false),
                    PriceBasisM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountBasisM = table.Column<long>(type: "INTEGER", nullable: false),
                    NetBeforeDiscountM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountExTaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    NetM = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalM = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedPolicyJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItems", x => x.Id);
                    table.CheckConstraint("CK_OrderItems_Amounts", "\"PriceBasisM\" >= 0 AND \"DiscountBasisM\" >= 0 AND \"NetBeforeDiscountM\" >= 0 AND \"DiscountExTaxM\" >= 0 AND \"NetM\" >= 0 AND \"TaxM\" >= 0 AND \"TotalM\" >= 0");
                    table.CheckConstraint("CK_OrderItems_Discount", " \"DiscountBasisM\" <= \"PriceBasisM\" ");
                    table.CheckConstraint("CK_OrderItems_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_OrderItems_LineNo", " \"LineNo\" BETWEEN 1 AND 100 ");
                    table.CheckConstraint("CK_OrderItems_NetIdentity", " \"NetBeforeDiscountM\" = \"NetM\" + \"DiscountExTaxM\" ");
                    table.CheckConstraint("CK_OrderItems_OrderId_Domain", "length(\"OrderId\") = 36 AND \"OrderId\" = lower(\"OrderId\")");
                    table.CheckConstraint("CK_OrderItems_Price", " \"UnitPriceM\" > 0 AND \"UnitPriceM\" <= 100000000000 ");
                    table.CheckConstraint("CK_OrderItems_PriceMode_Domain", "\"PriceMode\" IN ('Inclusive', 'Exclusive')");
                    table.CheckConstraint("CK_OrderItems_ProductId_Domain", "length(\"ProductId\") = 36 AND \"ProductId\" = lower(\"ProductId\")");
                    table.CheckConstraint("CK_OrderItems_Quantity", " \"QuantityQ\" > 0 AND \"UnitStepQ\" IN (1, 1000) AND \"QuantityQ\" % \"UnitStepQ\" = 0 ");
                    table.CheckConstraint("CK_OrderItems_TotalIdentity", " \"TotalM\" = \"NetM\" + \"TaxM\" ");
                    table.CheckConstraint("CK_OrderItems_TrackStockSnapshot_Domain", "\"TrackStockSnapshot\" IN (0, 1)");
                    table.CheckConstraint("CK_OrderItems_TreatmentCode_Domain", "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax')");
                    table.ForeignKey(
                        name: "FK_OrderItems_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedM = table.Column<long>(type: "INTEGER", nullable: false),
                    TenderedM = table.Column<long>(type: "INTEGER", nullable: true),
                    ChangeM = table.Column<long>(type: "INTEGER", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    VerificationState = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    VerifiedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RecordedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    PaidUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_Applied", " \"AppliedM\" > 0 AND \"AppliedM\" <= 100000000000 ");
                    table.CheckConstraint("CK_Payments_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Payments_Method", "(\"Method\" = 'Cash' AND \"VerificationState\" = 'CashReceived'\n    AND \"TenderedM\" IS NOT NULL AND \"ChangeM\" IS NOT NULL\n    AND \"TenderedM\" >= \"AppliedM\" AND \"ChangeM\" = \"TenderedM\" - \"AppliedM\")\nOR (\"Method\" = 'GCash' AND \"VerificationState\" = 'Verified'\n    AND \"TenderedM\" IS NULL AND \"ChangeM\" IS NULL\n    AND \"VerifiedByUserId\" IS NOT NULL AND \"VerifiedUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_Payments_Method_Domain", "\"Method\" IN ('Cash', 'GCash')");
                    table.CheckConstraint("CK_Payments_OrderId_Domain", "length(\"OrderId\") = 36 AND \"OrderId\" = lower(\"OrderId\")");
                    table.CheckConstraint("CK_Payments_RecordedByUserId_Domain", "length(\"RecordedByUserId\") = 36 AND \"RecordedByUserId\" = lower(\"RecordedByUserId\")");
                    table.CheckConstraint("CK_Payments_VerificationState_Domain", "\"VerificationState\" IN ('CashReceived', 'Verified', 'CashPaidOut', 'ManuallyRefunded', 'NoTransfer')");
                    table.CheckConstraint("CK_Payments_VerifiedByUserId_Domain", "\"VerifiedByUserId\" IS NULL OR (length(\"VerifiedByUserId\") = 36 AND \"VerifiedByUserId\" = lower(\"VerifiedByUserId\"))");
                    table.ForeignKey(
                        name: "FK_Payments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Users_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Users_VerifiedByUserId",
                        column: x => x.VerifiedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemTaxComponents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OrderItemId = table.Column<string>(type: "TEXT", nullable: false),
                    ComponentKey = table.Column<string>(type: "TEXT", nullable: false),
                    RatePpm = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxableBaseM = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TreatmentCode = table.Column<string>(type: "TEXT", nullable: false),
                    RuleVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemTaxComponents", x => x.Id);
                    table.CheckConstraint("CK_ItemTaxComponents_Amounts", "\"TaxableBaseM\" >= 0 AND \"TaxM\" >= 0");
                    table.CheckConstraint("CK_ItemTaxComponents_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_ItemTaxComponents_Key", "length(trim(\"ComponentKey\")) > 0");
                    table.CheckConstraint("CK_ItemTaxComponents_OrderItemId_Domain", "length(\"OrderItemId\") = 36 AND \"OrderItemId\" = lower(\"OrderItemId\")");
                    table.CheckConstraint("CK_ItemTaxComponents_Rate", " \"RatePpm\" BETWEEN 0 AND 1000000 ");
                    table.CheckConstraint("CK_ItemTaxComponents_TreatmentCode_Domain", "\"TreatmentCode\" IN ('Unknown', 'Taxable', 'Exempt', 'ZeroRated', 'TestNoTax')");
                    table.ForeignKey(
                        name: "FK_ItemTaxComponents_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CorrectionItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectionId = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalOrderItemId = table.Column<string>(type: "TEXT", nullable: false),
                    QuantityQ = table.Column<long>(type: "INTEGER", nullable: false),
                    CumulativeReturnedQ = table.Column<long>(type: "INTEGER", nullable: false),
                    NetM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountExTaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountBasisM = table.Column<long>(type: "INTEGER", nullable: false),
                    StockDisposition = table.Column<string>(type: "TEXT", nullable: false),
                    StockReturnedQ = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectionItems", x => x.Id);
                    table.CheckConstraint("CK_CorrectionItems_Amounts", "\"NetM\" >= 0 AND \"DiscountExTaxM\" >= 0 AND \"TaxM\" >= 0 AND \"TotalM\" >= 0 AND \"DiscountBasisM\" >= 0");
                    table.CheckConstraint("CK_CorrectionItems_CorrectionId_Domain", "length(\"CorrectionId\") = 36 AND \"CorrectionId\" = lower(\"CorrectionId\")");
                    table.CheckConstraint("CK_CorrectionItems_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_CorrectionItems_OriginalOrderItemId_Domain", "length(\"OriginalOrderItemId\") = 36 AND \"OriginalOrderItemId\" = lower(\"OriginalOrderItemId\")");
                    table.CheckConstraint("CK_CorrectionItems_Quantity", " \"QuantityQ\" > 0 AND \"CumulativeReturnedQ\" >= \"QuantityQ\" ");
                    table.CheckConstraint("CK_CorrectionItems_Stock", "(\"StockDisposition\" = 'Return' AND \"StockReturnedQ\" > 0 AND \"StockReturnedQ\" <= \"QuantityQ\")\nOR (\"StockDisposition\" IN ('Discard', 'NotTracked') AND \"StockReturnedQ\" = 0)");
                    table.CheckConstraint("CK_CorrectionItems_StockDisposition_Domain", "\"StockDisposition\" IN ('Return', 'Discard', 'NotTracked')");
                    table.CheckConstraint("CK_CorrectionItems_TotalIdentity", " \"TotalM\" = \"NetM\" + \"TaxM\" ");
                    table.ForeignKey(
                        name: "FK_CorrectionItems_OrderItems_OriginalOrderItemId",
                        column: x => x.OriginalOrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CorrectionTaxComponents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectionItemId = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTaxComponentId = table.Column<string>(type: "TEXT", nullable: false),
                    TaxM = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectionTaxComponents", x => x.Id);
                    table.CheckConstraint("CK_CorrectionTaxComponents_Amount", " \"TaxM\" >= 0 ");
                    table.CheckConstraint("CK_CorrectionTaxComponents_CorrectionItemId_Domain", "length(\"CorrectionItemId\") = 36 AND \"CorrectionItemId\" = lower(\"CorrectionItemId\")");
                    table.CheckConstraint("CK_CorrectionTaxComponents_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_CorrectionTaxComponents_OriginalTaxComponentId_Domain", "length(\"OriginalTaxComponentId\") = 36 AND \"OriginalTaxComponentId\" = lower(\"OriginalTaxComponentId\")");
                    table.ForeignKey(
                        name: "FK_CorrectionTaxComponents_CorrectionItems_CorrectionItemId",
                        column: x => x.CorrectionItemId,
                        principalTable: "CorrectionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionTaxComponents_ItemTaxComponents_OriginalTaxComponentId",
                        column: x => x.OriginalTaxComponentId,
                        principalTable: "ItemTaxComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    DeltaQ = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    OrderItemId = table.Column<string>(type: "TEXT", nullable: true),
                    CorrectionItemId = table.Column<string>(type: "TEXT", nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    MovementKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    BalanceAfterQ = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.CheckConstraint("CK_InventoryMovements_ActorUserId_Domain", "length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\")");
                    table.CheckConstraint("CK_InventoryMovements_ApprovedByUserId_Domain", "\"ApprovedByUserId\" IS NULL OR (length(\"ApprovedByUserId\") = 36 AND \"ApprovedByUserId\" = lower(\"ApprovedByUserId\"))");
                    table.CheckConstraint("CK_InventoryMovements_CorrectionItemId_Domain", "\"CorrectionItemId\" IS NULL OR (length(\"CorrectionItemId\") = 36 AND \"CorrectionItemId\" = lower(\"CorrectionItemId\"))");
                    table.CheckConstraint("CK_InventoryMovements_Delta", " \"DeltaQ\" <> 0 ");
                    table.CheckConstraint("CK_InventoryMovements_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_InventoryMovements_MovementKey", "length(trim(\"MovementKey\")) > 0");
                    table.CheckConstraint("CK_InventoryMovements_OrderId_Domain", "\"OrderId\" IS NULL OR (length(\"OrderId\") = 36 AND \"OrderId\" = lower(\"OrderId\"))");
                    table.CheckConstraint("CK_InventoryMovements_OrderItemId_Domain", "\"OrderItemId\" IS NULL OR (length(\"OrderItemId\") = 36 AND \"OrderItemId\" = lower(\"OrderItemId\"))");
                    table.CheckConstraint("CK_InventoryMovements_ProductId_Domain", "length(\"ProductId\") = 36 AND \"ProductId\" = lower(\"ProductId\")");
                    table.CheckConstraint("CK_InventoryMovements_RequestId_Domain", "length(\"RequestId\") = 36 AND \"RequestId\" = lower(\"RequestId\")");
                    table.CheckConstraint("CK_InventoryMovements_Source", "(\"Type\" = 'Sale' AND \"OrderId\" IS NOT NULL AND \"OrderItemId\" IS NOT NULL AND \"CorrectionItemId\" IS NULL AND \"DeltaQ\" < 0)\nOR (\"Type\" = 'Return' AND \"CorrectionItemId\" IS NOT NULL AND \"OrderItemId\" IS NULL AND \"DeltaQ\" > 0)\nOR (\"Type\" IN ('Opening', 'Adjustment') AND \"OrderId\" IS NULL AND \"OrderItemId\" IS NULL AND \"CorrectionItemId\" IS NULL\n    AND \"Reason\" IS NOT NULL AND length(trim(\"Reason\")) > 0)");
                    table.CheckConstraint("CK_InventoryMovements_Type_Domain", "\"Type\" IN ('Opening', 'Sale', 'Adjustment', 'Return')");
                    table.ForeignKey(
                        name: "FK_InventoryMovements_CorrectionItems_CorrectionItemId",
                        column: x => x.CorrectionItemId,
                        principalTable: "CorrectionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CorrectionRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    IntendedItemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentEvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    PreparedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CorrectionId = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectionRequests", x => x.Id);
                    table.CheckConstraint("CK_CorrectionRequests_ActorUserId_Domain", "length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\")");
                    table.CheckConstraint("CK_CorrectionRequests_Completion", "(\"State\" = 'Completed' AND \"CorrectionId\" IS NOT NULL AND \"ResolvedUtc\" IS NOT NULL)\nOR (\"State\" = 'Abandoned' AND \"CorrectionId\" IS NULL AND \"ResolvedUtc\" IS NOT NULL)\nOR (\"State\" = 'Prepared' AND \"CorrectionId\" IS NULL AND \"ResolvedUtc\" IS NULL)");
                    table.CheckConstraint("CK_CorrectionRequests_CorrectionId_Domain", "\"CorrectionId\" IS NULL OR (length(\"CorrectionId\") = 36 AND \"CorrectionId\" = lower(\"CorrectionId\"))");
                    table.CheckConstraint("CK_CorrectionRequests_Fingerprint", "length(trim(\"Fingerprint\")) > 0");
                    table.CheckConstraint("CK_CorrectionRequests_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_CorrectionRequests_OriginalOrderId_Domain", "length(\"OriginalOrderId\") = 36 AND \"OriginalOrderId\" = lower(\"OriginalOrderId\")");
                    table.CheckConstraint("CK_CorrectionRequests_State_Domain", "\"State\" IN ('Prepared', 'Completed', 'Abandoned')");
                    table.CheckConstraint("CK_CorrectionRequests_Version", " \"Version\" >= 1 ");
                    table.ForeignKey(
                        name: "FK_CorrectionRequests_Orders_OriginalOrderId",
                        column: x => x.OriginalOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionRequests_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Corrections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectionRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectionNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    BusinessDate = table.Column<string>(type: "TEXT", nullable: false),
                    TimezoneId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: true),
                    NetM = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountExTaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxM = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalM = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Corrections", x => x.Id);
                    table.CheckConstraint("CK_Corrections_ActorUserId_Domain", "length(\"ActorUserId\") = 36 AND \"ActorUserId\" = lower(\"ActorUserId\")");
                    table.CheckConstraint("CK_Corrections_Amounts", "\"NetM\" >= 0 AND \"DiscountExTaxM\" >= 0 AND \"TaxM\" >= 0 AND \"TotalM\" >= 0 AND \"TotalM\" <= 100000000000 ");
                    table.CheckConstraint("CK_Corrections_ApprovedByUserId_Domain", "length(\"ApprovedByUserId\") = 36 AND \"ApprovedByUserId\" = lower(\"ApprovedByUserId\")");
                    table.CheckConstraint("CK_Corrections_BusinessDate", "\"BusinessDate\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'");
                    table.CheckConstraint("CK_Corrections_CorrectionRequestId_Domain", "length(\"CorrectionRequestId\") = 36 AND \"CorrectionRequestId\" = lower(\"CorrectionRequestId\")");
                    table.CheckConstraint("CK_Corrections_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_Corrections_Kind_Domain", "\"Kind\" IN ('Refund', 'Void')");
                    table.CheckConstraint("CK_Corrections_OriginalOrderId_Domain", "length(\"OriginalOrderId\") = 36 AND \"OriginalOrderId\" = lower(\"OriginalOrderId\")");
                    table.CheckConstraint("CK_Corrections_Reason", "length(trim(\"Reason\")) > 0");
                    table.CheckConstraint("CK_Corrections_ShiftId_Domain", "\"ShiftId\" IS NULL OR (length(\"ShiftId\") = 36 AND \"ShiftId\" = lower(\"ShiftId\"))");
                    table.CheckConstraint("CK_Corrections_Snapshot", "length(\"SnapshotSha256\") = 64 AND \"SnapshotSha256\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_Corrections_TotalIdentity", " \"TotalM\" = \"NetM\" + \"TaxM\" ");
                    table.ForeignKey(
                        name: "FK_Corrections_CashierShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "CashierShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Corrections_CorrectionRequests_CorrectionRequestId",
                        column: x => x.CorrectionRequestId,
                        principalTable: "CorrectionRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Corrections_Orders_OriginalOrderId",
                        column: x => x.OriginalOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Corrections_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Corrections_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    CorrectionId = table.Column<string>(type: "TEXT", nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    IsTestModel = table.Column<bool>(type: "INTEGER", nullable: false),
                    TemplateVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModelSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PrinterName = table.Column<string>(type: "TEXT", nullable: true),
                    SpoolJobId = table.Column<string>(type: "TEXT", nullable: true),
                    SubmittedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                    table.CheckConstraint("CK_PrintJobs_Attempts", " \"AttemptCount\" >= 0 AND \"TemplateVersion\" >= 1 ");
                    table.CheckConstraint("CK_PrintJobs_CorrectionId_Domain", "\"CorrectionId\" IS NULL OR (length(\"CorrectionId\") = 36 AND \"CorrectionId\" = lower(\"CorrectionId\"))");
                    table.CheckConstraint("CK_PrintJobs_CreatedByUserId_Domain", "length(\"CreatedByUserId\") = 36 AND \"CreatedByUserId\" = lower(\"CreatedByUserId\")");
                    table.CheckConstraint("CK_PrintJobs_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_PrintJobs_IsTestModel_Domain", "\"IsTestModel\" IN (0, 1)");
                    table.CheckConstraint("CK_PrintJobs_Model", "length(\"ModelSha256\") = 64 AND \"ModelSha256\" NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_PrintJobs_OrderId_Domain", "\"OrderId\" IS NULL OR (length(\"OrderId\") = 36 AND \"OrderId\" = lower(\"OrderId\"))");
                    table.CheckConstraint("CK_PrintJobs_Purpose_Domain", "\"Purpose\" IN ('Original', 'Reprint', 'Test')");
                    table.CheckConstraint("CK_PrintJobs_ReprintReason", " \"Purpose\" <> 'Reprint' OR (\"Reason\" IS NOT NULL AND length(trim(\"Reason\")) > 0) ");
                    table.CheckConstraint("CK_PrintJobs_RequestId_Domain", "length(\"RequestId\") = 36 AND \"RequestId\" = lower(\"RequestId\")");
                    table.CheckConstraint("CK_PrintJobs_Source", "(\"Purpose\" = 'Test' AND \"IsTestModel\" = 1 AND \"OrderId\" IS NULL AND \"CorrectionId\" IS NULL)\nOR (\"Purpose\" <> 'Test' AND \"IsTestModel\" = 0\n    AND ((\"OrderId\" IS NULL AND \"CorrectionId\" IS NOT NULL) OR (\"OrderId\" IS NOT NULL AND \"CorrectionId\" IS NULL)))");
                    table.CheckConstraint("CK_PrintJobs_State_Domain", "\"State\" IN ('Pending', 'Submitting', 'SpoolAccepted', 'Failed', 'OutcomeUnknown', 'OperatorConfirmed')");
                    table.ForeignKey(
                        name: "FK_PrintJobs_Corrections_CorrectionId",
                        column: x => x.CorrectionId,
                        principalTable: "Corrections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrintJobs_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrintJobs_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefundPayments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectionId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    AmountM = table.Column<long>(type: "INTEGER", nullable: false),
                    VerificationState = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedByUserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundPayments", x => x.Id);
                    table.CheckConstraint("CK_RefundPayments_Amount", " \"AmountM\" >= 0 AND \"AmountM\" <= 100000000000 ");
                    table.CheckConstraint("CK_RefundPayments_CorrectionId_Domain", "length(\"CorrectionId\") = 36 AND \"CorrectionId\" = lower(\"CorrectionId\")");
                    table.CheckConstraint("CK_RefundPayments_Evidence", "(\"AmountM\" = 0 AND \"VerificationState\" = 'NoTransfer')\nOR (\"AmountM\" > 0 AND \"Method\" = 'Cash' AND \"VerificationState\" = 'CashPaidOut')\nOR (\"AmountM\" > 0 AND \"Method\" = 'GCash' AND \"VerificationState\" = 'ManuallyRefunded')");
                    table.CheckConstraint("CK_RefundPayments_Id_Domain", "length(\"Id\") = 36 AND \"Id\" = lower(\"Id\")");
                    table.CheckConstraint("CK_RefundPayments_Method_Domain", "\"Method\" IN ('Cash', 'GCash')");
                    table.CheckConstraint("CK_RefundPayments_RecordedByUserId_Domain", "length(\"RecordedByUserId\") = 36 AND \"RecordedByUserId\" = lower(\"RecordedByUserId\")");
                    table.CheckConstraint("CK_RefundPayments_VerificationState_Domain", "\"VerificationState\" IN ('CashReceived', 'Verified', 'CashPaidOut', 'ManuallyRefunded', 'NoTransfer')");
                    table.CheckConstraint("CK_RefundPayments_VerifiedByUserId_Domain", "length(\"VerifiedByUserId\") = 36 AND \"VerifiedByUserId\" = lower(\"VerifiedByUserId\")");
                    table.ForeignKey(
                        name: "FK_RefundPayments_Corrections_CorrectionId",
                        column: x => x.CorrectionId,
                        principalTable: "Corrections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundPayments_Users_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundPayments_Users_VerifiedByUserId",
                        column: x => x.VerifiedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRecords_BackupId",
                table: "ArtifactRecords",
                column: "BackupId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRecords_CorrectionId",
                table: "ArtifactRecords",
                column: "CorrectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRecords_LogicalKey",
                table: "ArtifactRecords",
                column: "LogicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRecords_OrderId",
                table: "ArtifactRecords",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRecords_ReportRunId",
                table: "ArtifactRecords",
                column: "ReportRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ActorUserId",
                table: "AuditLogs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredUtc_Action",
                table: "AuditLogs",
                columns: new[] { "OccurredUtc", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_RequestId",
                table: "AuditLogs",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Backups_CreatedUtc",
                table: "Backups",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Backups_RelativePath",
                table: "Backups",
                column: "RelativePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessProfiles_Revision",
                table: "BusinessProfiles",
                column: "Revision",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSettings_UpdatedByUserId",
                table: "BusinessSettings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_CashierUserId_OpenedUtc",
                table: "CashierShifts",
                columns: new[] { "CashierUserId", "OpenedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_ClosedByUserId",
                table: "CashierShifts",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "UX_CashierShifts_OneOpenPerCashier",
                table: "CashierShifts",
                column: "CashierUserId",
                unique: true,
                filter: " \"ClosedUtc\" IS NULL ");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ActorUserId",
                table: "CashMovements",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ApprovedByUserId",
                table: "CashMovements",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_RequestId",
                table: "CashMovements",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ShiftId",
                table: "CashMovements",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_NormalizedName",
                table: "Categories",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutRequests_ActorUserId",
                table: "CheckoutRequests",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutRequests_OrderId",
                table: "CheckoutRequests",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutRequests_State_PreparedUtc",
                table: "CheckoutRequests",
                columns: new[] { "State", "PreparedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionItems_CorrectionId",
                table: "CorrectionItems",
                column: "CorrectionId");

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionItems_OriginalOrderItemId",
                table: "CorrectionItems",
                column: "OriginalOrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionRequests_ActorUserId",
                table: "CorrectionRequests",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionRequests_CorrectionId",
                table: "CorrectionRequests",
                column: "CorrectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionRequests_OriginalOrderId",
                table: "CorrectionRequests",
                column: "OriginalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_ActorUserId",
                table: "Corrections",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_ApprovedByUserId",
                table: "Corrections",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_BusinessDate_CreatedUtc",
                table: "Corrections",
                columns: new[] { "BusinessDate", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_CorrectionNumber",
                table: "Corrections",
                column: "CorrectionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_CorrectionRequestId",
                table: "Corrections",
                column: "CorrectionRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_OriginalOrderId",
                table: "Corrections",
                column: "OriginalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_ShiftId",
                table: "Corrections",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionTaxComponents_CorrectionItemId_OriginalTaxComponentId",
                table: "CorrectionTaxComponents",
                columns: new[] { "CorrectionItemId", "OriginalTaxComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionTaxComponents_OriginalTaxComponentId",
                table: "CorrectionTaxComponents",
                column: "OriginalTaxComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_OwnerUserId",
                table: "Drafts",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallationState_CurrentBusinessProfileId",
                table: "InstallationState",
                column: "CurrentBusinessProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallationState_CurrentCalculationPolicyId",
                table: "InstallationState",
                column: "CurrentCalculationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallationState_CurrentCalendarPolicyId",
                table: "InstallationState",
                column: "CurrentCalendarPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ActorUserId",
                table: "InventoryMovements",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ApprovedByUserId",
                table: "InventoryMovements",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_CorrectionItemId",
                table: "InventoryMovements",
                column: "CorrectionItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_MovementKey",
                table: "InventoryMovements",
                column: "MovementKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_OrderId",
                table: "InventoryMovements",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_OrderItemId",
                table: "InventoryMovements",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ProductId_CreatedUtc_Id",
                table: "InventoryMovements",
                columns: new[] { "ProductId", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemTaxComponents_OrderItemId_ComponentKey",
                table: "ItemTaxComponents",
                columns: new[] { "OrderItemId", "ComponentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberSeries_Prefix_BusinessDate",
                table: "NumberSeries",
                columns: new[] { "Prefix", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OrderId_LineNo",
                table: "OrderItems",
                columns: new[] { "OrderId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_ProductId_OrderId",
                table: "OrderItems",
                columns: new[] { "ProductId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessDate_CompletedUtc_Id",
                table: "Orders",
                columns: new[] { "BusinessDate", "CompletedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessProfileId",
                table: "Orders",
                column: "BusinessProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CalculationPolicyId",
                table: "Orders",
                column: "CalculationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CashierUserId_CompletedUtc",
                table: "Orders",
                columns: new[] { "CashierUserId", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CheckoutRequestId",
                table: "Orders",
                column: "CheckoutRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_NumberSeriesKey_Sequence",
                table: "Orders",
                columns: new[] { "NumberSeriesKey", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ShiftId",
                table: "Orders",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TransactionNumber",
                table: "Orders",
                column: "TransactionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId",
                table: "Payments",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RecordedByUserId",
                table: "Payments",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Reference",
                table: "Payments",
                column: "Reference");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_VerifiedByUserId",
                table: "Payments",
                column: "VerifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Key",
                table: "Permissions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyVersions_ApprovedByUserId",
                table: "PolicyVersions",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyVersions_Kind_Revision",
                table: "PolicyVersions",
                columns: new[] { "Kind", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_CreatedByUserId",
                table: "PrintJobs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_RequestId",
                table: "PrintJobs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_State",
                table: "PrintJobs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "UX_PrintJobs_OriginalPerCorrection",
                table: "PrintJobs",
                column: "CorrectionId",
                unique: true,
                filter: " \"Purpose\" = 'Original' AND \"CorrectionId\" IS NOT NULL ");

            migrationBuilder.CreateIndex(
                name: "UX_PrintJobs_OriginalPerOrder",
                table: "PrintJobs",
                column: "OrderId",
                unique: true,
                filter: " \"Purpose\" = 'Original' AND \"OrderId\" IS NOT NULL ");

            migrationBuilder.CreateIndex(
                name: "IX_Products_CategoryId",
                table: "Products",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_NormalizedSku",
                table: "Products",
                column: "NormalizedSku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryRuns_ActorUserId",
                table: "RecoveryRuns",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryRuns_SourceBackupId",
                table: "RecoveryRuns",
                column: "SourceBackupId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundPayments_CorrectionId",
                table: "RefundPayments",
                column: "CorrectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundPayments_RecordedByUserId",
                table: "RefundPayments",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundPayments_VerifiedByUserId",
                table: "RefundPayments",
                column: "VerifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_CalendarPolicyId",
                table: "ReportRuns",
                column: "CalendarPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_GeneratedByUserId",
                table: "ReportRuns",
                column: "GeneratedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_Type_RangeStartDate_RangeEndExclusiveDate_FiltersSha256_Revision",
                table: "ReportRuns",
                columns: new[] { "Type", "RangeStartDate", "RangeEndExclusiveDate", "FiltersSha256", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionId",
                table: "RolePermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Key",
                table: "Roles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncHistory_QueueId_AttemptNo",
                table: "SyncHistory",
                columns: new[] { "QueueId", "AttemptNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncQueue_ArtifactId",
                table: "SyncQueue",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncQueue_InProgressLease",
                table: "SyncQueue",
                column: "LeaseExpiresUtc",
                filter: " \"State\" = 'InProgress' ");

            migrationBuilder.CreateIndex(
                name: "IX_SyncQueue_State_NextAttemptUtc",
                table: "SyncQueue",
                columns: new[] { "State", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxConfigurations_PolicyVersionId_TaxKey",
                table: "TaxConfigurations",
                columns: new[] { "PolicyVersionId", "TaxKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ArtifactRecords_Corrections_CorrectionId",
                table: "ArtifactRecords",
                column: "CorrectionId",
                principalTable: "Corrections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ArtifactRecords_Orders_OrderId",
                table: "ArtifactRecords",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CheckoutRequests_Orders_OrderId",
                table: "CheckoutRequests",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CorrectionItems_Corrections_CorrectionId",
                table: "CorrectionItems",
                column: "CorrectionId",
                principalTable: "Corrections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CorrectionRequests_Corrections_CorrectionId",
                table: "CorrectionRequests",
                column: "CorrectionId",
                principalTable: "Corrections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Immutability guards (database design §Enforcement 2). Frozen SQL; see SchemaGuards.
            foreach (var statement in GuardSql.Load(GuardSql.BaselineFile))
            {
                migrationBuilder.Sql(statement);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CorrectionRequests_Corrections_CorrectionId",
                table: "CorrectionRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_CheckoutRequests_Orders_OrderId",
                table: "CheckoutRequests");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "BusinessSettings");

            migrationBuilder.DropTable(
                name: "CashMovements");

            migrationBuilder.DropTable(
                name: "CloudStorageSettings");

            migrationBuilder.DropTable(
                name: "CorrectionTaxComponents");

            migrationBuilder.DropTable(
                name: "Discounts");

            migrationBuilder.DropTable(
                name: "Drafts");

            migrationBuilder.DropTable(
                name: "InstallationState");

            migrationBuilder.DropTable(
                name: "Inventory");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "PrintJobs");

            migrationBuilder.DropTable(
                name: "RecoveryRuns");

            migrationBuilder.DropTable(
                name: "RefundPayments");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "ScheduledJobs");

            migrationBuilder.DropTable(
                name: "SyncHistory");

            migrationBuilder.DropTable(
                name: "TaxConfigurations");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "ItemTaxComponents");

            migrationBuilder.DropTable(
                name: "CorrectionItems");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "SyncQueue");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "OrderItems");

            migrationBuilder.DropTable(
                name: "ArtifactRecords");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Backups");

            migrationBuilder.DropTable(
                name: "ReportRuns");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "Corrections");

            migrationBuilder.DropTable(
                name: "CorrectionRequests");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "BusinessProfiles");

            migrationBuilder.DropTable(
                name: "CashierShifts");

            migrationBuilder.DropTable(
                name: "CheckoutRequests");

            migrationBuilder.DropTable(
                name: "NumberSeries");

            migrationBuilder.DropTable(
                name: "PolicyVersions");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
