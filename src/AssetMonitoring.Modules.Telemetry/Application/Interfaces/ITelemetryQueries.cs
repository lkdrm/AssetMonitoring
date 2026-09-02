using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Application.Telemetry;

namespace AssetMonitoring.Modules.Telemetry.Application.Interfaces;

/// <summary>
/// Provides read-only telemetry measurement queries.
/// </summary>
public interface ITelemetryQueries
{
    /// <summary>
    /// Asynchronously retrieves a filtered and paginated telemetry history.
    /// </summary>
    /// <param name="query">
    /// The telemetry history filters and pagination parameters.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// A page containing matching telemetry measurements and pagination metadata.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="query"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier or UTC time range is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the page number or page size is outside the supported range.
    /// </exception>
    Task<TelemetryHistoryResult> GetHistoryAsync(TelemetryHistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the latest telemetry measurement for a device and
    /// metric.
    /// </summary>
    /// <param name="query">
    /// The device and metric used to locate the latest measurement.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// The latest matching measurement when one exists; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="query"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the telemetry metric is unsupported.
    /// </exception>
    Task<TelemetryMeasurementResponse?> GetLatestAsync(TelemetryLatestQuery query, CancellationToken cancellationToken = default);
}
