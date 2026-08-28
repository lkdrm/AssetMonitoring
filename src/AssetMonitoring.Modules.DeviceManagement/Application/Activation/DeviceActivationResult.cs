using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Activation;

/// <summary>
/// Represents the result of a device monitoring activation request.
/// </summary>
/// <param name="DeviceId">The device identifier.</param>
/// <param name="Lifecycle">The resulting device lifecycle.</param>
/// <param name="Changed">Indicates whether the lifecycle was changed.</param>
public sealed record DeviceActivationResult(Guid DeviceId, DeviceLifecycle Lifecycle, bool Changed);