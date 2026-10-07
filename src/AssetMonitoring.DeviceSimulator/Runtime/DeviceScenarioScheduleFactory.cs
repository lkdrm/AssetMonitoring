using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Creates per-device scenario schedules from resolved definitions
/// and their selected target devices.
/// </summary>
/// <remarks>
/// Each preparation creates fresh runtime state for every scenario-device pair.
/// A device receives a separate sequence for each affected telemetry metric.
/// Prepare schedules once before starting device telemetry loops.
/// </remarks>
public sealed class DeviceScenarioScheduleFactory
{
    /// <summary>
    /// Creates scenario schedules keyed by device identifier.
    /// </summary>
    /// <remarks>
    /// Creates runtimes in resolved plan order and selected device order
    /// before grouping them by device and metric.
    /// This preserves the order in which random targets are selected.
    /// Each metric sequence orders scenarios by their configured start delays
    /// and preserves input order when those delays are equal.
    /// Target resolution and plan validation are performed before this method.
    /// The factory supports high-temperature and high-humidity definitions.
    /// </remarks>
    /// <param name="resolvedScenarios">
    /// The resolved scenarios in plan order, containing validated definitions
    /// and selected compatible devices.
    /// Entries, definitions, device collections, and devices are expected
    /// to be non-null.
    /// </param>
    /// <param name="random">
    /// The random source used sequentially to select each runtime's target.
    /// Identical inputs, input order, and initial random state allow
    /// reproducible target selection.
    /// </param>
    /// <returns>
    /// A dictionary containing one schedule for each selected device identifier.
    /// Each schedule contains one sequence per affected metric.
    /// An empty plan or supported scenarios without selected devices
    /// produce an empty dictionary.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="resolvedScenarios"/> or
    /// <paramref name="random"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when a definition has an unsupported scenario type,
    /// including when that definition has no selected devices.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a selected device has an empty identifier.
    /// </exception>
    public IReadOnlyDictionary<Guid, DeviceScenarioSchedule> Create(IReadOnlyList<ResolvedScenario> resolvedScenarios, Random random)
    {
        ArgumentNullException.ThrowIfNull(resolvedScenarios);
        ArgumentNullException.ThrowIfNull(random);

        var runtimesByDevice = new Dictionary<Guid, List<IMeasurementScenarioRuntime>>();

        foreach (var resolvedScenario in resolvedScenarios)
        {
            if (resolvedScenario.Definition is not HighTemperatureScenarioDefinition && resolvedScenario.Definition is not HighHumidityScenarioDefinition)
            {
                throw new NotSupportedException($"Scenario type '{resolvedScenario.Definition.GetType().Name}' is not supported.");
            }

            foreach (var device in resolvedScenario.Devices)
            {
                var runtime = CreateRuntime(resolvedScenario.Definition, device.Id, random);

                if (!runtimesByDevice.TryGetValue(device.Id, out var deviceRuntimes))
                {
                    deviceRuntimes = new List<IMeasurementScenarioRuntime>();
                    runtimesByDevice.Add(device.Id, deviceRuntimes);
                }

                deviceRuntimes.Add(runtime);
            }
        }

        var result = new Dictionary<Guid, DeviceScenarioSchedule>();

        foreach (var (deviceId, runtimes) in runtimesByDevice)
        {
            var sequences = runtimes
                .GroupBy(runtime => runtime.Metric)
                .Select(group => new DeviceMetricScenarioSequence(deviceId, group.Key, group.ToArray()));

            result.Add(deviceId, new DeviceScenarioSchedule(deviceId, sequences));
        }

        return result;
    }

    /// <summary>
    /// Creates a runtime matching the concrete definition type
    /// for one selected device.
    /// </summary>
    /// <param name="definition">
    /// The non-null validated definition used to choose and configure the runtime.
    /// </param>
    /// <param name="deviceId">
    /// The nonempty identifier of the device affected by the runtime.
    /// </param>
    /// <param name="random">
    /// The non-null random source used to select the runtime's target.
    /// </param>
    /// <returns>
    /// A new independent runtime implementing the common measurement contract.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when no runtime is available for the definition type.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    private static IMeasurementScenarioRuntime CreateRuntime(ScenarioDefinition definition, Guid deviceId, Random random)
        => definition switch
        {
            HighTemperatureScenarioDefinition highTemperature => new HighTemperatureScenarioRuntime(highTemperature, deviceId, random),
            HighHumidityScenarioDefinition highHumidity => new HighHumidityScenarioRuntime(highHumidity, deviceId, random),
            _ => throw new NotSupportedException($"Scenario type '{definition.GetType().Name}' is not supported.")
        };
}
