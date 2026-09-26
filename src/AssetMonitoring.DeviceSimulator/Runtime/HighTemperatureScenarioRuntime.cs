using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Tracks the temperature state of one high-temperature scenario
/// for one simulated device.
/// </summary>
/// <remarks>
/// Reuse the same instance for successive measurements of the same device.
/// Calls must be sequential and use nondecreasing elapsed simulation time.
/// The scenario definition is expected to have passed simulation plan validation.
/// This runtime calculates values without sending requests or scheduling delays.
/// </remarks>
public sealed class HighTemperatureScenarioRuntime
{
    /// <summary>
    /// Gets the definition that configures this scenario instance.
    /// </summary>
    public HighTemperatureScenarioDefinition Definition => _definition;

    /// <summary>
    /// Gets the identifier of the device affected by this scenario instance.
    /// </summary>
    public Guid DeviceId => _deviceId;

    /// <summary>
    /// Gets the current execution phase.
    /// The initial phase is <see cref="ScenarioPhase.Pending"/>.
    /// </summary>
    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Pending;

    /// <summary>
    /// Gets the most recently calculated temperature,
    /// or null before a temperature has been calculated.
    /// </summary>
    public double? CurrentTemperature { get; private set; }

    private readonly HighTemperatureScenarioDefinition _definition;
    private readonly Guid _deviceId;
    private readonly double _targetTemperature;
    private double? _recoveryStartTemperature;
    private double? _recoveryTargetTemperature;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="HighTemperatureScenarioRuntime"/> class
    /// and selects its abnormal target temperature once.
    /// </summary>
    /// <param name="definition">
    /// The validated definition containing timing, temperature bounds,
    /// recovery settings, and limits on changes per measurement.
    /// </param>
    /// <param name="deviceId">
    /// The identifier of the device affected by the scenario.
    /// </param>
    /// <param name="random">
    /// The random source used to select the abnormal target temperature.
    /// Supply a seeded source when reproducible target selection is required.
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
        _targetTemperature = definition.AbnormalMinimum + random.NextDouble() * (definition.AbnormalMaximum - definition.AbnormalMinimum);
    }

    /// <summary>
    /// Calculates the next temperature and updates the scenario phase
    /// using the elapsed time since simulation execution began.
    /// </summary>
    /// <remarks>
    /// Pending and completed scenarios return the supplied normal temperature.
    /// The active phase raises the saved temperature toward the selected target.
    /// Automatic recovery follows its planned duration while respecting the
    /// maximum decrease per measurement, so recovery may finish later if needed.
    /// Calculate a value once per new measurement; HTTP retries must reuse
    /// the existing measurement without advancing this runtime again.
    /// </remarks>
    /// <param name="normalTemperature">
    /// The finite normal temperature generated for the current measurement.
    /// It also provides an initial temperature when no previous value exists
    /// and the recovery target on the first recovery calculation.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time since simulation execution began.
    /// Supply nondecreasing values for successive calls on this instance.
    /// </param>
    /// <returns>
    /// The temperature to use for the current measurement.
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
        if (double.IsNaN(normalTemperature) || double.IsInfinity(normalTemperature))
        {
            throw new ArgumentOutOfRangeException(nameof(normalTemperature), normalTemperature, "Temperature must be a finite number.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (!_definition.AutoRecover)
        {
            throw new NotSupportedException("High-temperature scenarios without automatic recovery are not supported yet.");
        }

        if (Phase == ScenarioPhase.Completed)
        {
            return GetCompletedTemperature(normalTemperature);
        }

        if (elapsed < _definition.StartsAfter)
        {
            return GetPendingTemperature(normalTemperature);
        }

        var activeEndsAt = _definition.StartsAfter + _definition.Duration;

        if (elapsed < activeEndsAt)
        {
            return GetActiveTemperature(normalTemperature);
        }

        return GetRecoveringTemperature(normalTemperature, elapsed - activeEndsAt);
    }

    /// <summary>
    /// Records a normal temperature and keeps the scenario completed.
    /// </summary>
    /// <param name="normalTemperature">
    /// The normal temperature to return: the captured target when recovery
    /// completes, or a newly generated normal value on subsequent calls.
    /// </param>
    /// <returns>
    /// The supplied normal temperature.
    /// </returns>
    private double GetCompletedTemperature(double normalTemperature)
    {
        Phase = ScenarioPhase.Completed;
        CurrentTemperature = normalTemperature;
        return normalTemperature;
    }

    /// <summary>
    /// Records and returns the normal temperature while the scenario
    /// is waiting for its configured start time.
    /// </summary>
    /// <param name="normalTemperature">
    /// The normal temperature generated for the current measurement.
    /// </param>
    /// <returns>
    /// The supplied normal temperature.
    /// </returns>
    private double GetPendingTemperature(double normalTemperature)
    {
        Phase = ScenarioPhase.Pending;
        CurrentTemperature = normalTemperature;
        return normalTemperature;
    }

    /// <summary>
    /// Raises the saved temperature toward the abnormal target
    /// without exceeding the maximum rise per measurement.
    /// </summary>
    /// <remarks>
    /// A temperature already at or above the target is returned unchanged.
    /// </remarks>
    /// <param name="normalTemperature">
    /// The initial temperature to use when no previous value exists.
    /// </param>
    /// <returns>
    /// The temperature for the current active-phase measurement.
    /// </returns>
    private double GetActiveTemperature(double normalTemperature)
    {
        Phase = ScenarioPhase.Active;

        var previousTemperature = CurrentTemperature ?? normalTemperature;

        if (previousTemperature >= _targetTemperature)
        {
            CurrentTemperature = previousTemperature;
            return previousTemperature;
        }

        var nextTemperature = Math.Min(previousTemperature + _definition.MaximumRisePerMeasurement, _targetTemperature);
        CurrentTemperature = nextTemperature;

        return nextTemperature;
    }

    /// <summary>
    /// Calculates the recovery temperature from elapsed recovery time
    /// and the maximum decrease allowed for one measurement.
    /// </summary>
    /// <remarks>
    /// The starting temperature and normal target are captured on the first call.
    /// Recovery completes only after the planned duration has elapsed and the
    /// target can be reached without exceeding the permitted decrease.
    /// The final recovery measurement uses the exact captured target.
    /// </remarks>
    /// <param name="normalTemperature">
    /// The normal target to capture on the first recovery call,
    /// also used as the starting temperature if no previous value exists.
    /// </param>
    /// <param name="recoveryElapsed">
    /// The elapsed time since the configured end of the active phase.
    /// </param>
    /// <returns>
    /// The temperature for the current recovery measurement,
    /// or the exact target when recovery completes.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the recovery duration is missing or is not greater than zero.
    /// </exception>
    private double GetRecoveringTemperature(double normalTemperature, TimeSpan recoveryElapsed)
    {
        var recoveryDuration = _definition.RecoveryDuration ?? throw new InvalidOperationException("Automatic recovery requires a recovery duration.");

        if (recoveryDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Automatic recovery duration must be greater than zero.");
        }

        Phase = ScenarioPhase.Recovering;

        _recoveryStartTemperature ??= CurrentTemperature ?? normalTemperature;
        _recoveryTargetTemperature ??= normalTemperature;

        var progress = Math.Clamp(recoveryElapsed.TotalSeconds / recoveryDuration.TotalSeconds, 0.0, 1.0);

        var plannedTemperature = _recoveryStartTemperature.Value + (_recoveryTargetTemperature.Value - _recoveryStartTemperature.Value) * progress;
        
        var previousTemperature = CurrentTemperature ?? _recoveryStartTemperature.Value;
        var minimumAllowedTemperature = previousTemperature - _definition.MaximumRecoveryPerMeasurement;

        if (progress >= 1 && _recoveryTargetTemperature.Value >= minimumAllowedTemperature)
        {
            return GetCompletedTemperature(_recoveryTargetTemperature.Value);
        }

        var nextRecoveryTemperature = Math.Max(plannedTemperature, minimumAllowedTemperature);

        CurrentTemperature = nextRecoveryTemperature;
        return nextRecoveryTemperature;
    }
}
