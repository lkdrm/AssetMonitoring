using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Executes a light-on scenario for one device, keeping the lights on
/// during the active phase and restoring normal measurements on completion.
/// </summary>
/// <remarks>
/// Each instance maintains independent state for one scenario-device pair.
/// Pending and completed phases preserve the incoming normal measurement.
/// The active phase produces a copy indicating that the lights are on.
/// Recovery is immediate and transitions directly to the completed phase.
/// Calls must be sequential and use nondecreasing elapsed simulation time.
/// The caller is responsible for supplying measurements from this device.
/// </remarks>
public sealed class LightOnScenarioRuntime : IMeasurementScenarioRuntime
{
    private readonly Guid _deviceId;
    private readonly LightOnScenarioDefinition _definition;

    /// <summary>
    /// Gets the definition that configures this scenario instance.
    /// </summary>
    public LightOnScenarioDefinition Definition => _definition;

    /// <summary>
    /// Gets the identifier of the device affected by the scenario.
    /// </summary>
    public Guid DeviceId => _deviceId;

    /// <inheritdoc />
    public SimulatorTelemetryMetric Metric => SimulatorTelemetryMetric.LightState;

    /// <summary>
    /// Gets the current execution phase.
    /// The initial phase is <see cref="ScenarioPhase.Pending"/>.
    /// </summary>
    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Pending;

    /// <summary>
    /// Gets the most recently produced light state,
    /// or null before the first measurement is processed.
    /// True indicates lights on; false indicates lights off.
    /// </summary>
    public bool? CurrentLightState { get; private set; }

    /// <inheritdoc />
    ScenarioDefinition IMeasurementScenarioRuntime.Definition => _definition;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="LightOnScenarioRuntime"/> class.
    /// </summary>
    /// <param name="definition">
    /// The validated configuration for this scenario instance.
    /// Automatic recovery must be enabled and the recovery duration must be null.
    /// </param>
    /// <param name="deviceId">
    /// The nonempty identifier of the device affected by the scenario.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="definition"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    public LightOnScenarioRuntime(LightOnScenarioDefinition definition, Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _definition = definition;
    }

    /// <summary>
    /// Validates a light-state measurement and the supported recovery mode
    /// before execution state is updated.
    /// </summary>
    /// <param name="measurement">The measurement supplied to the runtime.</param>
    /// <param name="elapsed">The nonnegative elapsed scenario time supplied by the caller.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="measurement"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the measurement metric is not light state.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the state value is absent or a numeric value is present.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="elapsed"/> is negative.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when automatic recovery is disabled.
    /// </exception>
    private void ValidateValues(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.Metric != Metric)
        {
            throw new ArgumentException($"The runtime applies only to {Metric} measurements.", nameof(measurement));
        }

        if (measurement.StateValue is null)
        {
            throw new InvalidOperationException("A light-state measurement must contain a state value.");
        }

        if (measurement.NumericValue is not null)
        {
            throw new InvalidOperationException("A light-state measurement must not contain a numeric value.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (!_definition.AutoRecover)
        {
            throw new NotSupportedException("Light-on scenarios without automatic recovery " + "are not supported yet.");
        }
    }

    /// <inheritdoc />
    public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
    {
        ValidateValues(measurement, elapsed);

        var normalState = measurement.StateValue ?? throw new InvalidOperationException("A light-state measurement must contain a state value.");

        if (Phase is ScenarioPhase.Completed)
        {
            return GetCompletedMeasurement(measurement, normalState);
        }

        if (elapsed < _definition.StartsAfter)
        {
            return GetPendingMeasurement(measurement, normalState);
        }

        if (elapsed < _definition.StartsAfter + _definition.Duration)
        {
            return GetActiveMeasurement(measurement);
        }

        return GetCompletedMeasurement(measurement, normalState);
    }

    /// <summary>
    /// Preserves normal light behavior while waiting for scenario activation.
    /// </summary>
    /// <param name="measurement">The validated normal light measurement.</param>
    /// <param name="normalState">The light state supplied by normal generation.</param>
    /// <returns>The original measurement.</returns>
    private SimulatorTelemetryMeasurementRequest GetPendingMeasurement(SimulatorTelemetryMeasurementRequest measurement, bool normalState)
    {
        Phase = ScenarioPhase.Pending;
        CurrentLightState = normalState;
        return measurement;
    }

    /// <summary>
    /// Produces a light-on measurement during the active phase.
    /// </summary>
    /// <param name="measurement">The validated normal light measurement.</param>
    /// <returns>
    /// A copy indicating lights on, preserving the remaining measurement properties.
    /// </returns>
    private SimulatorTelemetryMeasurementRequest GetActiveMeasurement(SimulatorTelemetryMeasurementRequest measurement)
    {
        Phase = ScenarioPhase.Active;
        CurrentLightState = true;
        return measurement with { StateValue = true };
    }

    /// <summary>
    /// Marks the scenario as completed and restores normal light behavior.
    /// </summary>
    /// <param name="measurement">The validated normal light measurement.</param>
    /// <param name="normalState">The light state supplied by normal generation.</param>
    /// <returns>The original measurement.</returns>
    private SimulatorTelemetryMeasurementRequest GetCompletedMeasurement(SimulatorTelemetryMeasurementRequest measurement, bool normalState)
    {
        Phase = ScenarioPhase.Completed;
        CurrentLightState = normalState;
        return measurement;
    }
}
