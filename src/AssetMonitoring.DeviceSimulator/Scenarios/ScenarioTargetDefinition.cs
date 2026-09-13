namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines how devices are selected for a simulation scenario.
/// </summary>
/// <param name="Mode">
/// The strategy used to select target devices.
/// </param>
/// <param name="Count">
/// The number of compatible devices to select when
/// <paramref name="Mode"/> is <see cref="ScenarioTargetMode.RandomCompatible"/>.
/// </param>
/// <param name="DeviceCode">
/// The target device code when
/// <paramref name="Mode"/> is <see cref="ScenarioTargetMode.SpecificDevice"/>.
/// </param>
public sealed record ScenarioTargetDefinition(ScenarioTargetMode Mode, int? Count = null, string? DeviceCode = null);
