using Beachtro.Application.Ports;
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
        return services;
    }
}
