using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Tracks a high-humidity scenario for one simulated device.
/// </summary>
/// <remarks>
/// Selects an abnormal target once and delegates gradual humidity changes
/// to <see cref="GradualDeviation"/>.
/// Reuse the same instance for successive measurements of the same device
/// and scenario. Calls must be sequential and use nondecreasing elapsed time.
/// The definition is expected to have passed simulation plan validation.
/// Humidity values are percentages; rise and recovery limits are expressed
/// in percentage points per measurement.
public sealed class HighHumidityScenarioRuntime : IMeasurementScenarioRuntime
{
    private readonly Guid _deviceId;
    private readonly HighHumidityScenarioDefinition _definition;
    private readonly GradualDeviation _deviation;

    /// <summary>
    /// Gets the definition that configures this scenario instance.
    /// </summary>
    public HighHumidityScenarioDefinition Definition => _definition;

    /// <summary>
    /// Gets the identifier of the device affected by the scenario.
    /// </summary>
    public Guid DeviceId => _deviceId;

    /// <summary>
    /// Gets the current execution phase.
    /// The initial phase is <see cref="ScenarioPhase.Pending"/>.
    /// </summary>
    public ScenarioPhase Phase => _deviation.Phase;

    /// <summary>
    /// Gets the most recently calculated relative humidity as a percentage,
    /// or null before the first calculation.
    /// </summary>
    public double? CurrentHumidity => _deviation.CurrentValue;

    /// <inheritdoc />
    ScenarioDefinition IMeasurementScenarioRuntime.Definition => _definition;

    /// <inheritdoc />
    public SimulatorTelemetryMetric Metric => SimulatorTelemetryMetric.Humidity;

    /// <summary>
    /// Initializes a scenario runtime and selects its abnormal humidity
    /// target once.
    /// </summary>
    /// <param name="definition">
    /// The validated definition containing timing, abnormal humidity bounds,
    /// recovery settings, and per-measurement limits.
    /// </param>
    /// <param name="deviceId">
    /// The nonempty identifier of the device affected by the scenario.
    /// </param>
    /// <param name="random">
    /// The random source used to select the abnormal humidity target.
    /// Supply a seeded source when reproducible selection is required.
    /// The source is used during construction and is not retained.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="definition"/> or
    /// <paramref name="random"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    public HighHumidityScenarioRuntime(HighHumidityScenarioDefinition definition, Guid deviceId, Random random)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(random);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _definition = definition;

        var targetHumidity = definition.AbnormalMinimum + random.NextDouble() * (definition.AbnormalMaximum - definition.AbnormalMinimum);

        _deviation = new GradualDeviation(definition.StartsAfter, definition.Duration, definition.RecoveryDuration, targetHumidity, definition.MaximumRisePerMeasurement, definition.MaximumRecoveryPerMeasurement);
    }

    /// <summary>
    /// Calculates the next humidity value and updates the scenario phase.
    /// </summary>
    /// <remarks>
    /// Pending returns and saves the supplied normal humidity.
    /// The active phase raises the saved value toward the selected target,
    /// or retains a value already at or above that target.
    /// The active duration includes the gradual rise.
    /// Recovery captures a normal target on its first calculation.
    /// The maximum decrease per measurement takes priority over the planned
    /// recovery duration, so recovery can finish later than scheduled.
    /// The call that completes recovery returns the captured target;
    /// subsequent calls return fresh normal humidity values.
    /// Calculate humidity once per new measurement. HTTP retries must reuse
    /// the previously calculated measurement.
    /// </remarks>
    /// <param name="normalHumidity">
    /// The finite normal relative humidity for the current measurement,
    /// expressed as a percentage in the inclusive interval [0, 100].
    /// Supplies the initial active value when no previous value exists
    /// and the target on the first recovery calculation.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time on the scenario timeline,
    /// using the same origin as the definition's start delay.
    /// Successive calls must supply nondecreasing values.
    /// </param>
    /// <returns>
    /// The relative humidity to use for the current measurement,
    /// expressed as a percentage.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="normalHumidity"/> is not finite,
    /// is outside [0, 100], or <paramref name="elapsed"/> is negative.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when automatic recovery is disabled.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when recovery is reached and its duration is missing
    /// or is not greater than zero.
    /// </exception>
    public double GetNextHumidity(double normalHumidity, TimeSpan elapsed)
    {
        if (!double.IsFinite(normalHumidity))
        {
            throw new ArgumentOutOfRangeException(nameof(normalHumidity), normalHumidity, "Humidity must be a finite number.");
        }

        if (normalHumidity < 0 || normalHumidity > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(normalHumidity), normalHumidity, "Humidity must be between 0 and 100 percent.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (!_definition.AutoRecover)
        {
            throw new NotSupportedException("High-humidity scenarios without automatic recovery " + "are not supported yet.");
        }

        return _deviation.GetNextValue(normalHumidity, elapsed);
    }

    /// <inheritdoc />
    public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.Metric != Metric)
        {
            throw new ArgumentException($"The runtime applies only to {Metric} measurements.", nameof(measurement));
        }

        var normalHumidity = measurement.NumericValue ?? throw new InvalidOperationException("A humidity measurement must contain a numeric value.");
        return measurement with { NumericValue = GetNextHumidity(normalHumidity, elapsed) };
    }
}
