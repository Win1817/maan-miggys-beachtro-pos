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
        return services;
    }
}
