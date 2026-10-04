using AssetMonitoring.DeviceSimulator.Api;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Load;
using AssetMonitoring.DeviceSimulator.Preparation;
using AssetMonitoring.DeviceSimulator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetMonitoring.DeviceSimulator.Infrastructure;

/// <summary>
/// Provides dependency injection registration for device simulator services.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers simulation plan reading, validation, loading,
    /// API communication, device preparation, time provider,
    /// and heartbeat execution services as singletons.
    /// Configures the shared HTTP client with the API base address
    /// and a limited pooled connection lifetime.
    /// </summary>
    /// <param name="services">
    /// The service collection to add registrations to.
    /// </param>
    /// <returns>
    /// The service collection so that additional registrations can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is null.
    /// </exception>
    public static IServiceCollection AddDeviceSimulator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISimulationPlanReader, JsonSimulationPlanReader>();
        services.AddSingleton<SimulationPlanValidator>();
        services.AddSingleton<SimulationPlanLoader>();
        services.AddSingleton<HttpClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DeviceSimulatorOptions>>().Value;
            var baseAddress = new Uri(options.ApiBaseAddress, UriKind.Absolute);
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = baseAddress
            };
        });
        services.AddSingleton<IDeviceSimulatorApiClient, DeviceSimulatorApiClient>();
        services.AddSingleton<DevicePreparationService>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<DeviceHeartbeatRunner>();
        services.AddSingleton<DeviceHeartbeatCoordinator>();
        services.AddSingleton<NormalTelemetryGenerator>();
        services.AddSingleton<DeviceTelemetryRunner>();
        services.AddSingleton<DeviceTelemetryCoordinator>();
        services.AddSingleton<ScenarioTargetResolver>();
        services.AddSingleton<SimulationPlanResolver>();
        services.AddSingleton<DeviceScenarioScheduleFactory>();

        return services;
    }
}
