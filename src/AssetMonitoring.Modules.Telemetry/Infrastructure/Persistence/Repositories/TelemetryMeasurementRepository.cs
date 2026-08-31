using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Database;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Repositories;

/// <summary>
/// Provides EF Core persistence operations for telemetry measurements.
/// </summary>
internal sealed class TelemetryMeasurementRepository : ITelemetryMeasurementRepository
{
    private readonly TelemetryDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TelemetryMeasurementRepository"/> class.
    /// </summary>
    /// <param name="dbContext">The Telemetry database context.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dbContext"/> is null.
    /// </exception>
    public TelemetryMeasurementRepository(TelemetryDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext, nameof(dbContext));

        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public void Add(TelemetryMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        _dbContext.Measurements.Add(measurement);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid measurementId, CancellationToken cancellationToken = default) =>
        await _dbContext.Measurements.AnyAsync(measurement => measurement.Id == measurementId, cancellationToken);

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) => await _dbContext.SaveChangesAsync(cancellationToken);
}
