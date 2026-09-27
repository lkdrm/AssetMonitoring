using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Creates independent per-device temperature scenario sequences
/// from resolved scenario definitions and selected devices.
/// </summary>
/// <remarks>
/// Each call creates fresh runtime state for every scenario-device pair.
/// Prepare the sequences once before starting telemetry loops.
/// </remarks>
public sealed class DeviceTemperatureScenarioSequenceFactory
{
    /// <summary>
    /// Creates temperature scenario sequences keyed by device identifier.
    /// </summary>
    /// <param name="resolvedScenarios">
    /// The resolved scenarios in plan order, containing validated definitions
    /// and selected devices. Entries, definitions, and device collections
    /// are expected to be non-null.
    /// </param>
    /// <param name="random">
    /// The random source used sequentially to select abnormal targets.
    /// Identical inputs, input order, and initial random state allow
    /// reproducible target selection.
    /// </param>
    /// <returns>
    /// A dictionary containing one sequence for each selected device.
    /// Each sequence orders scenarios by their planned start time and
    /// preserves input order for equal start times.
    /// Returns an empty dictionary when no scenario-device pairs exist.
    /// </returns>
    /// <remarks>
    /// Each scenario-device pair receives a separate runtime.
    /// Devices shared by multiple scenarios receive one combined sequence.
    /// Target selection and plan validation are performed before this method.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when resolvedScenarios or random is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when a definition is not a high-temperature scenario.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a selected device has an empty identifier.
    /// </exception>
    public IReadOnlyDictionary<Guid, DeviceTemperatureScenarioSequence> Create(IReadOnlyList<ResolvedScenario> resolvedScenarios, Random random)
    {
        ArgumentNullException.ThrowIfNull(resolvedScenarios);
        ArgumentNullException.ThrowIfNull(random);

        var runtimesByDevice = new Dictionary<Guid, List<HighTemperatureScenarioRuntime>>();

        foreach (var resolvedScenario in resolvedScenarios)
        {
            if (resolvedScenario.Definition is not HighTemperatureScenarioDefinition definition)
            {
                throw new NotSupportedException($"Scenario type '{resolvedScenario.Definition.GetType().Name}' is not supported.");
            }

            foreach (var device in resolvedScenario.Devices)
            {
                if(!runtimesByDevice.TryGetValue(device.Id, out var deviceRuntimes))
                {
                    deviceRuntimes = new List<HighTemperatureScenarioRuntime>();
                    runtimesByDevice.Add(device.Id, deviceRuntimes);
                }

                var runtime = new HighTemperatureScenarioRuntime(definition, device.Id, random);
                deviceRuntimes.Add(runtime);
            }
        }

        var result = new Dictionary<Guid, DeviceTemperatureScenarioSequence>();

        foreach (var runtimeDevice in runtimesByDevice)
        {
            var deviceScenarioSequence = new DeviceTemperatureScenarioSequence(runtimeDevice.Key, runtimeDevice.Value);
            result.Add(runtimeDevice.Key, deviceScenarioSequence);
        }

        return result;
    }
}
