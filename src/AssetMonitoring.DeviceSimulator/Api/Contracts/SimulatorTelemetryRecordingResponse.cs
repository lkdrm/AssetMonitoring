namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents the API response to a telemetry recording request.
/// </summary>
/// <param name="MeasurementId">
/// The identifier of the telemetry measurement.
/// </param>
/// <param name="Recorded">
/// Indicates whether a new measurement was persisted.
/// False indicates that the measurement already existed.
/// </param>
public sealed record SimulatorTelemetryRecordingResponse(Guid MeasurementId, bool Recorded);
