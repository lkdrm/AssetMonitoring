using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Tracks a high-temperature scenario for one simulated device.
/// </summary>
/// <remarks>
/// Selects an abnormal target once and delegates gradual temperature
/// changes to a shared numeric calculation component.
/// Reuse the same runtime instance for successive measurements
/// of the same device and scenario.
/// Calls must be sequential and use nondecreasing elapsed time.
/// The definition is expected to have passed simulation plan validation.
/// </remarks>
public sealed class HighTemperatureScenarioRuntime : IMeasurementScenarioRuntime
{
    private readonly HighTemperatureScenarioDefinition _definition;
    private readonly Guid _deviceId;
    private readonly GradualDeviation _deviation;

    /// <summary>
    /// Gets the definition that configures this scenario instance.
    /// </summary>
    public HighTemperatureScenarioDefinition Definition => _definition;

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
    /// Gets the most recently calculated temperature in degrees Celsius,
    /// or null before the first calculation.
    /// </summary>
    public double? CurrentTemperature => _deviation.CurrentValue;

    /// <inheritdoc />
    ScenarioDefinition IMeasurementScenarioRuntime.Definition => _definition;

    /// <inheritdoc />
    public SimulatorTelemetryMetric Metric => SimulatorTelemetryMetric.Temperature;

    /// <summary>
    /// Initializes a scenario runtime and selects its abnormal
    /// target temperature once.
    /// </summary>
    /// <param name="definition">
    /// The validated definition containing timing, target temperature
    /// bounds, recovery settings, and per-measurement limits.
    /// </param>
    /// <param name="deviceId">
    /// The nonempty identifier of the device affected by the scenario.
    /// </param>
    /// <param name="random">
    /// The random source used to select the abnormal target temperature.
    /// Supply a seeded source when reproducible selection is required.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="definition"/> or
    /// <paramref name="random"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    public HighTemperatureScenarioRuntime(HighTemperatureScenarioDefinition definition, Guid deviceId, Random random)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(random);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        _definition = definition;
        _deviceId = deviceId;

        var targetTemperature = definition.AbnormalMinimum + random.NextDouble() * (definition.AbnormalMaximum - definition.AbnormalMinimum);

        _deviation = new GradualDeviation(definition.StartsAfter, definition.Duration, definition.RecoveryDuration, targetTemperature, definition.MaximumRisePerMeasurement, definition.MaximumRecoveryPerMeasurement);
    }

    /// <summary>
    /// Calculates the next temperature and updates the scenario phase.
    /// </summary>
    /// <remarks>
    /// Pending and completed phases return the supplied normal temperature.
    /// The active phase raises the temperature toward the selected target.
    /// Recovery captures a normal target once and respects the maximum
    /// decrease per measurement, even when this extends its duration.
    /// Calculate the temperature once per new measurement;
    /// HTTP retries must reuse the previously calculated measurement.
    /// </remarks>
    /// <param name="normalTemperature">
    /// The finite normal temperature for the current measurement,
    /// in degrees Celsius.
    /// Supplies the initial value when no previous temperature exists
    /// and the target on the first recovery calculation.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time on the scenario timeline,
    /// using the same origin as the definition's start delay.
    /// Successive calls must supply nondecreasing values.
    /// </param>
    /// <returns>
    /// The temperature to use for the current measurement,
    /// in degrees Celsius.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="normalTemperature"/> is not finite
    /// or <paramref name="elapsed"/> is negative.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when automatic recovery is disabled.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when recovery is reached and its duration is missing
    /// or is not greater than zero.
    /// </exception>
    public double GetNextTemperature(double normalTemperature, TimeSpan elapsed)
    {
        if (!double.IsFinite(normalTemperature))
        {
            throw new ArgumentOutOfRangeException(nameof(normalTemperature), normalTemperature, "Temperature must be a finite number.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (!_definition.AutoRecover)
        {
            throw new NotSupportedException("High-temperature scenarios without automatic recovery " + "are not supported yet.");
        }

        return _deviation.GetNextValue(normalTemperature, elapsed);
    }

    /// <inheritdoc />
    public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.Metric != Metric)
        {
            throw new ArgumentException($"The runtime applies only to {Metric} measurements.", nameof(measurement));
        }

        var normalTemperature = measurement.NumericValue ?? throw new InvalidOperationException("A temperature measurement must contain a numeric value.");
        return measurement with { NumericValue = GetNextTemperature(normalTemperature, elapsed) };
    }
}