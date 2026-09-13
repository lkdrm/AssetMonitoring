namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents the device activation result returned by the API.
/// </summary>
/// <param name="DeviceId">
/// The identifier of the device whose activation was requested.
/// </param>
/// <param name="Lifecycle">
/// The resulting device lifecycle.
/// </param>
/// <param name="Changed">
/// Indicates whether activation changed the lifecycle.
/// False also represents success when the device was already active.
/// </param>
public sealed record SimulatorDeviceActivationResponse(Guid DeviceId, SimulatorDeviceLifecycle Lifecycle, bool Changed);
