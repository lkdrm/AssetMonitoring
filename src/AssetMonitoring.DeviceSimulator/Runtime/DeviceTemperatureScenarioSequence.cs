namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Coordinates sequential temperature scenarios for one device.
/// </summary>
public sealed class DeviceTemperatureScenarioSequence
{
    private readonly IReadOnlyList<HighTemperatureScenarioRuntime> _scenarios;
    private int _currentScenarioIndex;
    private TimeSpan? _currentScenarioStartedAt;

    /// <summary>
    /// Initializes the sequence with scenarios ordered by their planned start time.
    /// Scenarios with equal start times retain their input order.
    /// </summary>
    /// <param name="deviceId">The device that owns the scenarios.</param>
    /// <param name="scenarios">
    /// The scenario runtimes to execute. An empty collection is allowed.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="scenarios"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty, a scenario is null,
    /// or a scenario belongs to another device.
    /// </exception>
    public DeviceTemperatureScenarioSequence(Guid deviceId, IReadOnlyList<HighTemperatureScenarioRuntime> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        foreach (var scenario in scenarios)
        {
            if (scenario is null)
            {
                throw new ArgumentException("The scenario collection cannot contain null items.", nameof(scenarios));
            }

            if (scenario.DeviceId != deviceId)
            {
                throw new ArgumentException("Every scenario must belong to the specified device.", nameof(scenarios));
            }
        }

        _scenarios = scenarios.OrderBy(scenario => scenario.Definition.StartsAfter).ToArray();
        _currentScenarioIndex = 0;
        _currentScenarioStartedAt = null;
    }

    /// <summary>
    /// Calculates the next temperature using the current scenario
    /// and advances the queue when that scenario completes.
    /// </summary>
    /// <remarks>
    /// Each scenario starts on the first eligible measurement after its
    /// planned start time and completion of the preceding scenario.
    /// Its actual start is retained so waiting in the queue does not
    /// shorten its active duration.
    /// 
    /// A measurement processes at most one scenario. After completion,
    /// the next scenario can start on a subsequent call.
    /// 
    /// Calls must be sequential and use nondecreasing elapsed time.
    /// Invoke once per new temperature measurement; HTTP retries must
    /// reuse the calculated value without advancing the sequence again.
    /// </remarks>
    /// <param name="normalTemperature">
    /// The finite normal temperature for the current measurement.
    /// Used when no scenario is running and as the baseline supplied
    /// to the current scenario.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time since simulation execution began.
    /// </param>
    /// <returns>
    /// The temperature calculated by the current scenario, or
    /// <paramref name="normalTemperature"/> when the queue is empty,
    /// all scenarios have completed, or the next scenario is waiting
    /// for its planned start time.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="normalTemperature"/> is not finite
    /// or <paramref name="elapsed"/> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the current scenario reaches recovery with a missing
    /// or nonpositive recovery duration.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the current scenario has automatic recovery disabled.
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

        if (_currentScenarioIndex >= _scenarios.Count)
        {
            return normalTemperature;
        }

        var scenario = _scenarios[_currentScenarioIndex];
        if (elapsed < scenario.Definition.StartsAfter)
        {
            return normalTemperature;
        }

        _currentScenarioStartedAt ??= elapsed;

        var scenarioElapsed = scenario.Definition.StartsAfter + (elapsed - _currentScenarioStartedAt.Value);
        var temperature = scenario.GetNextTemperature(normalTemperature, scenarioElapsed);

        if(scenario.Phase == ScenarioPhase.Completed)
        {
            _currentScenarioIndex++;
            _currentScenarioStartedAt = null;
        }

        return temperature;
    }
}
