using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Resolves target devices for each scenario in a validated simulation plan.
/// </summary>
public sealed class SimulationPlanResolver
{
    private readonly ScenarioTargetResolver _targetResolver;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="SimulationPlanResolver"/> class.
    /// </summary>
    /// <param name="targetResolver">
    /// The resolver used to select compatible devices for each scenario.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="targetResolver"/> is null.
    /// </exception>
    public SimulationPlanResolver(ScenarioTargetResolver targetResolver)
    {
        ArgumentNullException.ThrowIfNull(targetResolver);

        _targetResolver = targetResolver;
    }

    /// <summary>
    /// Associates each scenario in the simulation plan with its selected devices.
    /// </summary>
    /// <remarks>
    /// Scenarios are resolved in plan order using one shared random source.
    /// When the plan specifies a seed, that seed initializes the random source.
    /// Target resolution failures propagate to the caller.
    /// </remarks>
    /// <param name="plan">
    /// The validated simulation plan containing the scenarios and optional seed.
    /// </param>
    /// <param name="devices">
    /// The available devices with populated capability collections.
    /// </param>
    /// <returns>
    /// The resolved scenarios in their original plan order.
    /// An empty collection when the plan contains no scenarios.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="plan"/> or
    /// <paramref name="devices"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a scenario target contains missing or invalid
    /// selection parameters.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a scenario has no compatible devices, its specific
    /// target is unavailable or incompatible, or its requested device
    /// count exceeds availability.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when a scenario type or target mode is unsupported.
    /// </exception>
    public IReadOnlyList<ResolvedScenario> Resolve(SimulationPlanDefinition plan, IReadOnlyList<SimulatorDeviceResponse> devices)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(devices);

        var random = plan.Seed.HasValue ? new Random(plan.Seed.Value) : new Random();

        var resolvedScenarios = new List<ResolvedScenario>();

        foreach (var scenario in plan.Scenarios)
        {
            var selectedDevices = _targetResolver.Resolve(scenario, devices, random);
            var resolveScenario = new ResolvedScenario(scenario, selectedDevices);

            resolvedScenarios.Add(resolveScenario);
        }

        return resolvedScenarios;
    }
}
