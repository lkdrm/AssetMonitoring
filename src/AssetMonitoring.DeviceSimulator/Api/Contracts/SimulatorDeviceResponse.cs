namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents device information retrieved from the API
/// for simulation preparation.
/// </summary>
/// <param name="Id">
/// The device identifier used in API requests.
/// </param>
/// <param name="Code">
/// The device code used for scenario targeting and logging.
/// </param>
/// <param name="Name">
/// The human-readable device name.
/// </param>
/// <param name="Capabilities">
/// The measurement capabilities supported by the device.
/// </param>
/// <param name="Lifecycle">
/// The lifecycle state used to determine whether the device
/// requires activation or must be excluded from simulation.
/// </param>
public sealed record class SimulatorDeviceResponse(Guid Id, string Code, string Name, IReadOnlyList<SimulatorDeviceCapability> Capabilities, SimulatorDeviceLifecycle Lifecycle);
