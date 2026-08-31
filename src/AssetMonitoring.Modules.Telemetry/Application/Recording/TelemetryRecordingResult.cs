namespace AssetMonitoring.Modules.Telemetry.Application.Recording;

/// <summary>
/// Represents the result of recording a telemetry measurement.
/// </summary>
/// <param name="MeasurementId">
/// The identifier of the telemetry measurement.
/// </param>
/// <param name="Recorded">
/// Indicates whether a new telemetry measurement was persisted.
/// </param>
public sealed record TelemetryRecordingResult(Guid MeasurementId, bool Recorded);
