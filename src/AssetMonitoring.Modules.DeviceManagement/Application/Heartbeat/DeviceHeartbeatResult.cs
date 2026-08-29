namespace AssetMonitoring.Modules.DeviceManagement.Application.Heartbeat;

/// <summary>
/// Represents the result of recording a device heartbeat.
/// </summary>
/// <param name="DeviceId">
/// The identifier of the device that reported the heartbeat.
/// </param>
/// <param name="LastHeartbeatAtUtc">
/// The latest accepted heartbeat timestamp in UTC.
/// </param>
/// <param name="Changed">
/// Indicates whether the stored heartbeat timestamp was updated.
/// </param>
public sealed record DeviceHeartbeatResult(Guid DeviceId, DateTime LastHeartbeatAtUtc, bool Changed);
