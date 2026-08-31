using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Contracts.Telemetry;

/// <summary>
/// Represents an HTTP request to record a device telemetry measurement.
/// </summary>
/// <param name="MeasurementId">
/// The unique identifier generated for the telemetry measurement.
/// </param>
/// <param name="Metric">
/// The type of telemetry metric being recorded.
/// </param>
/// <param name="NumericValue">
/// The numeric value for temperature or humidity measurements;
/// otherwise, <see langword="null"/>.
/// </param>
/// <param name="StateValue">
/// The state value for door or light measurements;
/// otherwise, <see langword="null"/>.
/// </param>
/// <param name="MeasuredAtUtc">
/// The UTC timestamp at which the device captured the measurement.
/// </param>
public sealed record TelemetryMeasurementRequest(Guid MeasurementId, TelemetryMetric Metric, double? NumericValue, bool? StateValue, DateTime MeasuredAtUtc);
