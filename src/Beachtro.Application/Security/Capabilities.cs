namespace Beachtro.Application.Security;

/// <summary>Capability keys from the security design capability matrix. Keys are persisted; never rename.</summary>
public static class Capabilities
{
    public const string PosSell = "pos.sell";
    public const string OrdersViewOwn = "orders.view.own";
    public const string OrdersViewAll = "orders.view.all";
    public const string ReceiptsPrint = "receipts.print";
    public const string ReceiptsReprint = "receipts.reprint";
    public const string ProductsManage = "products.manage";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryNegativeOverride = "inventory.negative.override";
    public const string DiscountsApply = "discounts.apply";
    public const string CorrectionsRefund = "corrections.refund";
    public const string CorrectionsVoid = "corrections.void";
    public const string ReportsViewExport = "reports.view.export";
    public const string ShiftsOwn = "shifts.own";
    public const string ShiftsManage = "shifts.manage";
    public const string CashMovements = "cash.movements";
    public const string AuditView = "audit.view";
    public const string SyncView = "sync.view";
    public const string SyncRetry = "sync.retry";
    public const string SyncConfigure = "sync.configure";
    public const string BackupCreateVerify = "backup.create.verify";
    public const string BackupRestore = "backup.restore";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string SettingsOperational = "settings.operational";
    public const string SettingsFinancial = "settings.financial";
    public const string ProductionActivate = "production.activate";

    public static IReadOnlyList<string> All { get; } =
    [
        PosSell, OrdersViewOwn, OrdersViewAll, ReceiptsPrint, ReceiptsReprint, ProductsManage, InventoryAdjust,
        InventoryNegativeOverride, DiscountsApply, CorrectionsRefund, CorrectionsVoid, ReportsViewExport, ShiftsOwn,
        ShiftsManage, CashMovements, AuditView, SyncView, SyncRetry, SyncConfigure, BackupCreateVerify, BackupRestore,
        UsersManage, RolesManage, SettingsOperational, SettingsFinancial, ProductionActivate,
    ];
}

/// <summary>
/// Default permission templates (security design matrix). These are application role templates,
/// not staff positions. Accounts and grants are confirmed before production (D-14).
/// </summary>
public static class RoleTemplates
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Cashier = "cashier";
    public const string Administrator = "administrator";

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Grants { get; } = new Dictionary<string, IReadOnlySet<string>>
    {
        [Cashier] = new HashSet<string>
        {
            Capabilities.PosSell, Capabilities.OrdersViewOwn, Capabilities.ReceiptsPrint, Capabilities.ShiftsOwn, Capabilities.SyncView,
        },
        [Manager] = new HashSet<string>
        {
            Capabilities.PosSell, Capabilities.OrdersViewOwn, Capabilities.OrdersViewAll, Capabilities.ReceiptsPrint,
            Capabilities.ReceiptsReprint, Capabilities.ProductsManage, Capabilities.InventoryAdjust, Capabilities.DiscountsApply,
            Capabilities.CorrectionsRefund, Capabilities.ReportsViewExport, Capabilities.ShiftsOwn, Capabilities.ShiftsManage,
            Capabilities.CashMovements, Capabilities.SyncView, Capabilities.SyncRetry, Capabilities.SettingsOperational,
        },
        [Administrator] = new HashSet<string>
        {
            Capabilities.AuditView, Capabilities.SyncView, Capabilities.SyncRetry, Capabilities.SyncConfigure,
            Capabilities.BackupCreateVerify, Capabilities.BackupRestore, Capabilities.UsersManage, Capabilities.SettingsOperational,
        },
        [Owner] = new HashSet<string>(Capabilities.All),
    };

    public static IReadOnlyDictionary<string, string> Names { get; } = new Dictionary<string, string>
    {
        [Owner] = "Owner",
        [Manager] = "Manager",
        [Cashier] = "Cashier",
        [Administrator] = "Administrator",
    };
}
