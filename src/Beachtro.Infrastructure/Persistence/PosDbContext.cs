using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>
/// Local SQLite ledger (D-10). Short-lived per operation; UI never holds one (T-19).
/// Model rules: INTEGER minor units/quantities/rates/UTC ms, lowercase canonical UUID TEXT,
/// enums as CHECK-constrained TEXT, booleans CHECK 0/1, RESTRICT deletes, immutability triggers.
/// </summary>
public sealed class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();
    public DbSet<TaxConfiguration> TaxConfigurations => Set<TaxConfiguration>();
    public DbSet<BusinessProfile> BusinessProfiles => Set<BusinessProfile>();
    public DbSet<BusinessSetting> BusinessSettings => Set<BusinessSetting>();
    public DbSet<InstallationState> InstallationState => Set<InstallationState>();
    public DbSet<CloudStorageSettings> CloudStorageSettings => Set<CloudStorageSettings>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<InventoryBalance> Inventory => Set<InventoryBalance>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<CashierShift> CashierShifts => Set<CashierShift>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<CheckoutRequest> CheckoutRequests => Set<CheckoutRequest>();
    public DbSet<NumberSeries> NumberSeries => Set<NumberSeries>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<ItemTaxComponent> ItemTaxComponents => Set<ItemTaxComponent>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CorrectionRequest> CorrectionRequests => Set<CorrectionRequest>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<CorrectionItem> CorrectionItems => Set<CorrectionItem>();
    public DbSet<CorrectionTaxComponent> CorrectionTaxComponents => Set<CorrectionTaxComponent>();
    public DbSet<RefundPayment> RefundPayments => Set<RefundPayment>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<ArtifactRecord> ArtifactRecords => Set<ArtifactRecord>();
    public DbSet<SyncQueueItem> SyncQueue => Set<SyncQueueItem>();
    public DbSet<SyncAttempt> SyncHistory => Set<SyncAttempt>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();
    public DbSet<RecoveryRun> RecoveryRuns => Set<RecoveryRun>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<Guid>().HaveConversion<LowercaseGuidConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        IdentityModel.Configure(modelBuilder);
        CatalogModel.Configure(modelBuilder);
        SalesModel.Configure(modelBuilder);
        OperationsModel.Configure(modelBuilder);
        ApplyColumnTypeChecks(modelBuilder);
    }

    /// <summary>Adds CHECKs every column of a given CLR type must satisfy.</summary>
    private static void ApplyColumnTypeChecks(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName();
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                string? rule = null;

                if (type == typeof(Guid))
                {
                    rule = $"length(\"{column}\") = 36 AND \"{column}\" = lower(\"{column}\")";
                }
                else if (type.IsEnum)
                {
                    rule = $"\"{column}\" IN ({string.Join(", ", Enum.GetNames(type).Select(n => $"'{n}'"))})";
                }
                else if (type == typeof(bool))
                {
                    rule = $"\"{column}\" IN (0, 1)";
                }
                else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
                {
                    throw new InvalidOperationException(
                        $"{table}.{column} uses {type.Name}. Financial and quantity data must be INTEGER (D-10).");
                }

                if (rule is not null)
                {
                    var sql = property.IsNullable ? $"\"{column}\" IS NULL OR ({rule})" : rule;
                    entity.AddCheckConstraint($"CK_{table}_{column}_Domain", sql);
                }
            }
        }
    }

    private sealed class LowercaseGuidConverter() : ValueConverter<Guid, string>(
        g => g.ToString("D"),
        s => Guid.ParseExact(s, "D"));
}
