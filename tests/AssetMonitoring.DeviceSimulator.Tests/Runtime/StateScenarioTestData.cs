using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>Provides valid inputs shared by door-open and light-on behavioral tests.</summary>
internal static class StateScenarioTestData
{
    /// <summary>Gets a stable device identifier for tests that need one device.</summary>
    internal static readonly Guid DeviceId = Guid.Parse("70000000-0000-0000-0000-000000000001");

    /// <summary>Gets the default one-minute activation delay.</summary>
    internal static readonly TimeSpan Start = TimeSpan.FromMinutes(1);

    /// <summary>Gets the default two-minute active duration.</summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    /// <summary>Gets a stable UTC timestamp for direct runtime measurements.</summary>
    internal static readonly DateTime MeasuredAtUtc = new(2026, 10, 19, 22, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a definition with immediate recovery and configurable timing.</summary>
    /// <param name="metric">The door-state or light-state metric.</param>
    /// <param name="name">The optional unique scenario name.</param>
    /// <param name="startsAfter">The optional activation delay.</param>
    /// <param name="duration">The optional active duration.</param>
    /// <param name="autoRecover">Whether automatic recovery is enabled.</param>
    /// <returns>A definition belonging to the requested state metric.</returns>
    internal static ScenarioDefinition CreateDefinition(SimulatorTelemetryMetric metric,
        string? name = null, TimeSpan? startsAfter = null, TimeSpan? duration = null,
        bool autoRecover = true) => metric switch
        {
            SimulatorTelemetryMetric.DoorState => new DoorOpenScenarioDefinition(
                name ?? "DoorOpen", startsAfter ?? Start, duration ?? Duration,
                new ScenarioTargetDefinition(ScenarioTargetMode.All), autoRecover, null),
            SimulatorTelemetryMetric.LightState => new LightOnScenarioDefinition(
                name ?? "LightOn", startsAfter ?? Start, duration ?? Duration,
                new ScenarioTargetDefinition(ScenarioTargetMode.All), autoRecover, null),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };

    /// <summary>Creates a fresh observable runtime for one state definition and device.</summary>
    /// <param name="definition">The door-open or light-on definition.</param>
    /// <param name="deviceId">The optional owning device identifier.</param>
    /// <returns>A fresh production runtime.</returns>
    internal static IMeasurementScenarioRuntime CreateRuntime(ScenarioDefinition definition,
        Guid? deviceId = null) => definition switch
        {
            DoorOpenScenarioDefinition door => new DoorOpenScenarioRuntime(door, deviceId ?? DeviceId),
            LightOnScenarioDefinition light => new LightOnScenarioRuntime(light, deviceId ?? DeviceId),
            _ => throw new ArgumentException("A state scenario is required.", nameof(definition))
        };

    /// <summary>Reads the observable state without accessing private runtime implementation.</summary>
    /// <param name="runtime">The state runtime to inspect.</param>
    /// <returns>The most recently produced state, or null before execution.</returns>
    internal static bool? CurrentState(IMeasurementScenarioRuntime runtime) => runtime switch
    {
        DoorOpenScenarioRuntime door => door.CurrentDoorState,
        LightOnScenarioRuntime light => light.CurrentLightState,
        _ => throw new ArgumentException("A state runtime is required.", nameof(runtime))
    };

    /// <summary>Creates a measurement with configurable payload fields.</summary>
    /// <param name="metric">The measurement metric.</param>
    /// <param name="state">The optional boolean payload.</param>
    /// <param name="numeric">The optional numeric payload.</param>
    /// <returns>A measurement with a fresh identifier and stable UTC timestamp.</returns>
    internal static SimulatorTelemetryMeasurementRequest CreateMeasurement(SimulatorTelemetryMetric metric,
        bool? state = false, double? numeric = null) =>
        new(Guid.NewGuid(), metric, numeric, state, MeasuredAtUtc);

    /// <summary>Creates an active device with supplied or default state capabilities.</summary>
    /// <param name="number">The numeric suffix used for its identifier and code.</param>
    /// <param name="capabilities">The capabilities, defaulting to both state metrics.</param>
    /// <returns>A fixture-owned device.</returns>
    internal static SimulatorDeviceResponse CreateDevice(int number = 1,
        params SimulatorDeviceCapability[] capabilities) => new(
        Guid.Parse($"70000000-0000-0000-0000-{number:D12}"), $"STATE-{number:D3}",
        $"State sensor {number}", capabilities.Length == 0
            ? new[] { SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.LightState }
            : capabilities, SimulatorDeviceLifecycle.Active);

    /// <summary>Builds a schedule retaining the supplied observable runtime instances.</summary>
    /// <param name="deviceId">The device owning every runtime.</param>
    /// <param name="runtimes">The runtimes grouped into independent metric sequences.</param>
    /// <returns>A production schedule.</returns>
    internal static DeviceScenarioSchedule CreateSchedule(Guid deviceId,
        params IMeasurementScenarioRuntime[] runtimes) => new(deviceId,
        runtimes.GroupBy(runtime => runtime.Metric).Select(group =>
            new DeviceMetricScenarioSequence(deviceId, group.Key, group.ToArray())));

    /// <summary>Supplies a fixed midpoint for abnormal numeric target selection.</summary>
    internal sealed class MidpointRandom : Random
    {
        /// <inheritdoc />
        public override double NextDouble() => 0.5;
    }
}
