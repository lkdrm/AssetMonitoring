using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Database;
using AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.Telemetry.Infrastructure;

/// <summary>
/// Provides dependency-injection registration for the Telemetry module.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Telemetry application and persistence services.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="connectionString">
    /// The SQL Server connection string used by the Telemetry module.
    /// </param>
    /// <returns>The same service collection for chained registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="connectionString"/> is missing.
    /// </exception>
    public static IServiceCollection AddTelemetry(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services, nameof(services));
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString, nameof(connectionString));

        services.AddDbContext<TelemetryDbContext>(options => options.UseSqlServer(connectionString));

        services.AddScoped<ITelemetryMeasurementRepository, TelemetryMeasurementRepository>();
        services.AddScoped<TelemetryRecordingService>();

        return services;
    }
}
