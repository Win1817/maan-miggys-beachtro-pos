using Beachtro.Application.Ports;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Beachtro.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBeachtroInfrastructure(this IServiceCollection services, LocalDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddDbContextFactory<PosDbContext>(options => PersistenceOptions.Configure(options, paths.DatabaseFile));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<WriteGate>();
        services.AddSingleton<Security.PosPasswordHasher>();
        services.AddSingleton<Application.Security.IIdentityService, Security.IdentityService>();
        services.AddSingleton<Application.Configuration.IProfileService, Configuration.ProfileService>();
        services.AddSingleton<Application.Security.SessionManager>();
        services.AddSingleton<Numbering.NumberSeriesService>();
        services.AddSingleton<Application.Recovery.INumberSeriesService>(sp => sp.GetRequiredService<Numbering.NumberSeriesService>());
        services.AddSingleton<Application.Catalog.ICatalogService, Catalog.CatalogService>();
        services.AddSingleton<Application.Sales.ICheckoutService, Sales.CheckoutService>();
        services.AddSingleton<Application.Sales.ICorrectionService, Sales.CorrectionService>();
        services.AddSingleton<Application.Recovery.IBackupService, Recovery.BackupService>();
        services.AddSingleton<Application.Sales.IOrderQueryService, Sales.OrderQueryService>();
        services.AddSingleton<Application.Receipts.IPrintService, Receipts.PrintService>();
        services.AddSingleton<Application.Configuration.IDemoDataService, Configuration.DemoDataService>();
        services.AddSingleton<Application.Shifts.IShiftService, Shifts.ShiftService>();
        services.AddSingleton<Reports.ReportService>();
        services.AddSingleton<Application.Reports.IReportService>(sp => sp.GetRequiredService<Reports.ReportService>());
        services.AddSingleton<Sync.ArtifactMaterializer>();
        services.AddSingleton<Sync.IRemoteStoreFactory, Sync.RemoteStoreFactory>();
        services.AddSingleton<Sync.SyncService>();
        services.AddSingleton<Application.Sync.ISyncService>(sp => sp.GetRequiredService<Sync.SyncService>());
        services.AddSingleton<Application.Sync.IJobScheduler, Sync.JobScheduler>();
        return services;
    }
}
