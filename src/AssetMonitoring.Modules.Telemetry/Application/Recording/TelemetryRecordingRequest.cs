using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Application.Recording;

/// <summary>
/// Represents a request to record a telemetry measurement.
/// </summary>
/// <param name="MeasurementId">
/// The unique identifier supplied by the telemetry message.
/// </param>
/// <param name="DeviceId">
/// The identifier of the device that produced the measurement.
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
public sealed record TelemetryRecordingRequest(Guid MeasurementId, Guid DeviceId, TelemetryMetric Metric, double? NumericValue, bool? StateValue, DateTime MeasuredAtUtc);
