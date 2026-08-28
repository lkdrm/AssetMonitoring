using AssetMonitoring.Modules.DeviceManagement.Application.Activation;
using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Application.Repository;
using AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;
using AssetMonitoring.Modules.DeviceManagement.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Infrastructure;

/// <summary>
/// Provides dependency-injection registration for the Device Management module.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Device Management application and infrastructure services.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="connectionString">The SQL Server connection string for Device Management.</param>
    /// <returns>The same service collection for chained registration.</returns>
    public static IServiceCollection AddDeviceManagement(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<DeviceManagementDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<DeviceCatalogSynchronizationService>();
        services.AddScoped<IDeviceQueries, DeviceQueries>();
        services.AddScoped<DeviceActivationService>();

        services.AddSingleton<IDeviceCatalogReader, JsonDeviceCatalogReader>();
        services.AddSingleton<DeviceCatalogValidator>();
        services.AddSingleton<DeviceCatalogLoader>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        return services;
    }
}
