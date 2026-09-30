namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Tracks the gradual rise of a numeric value toward a fixed target
/// and its subsequent recovery to a normal value.
/// </summary>
/// <remarks>
/// Reuse one instance for successive measurements of the same scenario.
/// Calls must be sequential and use nondecreasing elapsed time.
/// Configuration and input values are expected to be validated by the caller.
/// This class calculates values without scheduling delays or sending requests.
/// </remarks>
internal sealed class GradualDeviation
{
    private readonly TimeSpan _startAfter;
    private readonly TimeSpan _duration;
    private readonly TimeSpan? _recoveryDuration;
    private readonly double _targetValue;
    private readonly double _maximumRisePerMeasurement;
    private readonly double _maximumRecoveryPerMeasurement;

    private double? _recoveryStartValue;
    private double? _recoveryTargetValue;

    /// <summary>
    /// Gets the current execution phase.
    /// The initial phase is <see cref="ScenarioPhase.Pending"/>.
    /// </summary>
    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Pending;

    /// <summary>
    /// Gets the most recently calculated value,
    /// or null before the first calculation.
    /// </summary>
    public double? CurrentValue { get; private set; }

    /// <summary>
    /// Gets the fixed target toward which the active phase raises the value.
    /// </summary>
    public double TargetValue => _targetValue;

    /// <summary>
    /// Initializes a gradual deviation using the supplied timing,
    /// target, and per-measurement limits.
    /// </summary>
    /// <param name="startAfter">
    /// The nonnegative delay before the active phase begins.
    /// </param>
    /// <param name="duration">
    /// The positive length of the active phase, including the gradual rise
    /// and any remaining time spent at the target.
    /// </param>
    /// <param name="recoveryDuration">
    /// The planned recovery duration.
    /// A missing or nonpositive duration is rejected when recovery is reached.
    /// </param>
    /// <param name="targetValue">
    /// The finite target value used throughout the active phase.
    /// </param>
    /// <param name="maximumRisePerMeasurement">
    /// The positive, finite maximum increase allowed per calculation
    /// during the active phase.
    /// </param>
    /// <param name="maximumRecoveryPerMeasurement">
    /// The positive, finite maximum decrease allowed per calculation
    /// during recovery.
    /// </param>
    public GradualDeviation(TimeSpan startAfter, TimeSpan duration, TimeSpan? recoveryDuration, double targetValue, double maximumRisePerMeasurement, double maximumRecoveryPerMeasurement)
    {
        _startAfter = startAfter;
        _duration = duration;
        _recoveryDuration = recoveryDuration;
        _targetValue = targetValue;
        _maximumRisePerMeasurement = maximumRisePerMeasurement;
        _maximumRecoveryPerMeasurement = maximumRecoveryPerMeasurement;
    }

    /// <summary>
    /// Calculates the next value and updates the execution phase.
    /// </summary>
    /// <remarks>
    /// Pending and completed phases return the supplied normal value.
    /// Calculate a value once per new measurement.
    /// Retrying delivery of an existing measurement must not advance
    /// this instance again.
    /// </remarks>
    /// <param name="normalValue">
    /// The finite normal value generated for the current measurement.
    /// It supplies the initial active value when no previous value exists
    /// and the recovery target on the first recovery calculation.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time on the scenario timeline,
    /// using the same origin as the configured start delay.
    /// Successive calls must supply nondecreasing values.
    /// </param>
    /// <returns>
    /// The value to use for the current measurement.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when recovery is reached and its duration is missing
    /// or is not greater than zero.
    /// </exception>
    public double GetNextValue(double normalValue, TimeSpan elapsed)
    {
        if (Phase == ScenarioPhase.Completed)
        {
            return SetCompleted(normalValue);
        }

        if (elapsed < _startAfter)
        {
            Phase = ScenarioPhase.Pending;
            CurrentValue = normalValue;
            return normalValue;
        }

        var activeEndAt = _startAfter + _duration;
        return elapsed < activeEndAt ? GetActiveValue(normalValue) : GetRecoveringValue(normalValue, elapsed - activeEndAt);
    }

    /// <summary>
    /// Raises the saved value toward the target without exceeding
    /// the maximum increase per measurement.
    /// </summary>
    /// <remarks>
    /// A value already at or above the target is returned unchanged.
    /// </remarks>
    /// <param name="normalValue">
    /// The initial value to use when no previous value exists.
    /// </param>
    /// <returns>
    /// The value for the current active-phase measurement.
    /// </returns>
    private double GetActiveValue(double normalValue)
    {
        Phase = ScenarioPhase.Active;

        var previousValue = CurrentValue ?? normalValue;

        if (previousValue >= _targetValue)
        {
            CurrentValue = previousValue;
            return previousValue;
        }

        var nextValue = Math.Min(previousValue + _maximumRisePerMeasurement, _targetValue);

        CurrentValue = nextValue;
        return nextValue;
    }

    /// <summary>
    /// Calculates a recovery value using linear interpolation
    /// and the maximum decrease allowed per measurement.
    /// </summary>
    /// <remarks>
    /// The starting value and normal target are captured once,
    /// on the first recovery calculation.
    /// The decrease limit takes priority over the planned duration,
    /// so recovery may finish later than scheduled.
    /// Completion requires the planned duration to have elapsed
    /// and the target to be reachable within the permitted decrease.
    /// </remarks>
    /// <param name="normalValue">
    /// The recovery target to capture on the first call.
    /// Also used as the starting value when no previous value exists.
    /// </param>
    /// <param name="recoveryElapsed">
    /// The elapsed time since the scheduled end of the active phase.
    /// </param>
    /// <returns>
    /// The recovery value, or the exact captured target
    /// when recovery completes.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the recovery duration is missing
    /// or is not greater than zero.
    /// </exception>
    private double GetRecoveringValue(double normalValue, TimeSpan recoveryElapsed)
    {
        var recoveryDuration = _recoveryDuration ?? throw new InvalidOperationException("Automatic recovery requires a recovery duration.");

        if (recoveryDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Automatic recovery duration must be greater than zero.");
        }

        Phase = ScenarioPhase.Recovering;

        _recoveryStartValue ??= CurrentValue ?? normalValue;
        _recoveryTargetValue ??= normalValue;

        var progress = Math.Clamp(recoveryElapsed.TotalSeconds / recoveryDuration.TotalSeconds, 0.0, 1.0);

        var plannedValue = _recoveryStartValue.Value + (_recoveryTargetValue.Value - _recoveryStartValue.Value) * progress;

        var previousValue = CurrentValue ?? _recoveryStartValue.Value;
        var minimumAllowedValue = previousValue - _maximumRecoveryPerMeasurement;

        if (progress >= 1 && _recoveryTargetValue.Value >= minimumAllowedValue)
        {
            return SetCompleted(_recoveryTargetValue.Value);
        }

        var nextValue = Math.Max(plannedValue, minimumAllowedValue);
        CurrentValue = nextValue;
        return nextValue;
    }

    /// <summary>
    /// Marks the deviation as completed and records the supplied value.
    /// </summary>
    /// <param name="value">
    /// The captured recovery target on completion,
    /// or a fresh normal value on subsequent calls.
    /// </param>
    /// <returns>
    /// The supplied value.
    /// </returns>
    private double SetCompleted(double value)
    {
        Phase = ScenarioPhase.Completed;
        CurrentValue = value;
        return value;
    }
}