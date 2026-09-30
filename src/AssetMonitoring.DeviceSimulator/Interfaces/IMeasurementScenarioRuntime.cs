using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Interfaces;

/// <summary>
/// Represents a stateful scenario that transforms measurements
/// of one metric for one simulated device.
/// </summary>
/// <remarks>
/// Reuse the same instance for successive measurements belonging
/// to the same device and scenario.
/// Calls must be sequential and use nondecreasing elapsed time.
/// Implementations calculate measurement values without sending
/// requests or scheduling delays.
/// </remarks>
public interface IMeasurementScenarioRuntime
{
    /// <summary>
    /// Gets the identifier of the device affected by the scenario.
    /// </summary>
    Guid DeviceId { get; }

    /// <summary>
    /// Gets the definition that configures this scenario instance.
    /// </summary>
    ScenarioDefinition Definition { get; }

    /// <summary>
    /// Gets the telemetry metric affected by the scenario.
    /// </summary>
    SimulatorTelemetryMetric Metric { get; }

    /// <summary>
    /// Gets the current execution phase of the scenario.
    /// </summary>
    ScenarioPhase Phase { get; }

    /// <summary>
    /// Applies the scenario to a measurement and updates its runtime state.
    /// </summary>
    /// <remarks>
    /// Apply the scenario once per new measurement.
    /// HTTP retries must reuse the returned measurement without calling
    /// this method again.
    /// The transformation must preserve the measurement identifier,
    /// metric, and UTC timestamp.
    /// The caller is responsible for selecting the runtime associated
    /// with the device that produced the measurement.
    /// </remarks>
    /// <param name="measurement">
    /// The non-null measurement containing the normal value to transform.
    /// Its metric must match <see cref="Metric"/>.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time on the scenario timeline,
    /// using the same origin as the definition's start delay.
    /// Successive calls must supply nondecreasing values.
    /// </param>
    /// <returns>
    /// The measurement with the scenario applied.
    /// The original measurement may be returned when no change is required.
    /// </returns>
    SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed);
}