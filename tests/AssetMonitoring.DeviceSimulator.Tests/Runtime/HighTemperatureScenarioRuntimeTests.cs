using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies high-temperature scenario state, phase boundaries, target selection,
/// temperature limits, automatic recovery, and terminal completion.
/// </summary>
/// <remarks>
/// Tests use the public API, explicit elapsed times, and controlled random samples.
/// No HTTP client, real-time delay, clock package, or mocking package is required.
/// Expected temperatures are specified independently as example values.
/// Definitions are valid except in tests for explicitly supported error cases.
/// </remarks>
public sealed class HighTemperatureScenarioRuntimeTests
{
    /// <summary>Identifies the device used by default fixtures.</summary>
    private static readonly Guid DeviceId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");

    /// <summary>Verifies that constructor dependencies are required.</summary>
    /// <param name="argument">The dependency to omit.</param>
    [Theory]
    [InlineData("definition")]
    [InlineData("random")]
    public void ConstructorWithNullDependencyThrowsArgumentNullException(string argument)
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new HighTemperatureScenarioRuntime(
                argument == "definition" ? null! : CreateDefinition(),
                DeviceId,
                argument == "random" ? null! : new SequenceRandom(0.5)));

        Assert.Equal(argument, exception.ParamName);
    }

    /// <summary>Verifies that an empty device identifier is rejected.</summary>
    [Fact]
    public void ConstructorWithEmptyDeviceIdThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new HighTemperatureScenarioRuntime(
                CreateDefinition(), Guid.Empty, new SequenceRandom(0.5)));

        Assert.Equal("deviceId", exception.ParamName);
    }

    /// <summary>
    /// Verifies device identity, definition identity, and state before any measurement.
    /// </summary>
    [Fact]
    public void ConstructorWithValidArgumentsInitializesPendingState()
    {
        var definition = CreateDefinition();
        var runtime = CreateRuntime(definition);

        Assert.Same(definition, runtime.Definition);
        Assert.Equal(DeviceId, runtime.DeviceId);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentTemperature);
    }

    /// <summary>
    /// Verifies selection throughout the configured abnormal range, including
    /// regression coverage for accidentally subtracting the maximum from itself.
    /// </summary>
    /// <param name="sample">The controlled random fraction.</param>
    /// <param name="expectedTarget">The independently specified target temperature.</param>
    [Theory]
    [InlineData(0.0, 30.0)]
    [InlineData(0.25, 31.0)]
    [InlineData(0.5, 32.0)]
    [InlineData(0.875, 33.5)]
    public void GetNextTemperatureSelectsTargetWithinConfiguredRange(
        double sample, double expectedTarget)
    {
        var definition = CreateDefinition() with { MaximumRisePerMeasurement = 20 };
        var runtime = CreateRuntime(definition, new SequenceRandom(sample));

        AssertMeasurement(runtime, 22, 60, expectedTarget, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies that the abnormal target is selected once and retained through
    /// active, recovering, and completed measurements.
    /// </summary>
    [Fact]
    public void GetNextTemperatureDoesNotResampleTheAbnormalTarget()
    {
        var random = new SequenceRandom(0.25);
        var definition = CreateDefinition() with
        {
            MaximumRisePerMeasurement = 20,
            MaximumRecoveryPerMeasurement = 20
        };
        var runtime = CreateRuntime(definition, random);

        AssertMeasurement(runtime, 22, 0, 22, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 22, 60, 31, ScenarioPhase.Active);
        AssertMeasurement(runtime, 24, 120, 31, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 240, 31, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 360, 22, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 23, 400, 23, ScenarioPhase.Completed);

        Assert.Equal(1, random.CallCount);
    }

    /// <summary>
    /// Verifies repeatability with equal seeds without depending on a particular
    /// framework-specific sequence of random values.
    /// </summary>
    [Fact]
    public void GetNextTemperatureWithEqualSeedsProducesEqualMeasurements()
    {
        var definition = CreateDefinition() with
        {
            MaximumRisePerMeasurement = 20,
            MaximumRecoveryPerMeasurement = 20
        };
        var first = CreateRuntime(definition, new Random(42));
        var second = CreateRuntime(definition, new Random(42));
        var steps = new (double Seconds, double Normal)[]
        {
            (0, 22), (60, 22), (120, 24), (240, 22),
            (270, 20), (300, 24), (360, 23), (400, 21)
        };

        foreach (var step in steps)
        {
            var elapsed = TimeSpan.FromSeconds(step.Seconds);
            var firstValue = first.GetNextTemperature(step.Normal, elapsed);
            var secondValue = second.GetNextTemperature(step.Normal, elapsed);

            Assert.Equal(firstValue, secondValue);
            Assert.Equal(first.Phase, second.Phase);
            Assert.Equal(first.CurrentTemperature, second.CurrentTemperature);
        }

        Assert.Equal(ScenarioPhase.Completed, first.Phase);
    }

    /// <summary>
    /// Verifies that device runtimes retain independent state and targets
    /// even when they share a definition and a random source.
    /// </summary>
    [Fact]
    public void SeparateDeviceRuntimesKeepIndependentStateAndTargets()
    {
        var definition = CreateDefinition() with { MaximumRisePerMeasurement = 20 };
        var random = new SequenceRandom(0.25, 0.75);
        var secondId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var first = new HighTemperatureScenarioRuntime(definition, DeviceId, random);
        var second = new HighTemperatureScenarioRuntime(definition, secondId, random);

        AssertMeasurement(first, 22, 60, 31, ScenarioPhase.Active);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(second.CurrentTemperature);

        AssertMeasurement(second, 22, 60, 33, ScenarioPhase.Active);
        AssertCurrentState(first, 31, ScenarioPhase.Active);
        Assert.Equal(DeviceId, first.DeviceId);
        Assert.Equal(secondId, second.DeviceId);
        Assert.Equal(2, random.CallCount);
    }

    /// <summary>Verifies that a normal input must be finite.</summary>
    /// <param name="normalTemperature">The nonfinite input.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GetNextTemperatureWithNonfiniteNormalTemperatureThrows(
        double normalTemperature)
    {
        var runtime = CreateRuntime();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextTemperature(normalTemperature, TimeSpan.Zero));

        Assert.Equal("normalTemperature", exception.ParamName);
        Assert.Equal(normalTemperature, Assert.IsType<double>(exception.ActualValue));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentTemperature);
    }

    /// <summary>
    /// Verifies rejection of the complete negative duration, including negative
    /// whole minutes and hours whose Seconds component is zero.
    /// </summary>
    /// <param name="ticks">The negative elapsed duration in ticks.</param>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-TimeSpan.TicksPerMinute)]
    [InlineData(-TimeSpan.TicksPerHour)]
    public void GetNextTemperatureWithNegativeElapsedTimeThrows(long ticks)
    {
        var runtime = CreateRuntime();
        var elapsed = TimeSpan.FromTicks(ticks);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextTemperature(22, elapsed));

        Assert.Equal("elapsed", exception.ParamName);
        Assert.Equal(elapsed, Assert.IsType<TimeSpan>(exception.ActualValue));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentTemperature);
    }

    /// <summary>
    /// Verifies that invalid input cannot bypass validation or corrupt saved state
    /// after the runtime has entered Active or Completed.
    /// </summary>
    /// <param name="completed">Whether to finish recovery before invalid calls.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetNextTemperatureWithInvalidInputPreservesExistingState(bool completed)
    {
        var definition = CreateDefinition() with
        {
            MaximumRisePerMeasurement = 20,
            MaximumRecoveryPerMeasurement = 20
        };
        var runtime = CreateRuntime(definition);
        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);

        if (completed)
        {
            AssertMeasurement(runtime, 22, 240, 32, ScenarioPhase.Recovering);
            AssertMeasurement(runtime, 22, 360, 22, ScenarioPhase.Completed);
        }

        var savedTemperature = Assert.IsType<double>(runtime.CurrentTemperature);
        var savedPhase = runtime.Phase;

        var temperatureException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextTemperature(double.NaN, TimeSpan.FromSeconds(400)));
        Assert.Equal("normalTemperature", temperatureException.ParamName);
        AssertCurrentState(runtime, savedTemperature, savedPhase);

        var timeException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextTemperature(22, TimeSpan.FromTicks(-1)));
        Assert.Equal("elapsed", timeException.ParamName);
        AssertCurrentState(runtime, savedTemperature, savedPhase);
    }

    /// <summary>
    /// Verifies the current explicit rejection of scenarios without automatic
    /// recovery, even when the first call occurs before their start time.
    /// </summary>
    /// <param name="elapsedSeconds">The time of the first attempted measurement.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(240)]
    [InlineData(400)]
    public void GetNextTemperatureWithoutAutomaticRecoveryThrows(int elapsedSeconds)
    {
        var definition = CreateDefinition() with
        {
            AutoRecover = false,
            RecoveryDuration = null
        };
        var runtime = CreateRuntime(definition);

        Assert.Throws<NotSupportedException>(() =>
            runtime.GetNextTemperature(22, TimeSpan.FromSeconds(elapsedSeconds)));

        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentTemperature);
    }

    /// <summary>
    /// Verifies recovery rejects missing, zero, and negative durations before
    /// dividing by the configured duration.
    /// </summary>
    /// <param name="recoverySeconds">The invalid duration, or null when absent.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetNextTemperatureWithInvalidRecoveryDurationThrows(int? recoverySeconds)
    {
        var definition = CreateDefinition() with
        {
            RecoveryDuration = recoverySeconds.HasValue
                ? TimeSpan.FromSeconds(recoverySeconds.Value)
                : null
        };
        var runtime = CreateRuntime(definition);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.GetNextTemperature(22, TimeSpan.FromSeconds(240)));
    }

    /// <summary>Verifies that the Pending interval excludes its end boundary.</summary>
    /// <param name="elapsedTicks">A time strictly before the configured start.</param>
    [Theory]
    [InlineData(0L)]
    [InlineData(TimeSpan.TicksPerMinute - 1)]
    public void GetNextTemperatureBeforeStartReturnsNormalTemperature(long elapsedTicks)
    {
        var runtime = CreateRuntime();

        var result = runtime.GetNextTemperature(22.25, TimeSpan.FromTicks(elapsedTicks));

        Assert.Equal(22.25, result);
        AssertCurrentState(runtime, result, ScenarioPhase.Pending);
    }

    /// <summary>
    /// Verifies that Pending follows new normal values and the first active
    /// measurement rises from the most recently saved value.
    /// </summary>
    [Fact]
    public void GetNextTemperatureStartsRisingFromTheLatestPendingTemperature()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 20, 0, 20, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 24, 30, 24, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 21.5, 59, 21.5, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 24, 60, 23.5, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies that a first call at the start or anywhere before the active
    /// end initializes from its normal input and performs one rise only.
    /// </summary>
    /// <param name="elapsedTicks">A time within the active interval.</param>
    [Theory]
    [InlineData(TimeSpan.TicksPerMinute)]
    [InlineData(150 * TimeSpan.TicksPerSecond)]
    [InlineData(4 * TimeSpan.TicksPerMinute - 1)]
    public void GetNextTemperatureWithFirstCallDuringActiveStartsFromNormal(long elapsedTicks)
    {
        var runtime = CreateRuntime();

        var result = runtime.GetNextTemperature(22, TimeSpan.FromTicks(elapsedTicks));

        Assert.Equal(24.0, result);
        AssertCurrentState(runtime, result, ScenarioPhase.Active);
    }

    /// <summary>Verifies immediate activation when StartsAfter is zero.</summary>
    [Fact]
    public void GetNextTemperatureWithZeroStartDelayIsActiveImmediately()
    {
        var runtime = CreateRuntime(CreateDefinition() with { StartsAfter = TimeSpan.Zero });

        AssertMeasurement(runtime, 22, 0, 24, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies accumulation across measurements despite changing normal samples,
    /// including regression coverage for comparing the previous value to itself.
    /// </summary>
    [Fact]
    public void GetNextTemperatureAccumulatesRiseAndHoldsTheTarget()
    {
        var runtime = CreateRuntime();
        var steps = new (double Normal, double Expected)[]
        {
            (22, 24), (20, 26), (24, 28), (21, 30), (23, 32), (20, 32)
        };

        for (var index = 0; index < steps.Length; index++)
        {
            AssertMeasurement(runtime, steps[index].Normal, 60 + index * 20,
                steps[index].Expected, ScenarioPhase.Active);
        }
    }

    /// <summary>
    /// Verifies fractional rises and a shortened final step that does not
    /// overshoot the selected abnormal target.
    /// </summary>
    [Fact]
    public void GetNextTemperatureShortensTheLastRiseToAvoidOvershooting()
    {
        var definition = CreateDefinition() with { MaximumRisePerMeasurement = 2.5 };
        var runtime = CreateRuntime(definition, new SequenceRandom(0.25));

        AssertMeasurement(runtime, 24, 60, 26.5, ScenarioPhase.Active);
        AssertMeasurement(runtime, 20, 70, 29, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 80, 31, ScenarioPhase.Active);
        AssertMeasurement(runtime, 24, 90, 31, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies that Active does not lower an initial value already at or above
    /// the selected target, or replace it with a later normal sample.
    /// </summary>
    /// <param name="initialTemperature">A value at or above the target of 32.</param>
    [Theory]
    [InlineData(32.0)]
    [InlineData(36.0)]
    public void GetNextTemperatureDoesNotLowerAnActiveValueAtOrAboveTarget(
        double initialTemperature)
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, initialTemperature, 60,
            initialTemperature, ScenarioPhase.Active);
        AssertMeasurement(runtime, 20, 90,
            initialTemperature, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies the exact active end uses simulation time, not the time of the
    /// first active call, and does not perform an additional active rise.
    /// </summary>
    [Fact]
    public void GetNextTemperatureAtActiveEndBeginsRecoveryWithoutAnotherRise()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 22, 239, 24, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 240, 24, ScenarioPhase.Recovering);
    }

    /// <summary>
    /// Verifies that a first call after the active interval uses its normal input
    /// rather than inventing a temperature rise for missed measurements.
    /// </summary>
    /// <param name="elapsedSeconds">The time of the first call.</param>
    /// <param name="expectedPhase">The expected phase after that call.</param>
    [Theory]
    [InlineData(240, ScenarioPhase.Recovering)]
    [InlineData(300, ScenarioPhase.Recovering)]
    [InlineData(360, ScenarioPhase.Completed)]
    [InlineData(600, ScenarioPhase.Completed)]
    public void GetNextTemperatureWithFirstCallAfterActiveUsesNormalInput(
        int elapsedSeconds, ScenarioPhase expectedPhase)
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 22.25, elapsedSeconds, 22.25, expectedPhase);
    }

    /// <summary>
    /// Verifies recovery retains both its original starting temperature and its
    /// first normal target despite changing normal inputs on later calls.
    /// </summary>
    [Fact]
    public void GetNextTemperatureInterpolatesUsingCapturedRecoveryValues()
    {
        var definition = CreateDefinition() with
        {
            MaximumRisePerMeasurement = 20,
            MaximumRecoveryPerMeasurement = 20
        };
        var runtime = CreateRuntime(definition);

        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 240, 32, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 270, 29.5, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 20, 300, 27, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 23, 330, 24.5, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 360, 22, ScenarioPhase.Completed);
    }

    /// <summary>
    /// Verifies that recovery may decrease by less than the configured maximum
    /// when the planned temperature is closer to the previous measurement.
    /// </summary>
    [Fact]
    public void GetNextTemperatureFollowsSmallerPlannedRecoverySteps()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 20 });

        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 240, 32, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 252, 31, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 264, 30, ScenarioPhase.Recovering);
    }

    /// <summary>
    /// Verifies the maximum decrease takes priority over the planned recovery
    /// deadline and completion waits until the target is actually reachable.
    /// </summary>
    [Fact]
    public void GetNextTemperatureExtendsRecoveryToRespectTheDecreaseLimit()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 20 });

        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 240, 32, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 300, 30, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 360, 28, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 390, 26, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 420, 24, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 450, 22, ScenarioPhase.Completed);
    }

    /// <summary>
    /// Verifies the last recovery step is shortened and returns the exact captured
    /// target instead of undershooting or returning an interpolation approximation.
    /// </summary>
    [Fact]
    public void GetNextTemperatureCompletesAtTheExactFractionalRecoveryTarget()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 20 });

        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22.37, 240, 32, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 360, 30, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 361, 28, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 362, 26, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 24, 363, 24, ScenarioPhase.Recovering);
        var finalValue = AssertMeasurement(
            runtime, 24, 364, 22.37, ScenarioPhase.Completed);

        Assert.Equal(22.37, finalValue);
    }

    /// <summary>
    /// Verifies a late first recovery call uses elapsed simulation time while
    /// still respecting the maximum decrease for that single measurement.
    /// </summary>
    [Fact]
    public void GetNextTemperatureWithLateFirstRecoveryCallLimitsTheDecrease()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 20 });

        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 360, 30, ScenarioPhase.Recovering);
    }

    /// <summary>
    /// Verifies being at the normal target is insufficient for completion before
    /// the configured recovery duration has elapsed.
    /// </summary>
    [Fact]
    public void GetNextTemperatureAtRecoveryTargetWaitsForTheRecoveryEnd()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 22, 240, 22, ScenarioPhase.Recovering);
        var justBeforeEnd = TimeSpan.FromMinutes(6) - TimeSpan.FromTicks(1);
        var beforeEndValue = runtime.GetNextTemperature(22, justBeforeEnd);
        Assert.Equal(22.0, beforeEndValue);
        AssertCurrentState(runtime, beforeEndValue, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 360, 22, ScenarioPhase.Completed);
    }

    /// <summary>
    /// Verifies terminal completion remains stable across multiple normal samples,
    /// including regression coverage for accidentally resetting the phase.
    /// </summary>
    [Fact]
    public void GetNextTemperatureAfterCompletionReturnsFreshNormalValues()
    {
        var definition = CreateDefinition() with
        {
            MaximumRisePerMeasurement = 20,
            MaximumRecoveryPerMeasurement = 20
        };
        var runtime = CreateRuntime(definition);

        AssertMeasurement(runtime, 22, 60, 32, ScenarioPhase.Active);
        AssertMeasurement(runtime, 22, 240, 32, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 22, 360, 22, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 24, 361, 24, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 20, 362, 20, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 23.5, 400, 23.5, ScenarioPhase.Completed);
    }

    /// <summary>
    /// Verifies a complete example using the ordinary two-degree limits,
    /// including waiting, rising, holding, delayed recovery, and completion.
    /// </summary>
    [Fact]
    public void GetNextTemperatureRunsTheCompleteScenarioLifecycle()
    {
        var runtime = CreateRuntime();
        var steps = new (double Seconds, double Normal, double Expected, ScenarioPhase Phase)[]
        {
            (0, 22, 22, ScenarioPhase.Pending),
            (59, 22, 22, ScenarioPhase.Pending),
            (60, 22, 24, ScenarioPhase.Active),
            (90, 22, 26, ScenarioPhase.Active),
            (120, 22, 28, ScenarioPhase.Active),
            (150, 22, 30, ScenarioPhase.Active),
            (180, 22, 32, ScenarioPhase.Active),
            (210, 22, 32, ScenarioPhase.Active),
            (240, 22, 32, ScenarioPhase.Recovering),
            (270, 22, 30, ScenarioPhase.Recovering),
            (300, 22, 28, ScenarioPhase.Recovering),
            (330, 22, 26, ScenarioPhase.Recovering),
            (360, 22, 24, ScenarioPhase.Recovering),
            (390, 22, 22, ScenarioPhase.Completed),
            (420, 24, 24, ScenarioPhase.Completed),
            (450, 20, 20, ScenarioPhase.Completed)
        };

        foreach (var step in steps)
        {
            AssertMeasurement(runtime, step.Normal, step.Seconds, step.Expected, step.Phase);
        }
    }

    /// <summary>
    /// Creates a runtime with a target of 32 unless another random source is supplied.
    /// </summary>
    /// <param name="definition">The definition override, or null for the default.</param>
    /// <param name="random">The random source override, or null for a fixed sample.</param>
    /// <returns>A new independent runtime for the fixture device.</returns>
    private static HighTemperatureScenarioRuntime CreateRuntime(
        HighTemperatureScenarioDefinition? definition = null,
        Random? random = null) =>
        new(definition ?? CreateDefinition(), DeviceId, random ?? new SequenceRandom(0.5));

    /// <summary>
    /// Creates a scenario starting at 60 seconds, recovering from 240 seconds,
    /// and planning to finish recovery at 360 seconds, with two-degree limits.
    /// </summary>
    /// <returns>The default validated-shape scenario definition.</returns>
    private static HighTemperatureScenarioDefinition CreateDefinition() =>
        new(
            "GradualHighTemperature",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true,
            TimeSpan.FromMinutes(2),
            30,
            34,
            2,
            2);

    /// <summary>
    /// Checks the returned value, saved temperature, and phase for one measurement.
    /// </summary>
    /// <param name="runtime">The runtime to advance.</param>
    /// <param name="normalTemperature">The supplied normal temperature.</param>
    /// <param name="elapsedSeconds">The elapsed simulation time in seconds.</param>
    /// <param name="expectedTemperature">The independent expected temperature.</param>
    /// <param name="expectedPhase">The expected phase after the call.</param>
    /// <returns>The actual temperature, for additional exact-value assertions.</returns>
    private static double AssertMeasurement(
        HighTemperatureScenarioRuntime runtime,
        double normalTemperature,
        double elapsedSeconds,
        double expectedTemperature,
        ScenarioPhase expectedPhase)
    {
        var actual = runtime.GetNextTemperature(
            normalTemperature, TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(expectedTemperature, actual, 10);
        AssertCurrentState(runtime, actual, expectedPhase);
        return actual;
    }

    /// <summary>Checks the externally observable saved state.</summary>
    /// <param name="runtime">The runtime to inspect.</param>
    /// <param name="expectedTemperature">The exact value expected in saved state.</param>
    /// <param name="expectedPhase">The expected scenario phase.</param>
    private static void AssertCurrentState(
        HighTemperatureScenarioRuntime runtime,
        double expectedTemperature,
        ScenarioPhase expectedPhase)
    {
        Assert.Equal(expectedPhase, runtime.Phase);
        Assert.Equal(expectedTemperature, Assert.IsType<double>(runtime.CurrentTemperature));
    }

    /// <summary>
    /// Supplies predetermined random fractions and detects unexpected extra draws.
    /// </summary>
    private sealed class SequenceRandom : Random
    {
        private readonly Queue<double> _samples;

        /// <summary>Initializes the ordered samples available to the runtime.</summary>
        /// <param name="samples">Valid random fractions in the interval [0, 1).</param>
        public SequenceRandom(params double[] samples)
        {
            _samples = new Queue<double>(samples);
        }

        /// <summary>Gets the number of samples supplied so far.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc />
        public override double NextDouble()
        {
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException(
                    "The runtime requested an unexpected random value.");
            }

            CallCount++;
            return _samples.Dequeue();
        }
    }
}
