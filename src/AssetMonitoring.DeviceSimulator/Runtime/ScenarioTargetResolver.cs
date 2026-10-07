using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Resolves compatible active devices targeted by a simulation scenario.
/// </summary>
public sealed class ScenarioTargetResolver
{
    /// <summary>
    /// Selects compatible active devices according to the scenario target mode.
    /// </summary>
    /// <remarks>
    /// Supports high-temperature and high-humidity scenarios.
    /// Compatible devices are ordered by code using ordinal comparison.
    /// Specific device codes are matched case-sensitively.
    /// Random selection uses the supplied random source without replacement.
    /// </remarks>
    /// <param name="scenario">
    /// The validated scenario whose target devices are selected.
    /// </param>
    /// <param name="devices">
    /// The available devices with populated capability collections.
    /// </param>
    /// <param name="random">
    /// The simulation plan's random source, reused across scenario resolutions.
    /// </param>
    /// <returns>
    /// A nonempty collection of selected compatible devices.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any argument is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the target requires a missing device code
    /// or a missing or nonpositive device count.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no compatible devices exist, the specified device is
    /// unavailable or incompatible, or the requested count exceeds availability.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the scenario type or target mode is unsupported.
    /// </exception>
    public IReadOnlyList<SimulatorDeviceResponse> Resolve(ScenarioDefinition scenario, IReadOnlyList<SimulatorDeviceResponse> devices, Random random)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(random);

        var requiredCapability = GetRequiredCapability(scenario);

        var compatibleDevices = devices.Where(device => device.Lifecycle == SimulatorDeviceLifecycle.Active && (requiredCapability is null || device.Capabilities.Contains(requiredCapability.Value)))
            .OrderBy(device => device.Code, StringComparer.Ordinal).ToList();

        if (compatibleDevices.Count == 0)
        {
            throw new InvalidOperationException($"No compatible devices are available for scenario '{scenario.Name}'.");
        }

        if (scenario.Target.Mode == ScenarioTargetMode.All)
        {
            return compatibleDevices;
        }

        if (scenario.Target.Mode == ScenarioTargetMode.SpecificDevice)
        {
            if (string.IsNullOrWhiteSpace(scenario.Target.DeviceCode))
            {
                throw new ArgumentException("A device code is required for SpecificDevice targeting.", nameof(scenario));
            }

            var compatibleDevice = compatibleDevices.FirstOrDefault(device => device.Code == scenario.Target.DeviceCode);

            if (compatibleDevice is null)
            {
                throw new InvalidOperationException($"Device '{scenario.Target.DeviceCode}' is unavailable or incompatible "
                    + $"with scenario '{scenario.Name}'.");
            }

            return new[] { compatibleDevice };
        }

        if (scenario.Target.Mode == ScenarioTargetMode.RandomCompatible)
        {
            var count = scenario.Target.Count ?? 0;

            if (count <= 0)
            {
                throw new ArgumentException("A positive device count is required for RandomCompatible targeting.", nameof(scenario));
            }

            if (count > compatibleDevices.Count)
            {
                throw new InvalidOperationException($"Scenario '{scenario.Name}' requires {count} compatible devices, " +
                    $"but only {compatibleDevices.Count} are available.");
            }

            var availableDevices = compatibleDevices.ToList();
            var selectedDevices = new List<SimulatorDeviceResponse>(count);

            for (var i = 0; i < count; i++)
            {
                var index = random.Next(availableDevices.Count);

                selectedDevices.Add(availableDevices[index]);
                availableDevices.RemoveAt(index);
            }

            return selectedDevices;
        }

        throw new NotSupportedException($"Scenario target mode '{scenario.Target.Mode}' is not supported.");
    }

    /// <summary>
    /// Determines the device capability required by a supported scenario type.
    /// </summary>
    /// <param name="scenario">
    /// The scenario definition whose required device capability is determined.
    /// </param>
    /// <returns>
    /// <see cref="SimulatorDeviceCapability.Temperature"/> for a high-temperature
    /// scenario, or <see cref="SimulatorDeviceCapability.Humidity"/> for a
    /// high-humidity scenario.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the type of <paramref name="scenario"/> is not supported.
    /// </exception>
    private static SimulatorDeviceCapability? GetRequiredCapability(ScenarioDefinition scenario)
        => scenario switch
        {
            HighTemperatureScenarioDefinition => SimulatorDeviceCapability.Temperature,
            HighHumidityScenarioDefinition => SimulatorDeviceCapability.Humidity,
            _ => throw new NotSupportedException($"Scenario type '{scenario.GetType().Name}' is not supported.")
        };
}
