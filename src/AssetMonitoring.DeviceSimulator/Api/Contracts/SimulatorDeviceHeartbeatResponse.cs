namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents the API response to a device heartbeat request.
/// </summary>
/// <param name="DeviceId">
/// The identifier of the device.
/// </param>
/// <param name="LastHeartbeatAtUtc">
/// The latest heartbeat timestamp stored by the server, expressed in UTC.
/// </param>
/// <param name="Changed">
/// Indicates whether the request updated the stored heartbeat timestamp.
/// </param>
public sealed record SimulatorDeviceHeartbeatResponse(Guid DeviceId, DateTime LastHeartbeatAtUtc, bool Changed);
