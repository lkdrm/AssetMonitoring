using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Application.Interfaces;

/// <summary>
/// Provides persistence operations for telemetry measurements.
/// </summary>
public interface ITelemetryMeasurementRepository
{
    /// <summary>
    /// Asynchronously determines whether a telemetry measurement
    /// with the specified identifier already exists.
    /// </summary>
    /// <param name="measurementId">The telemetry measurement identifier.</param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the measurement exists;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    Task<bool> ExistsAsync(Guid measurementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a telemetry measurement to the persistence context.
    /// </summary>
    /// <param name="measurement">The telemetry measurement to add.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="measurement"/> is null.
    /// </exception>
    void Add(TelemetryMeasurement measurement);

    /// <summary>
    /// Asynchronously persists pending telemetry changes.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
