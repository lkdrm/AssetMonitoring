using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies humidity runtime input validation, target selection, phase
/// boundaries, recovery behavior, and measurement transformation.
/// </summary>
/// <remarks>
/// Tests use the public API, explicit elapsed times, and controlled random
/// samples. Expected humidity values are independent example values.
/// No HTTP client, real clock, delay, or additional test package is required.
/// Definitions have valid settings except in explicit error tests.
/// </remarks>
public sealed class HighHumidityScenarioRuntimeTests
{
    /// <summary>Identifies the device used by default fixtures.</summary>
    private static readonly Guid DeviceId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");

    /// <summary>Verifies that constructor dependencies are required.</summary>
    /// <param name="argument">The dependency omitted from construction.</param>
    [Theory]
    [InlineData("definition")]
    [InlineData("random")]
    public void ConstructorWithNullDependencyThrowsArgumentNullException(string argument)
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new HighHumidityScenarioRuntime(
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
            new HighHumidityScenarioRuntime(
                CreateDefinition(), Guid.Empty, new SequenceRandom(0.5)));

        Assert.Equal("deviceId", exception.ParamName);
    }

    /// <summary>
    /// Verifies identity and initial state through both the concrete runtime
    /// and the common measurement scenario interface.
    /// </summary>
    [Fact]
    public void ConstructorInitializesIdentityMetricAndPendingState()
    {
        var definition = CreateDefinition();
        var runtime = CreateRuntime(definition);
        IMeasurementScenarioRuntime contract = runtime;

        Assert.Same(definition, runtime.Definition);
        Assert.Same(definition, contract.Definition);
        Assert.Equal(DeviceId, runtime.DeviceId);
        Assert.Equal(DeviceId, contract.DeviceId);
        Assert.Equal(SimulatorTelemetryMetric.Humidity, runtime.Metric);
        Assert.Equal(SimulatorTelemetryMetric.Humidity, contract.Metric);
        Assert.Equal(ScenarioPhase.Pending, contract.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies target selection throughout the abnormal range.</summary>
    /// <param name="sample">The controlled random fraction.</param>
    /// <param name="expectedTarget">The independently specified target.</param>
    [Theory]
    [InlineData(0.0, 90.0)]
    [InlineData(0.25, 91.0)]
    [InlineData(0.5, 92.0)]
    [InlineData(0.875, 93.5)]
    public void GetNextHumiditySelectsTheConfiguredTarget(double sample, double expectedTarget)
    {
        var runtime = CreateRuntime(
            CreateDefinition() with { MaximumRisePerMeasurement = 100 },
            new SequenceRandom(sample));

        AssertMeasurement(runtime, 80, 60, expectedTarget, ScenarioPhase.Active);
    }

    /// <summary>Verifies that no phase resamples the target selected at construction.</summary>
    [Fact]
    public void GetNextHumiditySelectsTheTargetOnlyOnce()
    {
        var random = new SequenceRandom(0.25);
        var runtime = CreateRuntime(CreateDefinition() with
        {
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        }, random);

        Assert.Equal(1, random.CallCount);
        AssertMeasurement(runtime, 80, 0, 80, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 80, 60, 91, ScenarioPhase.Active);
        AssertMeasurement(runtime, 82, 120, 91, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 240, 91, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 360, 80, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 81, 400, 81, ScenarioPhase.Completed);
        Assert.Equal(1, random.CallCount);
    }

    /// <summary>
    /// Verifies reproducibility without assuming a framework-specific random sequence.
    /// </summary>
    /// <param name="seed">The seed shared by two independent random sources.</param>
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-17)]
    public void GetNextHumidityWithEqualSeedsProducesEqualMeasurements(int seed)
    {
        var definition = CreateDefinition() with
        {
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        };
        var first = CreateRuntime(definition, new Random(seed));
        var second = CreateRuntime(definition, new Random(seed));
        var steps = new (double Seconds, double Normal)[]
        {
            (0, 80), (60, 80), (120, 82), (240, 80),
            (270, 78), (300, 82), (360, 81), (400, 79)
        };

        foreach (var step in steps)
        {
            var elapsed = TimeSpan.FromSeconds(step.Seconds);
            var firstValue = first.GetNextHumidity(step.Normal, elapsed);
            var secondValue = second.GetNextHumidity(step.Normal, elapsed);

            Assert.Equal(firstValue, secondValue);
            Assert.Equal(first.Phase, second.Phase);
            Assert.Equal(first.CurrentHumidity, second.CurrentHumidity);
            Assert.InRange(firstValue, 0.0, 100.0);
        }

        Assert.Equal(ScenarioPhase.Completed, first.Phase);
    }

    /// <summary>
    /// Verifies independent targets and state for runtimes sharing a definition
    /// and random source.
    /// </summary>
    [Fact]
    public void SeparateDeviceRuntimesKeepIndependentStateAndTargets()
    {
        var definition = CreateDefinition() with { MaximumRisePerMeasurement = 100 };
        var random = new SequenceRandom(0.25, 0.75);
        var secondId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var first = new HighHumidityScenarioRuntime(definition, DeviceId, random);
        var second = new HighHumidityScenarioRuntime(definition, secondId, random);

        AssertMeasurement(first, 80, 60, 91, ScenarioPhase.Active);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(second.CurrentHumidity);
        AssertMeasurement(second, 80, 60, 93, ScenarioPhase.Active);
        AssertCurrentState(first, 91, ScenarioPhase.Active);
        Assert.Equal(secondId, second.DeviceId);
        Assert.Equal(2, random.CallCount);
    }

    /// <summary>Verifies that invalid normal humidity is rejected before state changes.</summary>
    /// <param name="normalHumidity">A nonfinite or out-of-range normal value.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-double.Epsilon)]
    [InlineData(-1.0)]
    [InlineData(100.000000001)]
    [InlineData(101.0)]
    public void GetNextHumidityWithInvalidNormalHumidityThrows(double normalHumidity)
    {
        var runtime = CreateRuntime();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextHumidity(normalHumidity, TimeSpan.Zero));

        Assert.Equal("normalHumidity", exception.ParamName);
        Assert.Equal(normalHumidity, Assert.IsType<double>(exception.ActualValue));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies that both physical humidity boundaries are inclusive.</summary>
    /// <param name="normalHumidity">An allowed endpoint of the humidity interval.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(100.0)]
    public void GetNextHumidityAcceptsZeroAndOneHundred(double normalHumidity)
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, normalHumidity, 0, normalHumidity, ScenarioPhase.Pending);
    }

    /// <summary>Verifies negative durations, including whole minutes and hours.</summary>
    /// <param name="ticks">The negative elapsed duration in ticks.</param>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-TimeSpan.TicksPerMinute)]
    [InlineData(-TimeSpan.TicksPerHour)]
    public void GetNextHumidityWithNegativeElapsedTimeThrows(long ticks)
    {
        var runtime = CreateRuntime();
        var elapsed = TimeSpan.FromTicks(ticks);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextHumidity(80, elapsed));

        Assert.Equal("elapsed", exception.ParamName);
        Assert.Equal(elapsed, Assert.IsType<TimeSpan>(exception.ActualValue));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>
    /// Verifies that invalid values and times do not alter existing runtime state,
    /// including after completion.
    /// </summary>
    /// <param name="phase">The phase reached before invalid input is supplied.</param>
    [Theory]
    [InlineData(ScenarioPhase.Active)]
    [InlineData(ScenarioPhase.Recovering)]
    [InlineData(ScenarioPhase.Completed)]
    public void GetNextHumidityWithInvalidInputPreservesExistingState(ScenarioPhase phase)
    {
        var runtime = CreateFastRuntime();
        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);

        if (phase != ScenarioPhase.Active)
        {
            AssertMeasurement(runtime, 80, 240, 92, ScenarioPhase.Recovering);
        }

        if (phase == ScenarioPhase.Completed)
        {
            AssertMeasurement(runtime, 80, 360, 80, ScenarioPhase.Completed);
        }

        var savedHumidity = Assert.IsType<double>(runtime.CurrentHumidity);

        foreach (var invalidHumidity in new[] { double.NaN, -1.0, 101.0 })
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                runtime.GetNextHumidity(invalidHumidity, TimeSpan.FromSeconds(400)));
            Assert.Equal("normalHumidity", exception.ParamName);
            AssertCurrentState(runtime, savedHumidity, phase);
        }

        var timeException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GetNextHumidity(80, TimeSpan.FromTicks(-1)));
        Assert.Equal("elapsed", timeException.ParamName);
        AssertCurrentState(runtime, savedHumidity, phase);
    }

    /// <summary>
    /// Verifies that unsupported recovery settings cannot bypass validation
    /// by calling before activation or after the planned recovery deadline.
    /// </summary>
    /// <param name="elapsedSeconds">The time of the first attempted calculation.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(240)]
    [InlineData(400)]
    public void GetNextHumidityWithoutAutomaticRecoveryThrows(int elapsedSeconds)
    {
        var runtime = CreateRuntime(CreateDefinition() with
        {
            AutoRecover = false,
            RecoveryDuration = null
        });

        Assert.Throws<NotSupportedException>(() =>
            runtime.GetNextHumidity(80, TimeSpan.FromSeconds(elapsedSeconds)));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies invalid recovery durations are rejected when recovery is reached.</summary>
    /// <param name="recoverySeconds">The invalid duration, or null when missing.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetNextHumidityWithInvalidRecoveryDurationThrowsAtRecovery(int? recoverySeconds)
    {
        var runtime = CreateRuntime(CreateDefinition() with
        {
            RecoveryDuration = recoverySeconds.HasValue
                ? TimeSpan.FromSeconds(recoverySeconds.Value)
                : null
        });
        AssertMeasurement(runtime, 80, 60, 82, ScenarioPhase.Active);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.GetNextHumidity(80, TimeSpan.FromSeconds(240)));
        AssertCurrentState(runtime, 82, ScenarioPhase.Active);
    }

    /// <summary>Verifies that Pending ends exactly at the configured start.</summary>
    /// <param name="elapsedTicks">An elapsed time strictly before activation.</param>
    [Theory]
    [InlineData(0L)]
    [InlineData(TimeSpan.TicksPerMinute - 1)]
    public void GetNextHumidityBeforeStartReturnsNormalHumidity(long elapsedTicks)
    {
        var runtime = CreateRuntime();

        var result = runtime.GetNextHumidity(80.25, TimeSpan.FromTicks(elapsedTicks));

        Assert.Equal(80.25, result);
        AssertCurrentState(runtime, result, ScenarioPhase.Pending);
    }

    /// <summary>Verifies activation uses the most recently saved Pending value.</summary>
    [Fact]
    public void GetNextHumidityStartsRisingFromTheLatestPendingHumidity()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 78, 0, 78, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 82, 30, 82, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 79.5, 59, 79.5, ScenarioPhase.Pending);
        AssertMeasurement(runtime, 82, 60, 81.5, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies a first active call performs one rise without inventing
    /// measurements missed before that call.
    /// </summary>
    /// <param name="elapsedTicks">An elapsed time within the active interval.</param>
    [Theory]
    [InlineData(TimeSpan.TicksPerMinute)]
    [InlineData(150 * TimeSpan.TicksPerSecond)]
    [InlineData(4 * TimeSpan.TicksPerMinute - 1)]
    public void GetNextHumidityWithFirstCallDuringActivePerformsOneRise(long elapsedTicks)
    {
        var runtime = CreateRuntime();

        var result = runtime.GetNextHumidity(80, TimeSpan.FromTicks(elapsedTicks));

        Assert.Equal(82.0, result);
        AssertCurrentState(runtime, result, ScenarioPhase.Active);
    }

    /// <summary>Verifies immediate activation when the start delay is zero.</summary>
    [Fact]
    public void GetNextHumidityWithZeroStartDelayIsActiveImmediately()
    {
        var runtime = CreateRuntime(CreateDefinition() with { StartsAfter = TimeSpan.Zero });

        AssertMeasurement(runtime, 80, 0, 82, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies the rise limit applies per measurement, including successive
    /// calls with the same elapsed time.
    /// </summary>
    [Fact]
    public void GetNextHumidityAccumulatesRisePerMeasurementAndHoldsTheTarget()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 80, 60, 82, ScenarioPhase.Active);
        AssertMeasurement(runtime, 78, 60, 84, ScenarioPhase.Active);
        AssertMeasurement(runtime, 82, 90, 86, ScenarioPhase.Active);
        AssertMeasurement(runtime, 79, 120, 88, ScenarioPhase.Active);
        AssertMeasurement(runtime, 81, 150, 90, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 180, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 78, 210, 92, ScenarioPhase.Active);
    }

    /// <summary>Verifies that a fractional last rise stops exactly at the target.</summary>
    [Fact]
    public void GetNextHumidityShortensTheLastRiseToAvoidOvershooting()
    {
        var runtime = CreateRuntime(
            CreateDefinition() with { MaximumRisePerMeasurement = 2.5 },
            new SequenceRandom(0.25));

        AssertMeasurement(runtime, 84, 60, 86.5, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 70, 89, ScenarioPhase.Active);
        AssertMeasurement(runtime, 82, 80, 91, ScenarioPhase.Active);
        AssertMeasurement(runtime, 84, 90, 91, ScenarioPhase.Active);
    }

    /// <summary>Verifies that Active retains an initial value at or above its target.</summary>
    /// <param name="initialHumidity">An allowed value at or above the target of 92.</param>
    [Theory]
    [InlineData(92.0)]
    [InlineData(100.0)]
    public void GetNextHumidityDoesNotLowerAnActiveValueAtOrAboveTarget(double initialHumidity)
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, initialHumidity, 60, initialHumidity, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 90, initialHumidity, ScenarioPhase.Active);
    }

    /// <summary>
    /// Verifies that the exact active end begins recovery without another rise,
    /// even if the abnormal target has not yet been reached.
    /// </summary>
    [Fact]
    public void GetNextHumidityAtActiveEndBeginsRecoveryWithoutAnotherRise()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 80, 239, 82, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 240, 82, ScenarioPhase.Recovering);
    }

    /// <summary>Verifies late first calls do not invent an earlier abnormal rise.</summary>
    /// <param name="elapsedSeconds">The elapsed time of the first call.</param>
    /// <param name="expectedPhase">The phase appropriate to that time.</param>
    [Theory]
    [InlineData(240, ScenarioPhase.Recovering)]
    [InlineData(300, ScenarioPhase.Recovering)]
    [InlineData(360, ScenarioPhase.Completed)]
    [InlineData(600, ScenarioPhase.Completed)]
    public void GetNextHumidityWithFirstCallAfterActiveUsesNormalInput(
        int elapsedSeconds, ScenarioPhase expectedPhase)
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 80.25, elapsedSeconds, 80.25, expectedPhase);
    }

    /// <summary>
    /// Verifies linear recovery uses consistent time units and retains the
    /// starting value and normal target captured on its first recovery call.
    /// </summary>
    [Fact]
    public void GetNextHumidityInterpolatesUsingCapturedRecoveryValues()
    {
        var runtime = CreateFastRuntime();

        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 240, 92, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 270, 89, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 78, 300, 86, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 81, 330, 83, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 360, 80, ScenarioPhase.Completed);
    }

    /// <summary>Verifies recovery follows a planned decrease smaller than its limit.</summary>
    [Fact]
    public void GetNextHumidityFollowsSmallerPlannedRecoverySteps()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 100 });

        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 240, 92, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 250, 91, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 260, 90, ScenarioPhase.Recovering);
    }

    /// <summary>Verifies the decrease limit can extend recovery beyond its planned end.</summary>
    [Fact]
    public void GetNextHumidityExtendsRecoveryToRespectTheDecreaseLimit()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 100 });

        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 240, 92, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 300, 90, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 360, 88, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 390, 86, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 420, 84, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 450, 82, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 480, 80, ScenarioPhase.Completed);
    }

    /// <summary>Verifies that the final recovery step returns the exact captured target.</summary>
    [Fact]
    public void GetNextHumidityCompletesAtTheExactFractionalRecoveryTarget()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 100 });

        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80.37, 240, 92, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 360, 90, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 361, 88, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 362, 86, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 363, 84, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 364, 82, ScenarioPhase.Recovering);
        var finalValue = AssertMeasurement(runtime, 82, 365, 80.37, ScenarioPhase.Completed);

        Assert.Equal(80.37, finalValue);
    }

    /// <summary>Verifies a late first recovery call still respects a single decrease limit.</summary>
    [Fact]
    public void GetNextHumidityWithLateFirstRecoveryCallLimitsTheDecrease()
    {
        var runtime = CreateRuntime(CreateDefinition() with { MaximumRisePerMeasurement = 100 });

        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 360, 90, ScenarioPhase.Recovering);
    }

    /// <summary>Verifies that completion also requires the planned recovery time to elapse.</summary>
    [Fact]
    public void GetNextHumidityAtRecoveryTargetWaitsForTheRecoveryEnd()
    {
        var runtime = CreateRuntime();

        AssertMeasurement(runtime, 80, 240, 80, ScenarioPhase.Recovering);
        var justBeforeEnd = TimeSpan.FromMinutes(6) - TimeSpan.FromTicks(1);
        var beforeEnd = runtime.GetNextHumidity(80, justBeforeEnd);
        Assert.Equal(80.0, beforeEnd);
        AssertCurrentState(runtime, beforeEnd, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 80, 360, 80, ScenarioPhase.Completed);
    }

    /// <summary>Verifies that Completed remains terminal and follows fresh normal inputs.</summary>
    [Fact]
    public void GetNextHumidityAfterCompletionReturnsFreshNormalValues()
    {
        var runtime = CreateFastRuntime();

        AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
        AssertMeasurement(runtime, 80, 240, 92, ScenarioPhase.Recovering);
        AssertMeasurement(runtime, 82, 360, 80, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 82, 361, 82, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 0, 362, 0, ScenarioPhase.Completed);
        AssertMeasurement(runtime, 100, 400, 100, ScenarioPhase.Completed);
    }

    /// <summary>Verifies the complete lifecycle with ordinary two-point limits.</summary>
    [Fact]
    public void GetNextHumidityRunsTheCompleteScenarioLifecycleWithinPhysicalBounds()
    {
        var runtime = CreateRuntime();
        var steps = new (double Seconds, double Normal, double Expected, ScenarioPhase Phase)[]
        {
            (0, 80, 80, ScenarioPhase.Pending),
            (59, 80, 80, ScenarioPhase.Pending),
            (60, 80, 82, ScenarioPhase.Active),
            (90, 80, 84, ScenarioPhase.Active),
            (120, 80, 86, ScenarioPhase.Active),
            (150, 80, 88, ScenarioPhase.Active),
            (180, 80, 90, ScenarioPhase.Active),
            (210, 80, 92, ScenarioPhase.Active),
            (230, 80, 92, ScenarioPhase.Active),
            (240, 80, 92, ScenarioPhase.Recovering),
            (270, 80, 90, ScenarioPhase.Recovering),
            (300, 80, 88, ScenarioPhase.Recovering),
            (330, 80, 86, ScenarioPhase.Recovering),
            (360, 80, 84, ScenarioPhase.Recovering),
            (390, 80, 82, ScenarioPhase.Recovering),
            (420, 82, 80, ScenarioPhase.Completed),
            (450, 78, 78, ScenarioPhase.Completed)
        };

        foreach (var step in steps)
        {
            var value = AssertMeasurement(runtime, step.Normal, step.Seconds,
                step.Expected, step.Phase);
            Assert.InRange(value, 0.0, 100.0);
        }
    }

    /// <summary>Verifies that Apply requires a measurement.</summary>
    [Fact]
    public void ApplyWithNullMeasurementThrowsArgumentNullException()
    {
        var runtime = CreateRuntime();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            runtime.Apply(null!, TimeSpan.Zero));

        Assert.Equal("measurement", exception.ParamName);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies that only humidity measurements are accepted.</summary>
    /// <param name="metric">A metric that this runtime does not transform.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    [InlineData((SimulatorTelemetryMetric)999)]
    public void ApplyWithAnotherMetricThrowsArgumentException(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime();
        var measurement = CreateMeasurement(80) with { Metric = metric };

        var exception = Assert.Throws<ArgumentException>(() =>
            runtime.Apply(measurement, TimeSpan.FromSeconds(60)));

        Assert.Equal("measurement", exception.ParamName);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies that a humidity measurement must carry a numeric value.</summary>
    [Fact]
    public void ApplyWithoutNumericValueThrowsInvalidOperationException()
    {
        var runtime = CreateRuntime();

        Assert.Throws<InvalidOperationException>(() =>
            runtime.Apply(CreateMeasurement(null), TimeSpan.FromSeconds(60)));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies that Apply forwards humidity values to runtime input validation.</summary>
    /// <param name="normalHumidity">A nonfinite or physically invalid measurement value.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-double.Epsilon)]
    [InlineData(-1.0)]
    [InlineData(100.000000001)]
    [InlineData(101.0)]
    public void ApplyWithInvalidNumericValueThrows(double normalHumidity)
    {
        var runtime = CreateRuntime();
        var measurement = CreateMeasurement(normalHumidity);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.Apply(measurement, TimeSpan.FromSeconds(60)));

        Assert.Equal("normalHumidity", exception.ParamName);
        Assert.Equal(normalHumidity, Assert.IsType<double>(exception.ActualValue));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies that Apply preserves elapsed-time validation.</summary>
    [Fact]
    public void ApplyWithNegativeElapsedTimeThrows()
    {
        var runtime = CreateRuntime();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.Apply(CreateMeasurement(80), TimeSpan.FromTicks(-1)));

        Assert.Equal("elapsed", exception.ParamName);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>Verifies that Apply forwards unsupported automatic recovery settings.</summary>
    [Fact]
    public void ApplyWithoutAutomaticRecoveryThrows()
    {
        var runtime = CreateRuntime(CreateDefinition() with
        {
            AutoRecover = false,
            RecoveryDuration = null
        });

        Assert.Throws<NotSupportedException>(() =>
            runtime.Apply(CreateMeasurement(80), TimeSpan.Zero));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentHumidity);
    }

    /// <summary>
    /// Verifies transformation through the common interface preserves measurement
    /// identity, metric, state value, timestamp, and the original input record.
    /// </summary>
    /// <param name="elapsedSeconds">The time of the measurement being transformed.</param>
    /// <param name="normalHumidity">The measurement's normal numeric value.</param>
    /// <param name="expectedHumidity">The independently specified outgoing value.</param>
    /// <param name="expectedPhase">The phase after transformation.</param>
    [Theory]
    [InlineData(0, 80.0, 80.0, ScenarioPhase.Pending)]
    [InlineData(60, 80.0, 92.0, ScenarioPhase.Active)]
    [InlineData(300, 85.0, 86.0, ScenarioPhase.Recovering)]
    [InlineData(361, 85.0, 85.0, ScenarioPhase.Completed)]
    public void ApplyPreservesMeasurementMetadataAcrossPhases(
        int elapsedSeconds, double normalHumidity, double expectedHumidity,
        ScenarioPhase expectedPhase)
    {
        var runtime = CreateFastRuntime();

        if (elapsedSeconds >= 240)
        {
            AssertMeasurement(runtime, 80, 60, 92, ScenarioPhase.Active);
            AssertMeasurement(runtime, 80, 240, 92, ScenarioPhase.Recovering);
        }

        if (elapsedSeconds > 360)
        {
            AssertMeasurement(runtime, 80, 360, 80, ScenarioPhase.Completed);
        }

        var measurement = CreateMeasurement(normalHumidity);
        var snapshot = measurement with { };
        IMeasurementScenarioRuntime contract = runtime;

        var result = contract.Apply(measurement, TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(expectedHumidity, Assert.IsType<double>(result.NumericValue), 10);
        Assert.Equal(measurement.MeasurementId, result.MeasurementId);
        Assert.Equal(measurement.Metric, result.Metric);
        Assert.Equal(measurement.StateValue, result.StateValue);
        Assert.Equal(measurement.MeasuredAtUtc, result.MeasuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.MeasuredAtUtc.Kind);
        Assert.Equal(snapshot, measurement);
        Assert.NotSame(measurement, result);
        AssertCurrentState(runtime, Assert.IsType<double>(result.NumericValue), expectedPhase);
    }

    /// <summary>Verifies that transforming one measurement advances the rise once.</summary>
    [Fact]
    public void ApplyAdvancesHumidityOncePerMeasurement()
    {
        var runtime = CreateRuntime();
        var firstMeasurement = CreateMeasurement(80);
        var secondMeasurement = firstMeasurement with
        {
            MeasurementId = Guid.Parse("30000000-0000-0000-0000-000000000002"),
            NumericValue = 78
        };

        var firstResult = runtime.Apply(firstMeasurement, TimeSpan.FromSeconds(60));
        Assert.Equal(82.0, Assert.IsType<double>(firstResult.NumericValue));
        AssertCurrentState(runtime, 82, ScenarioPhase.Active);

        var secondResult = runtime.Apply(secondMeasurement, TimeSpan.FromSeconds(65));
        Assert.Equal(84.0, Assert.IsType<double>(secondResult.NumericValue));
        AssertCurrentState(runtime, 84, ScenarioPhase.Active);
        Assert.Equal(80.0, Assert.IsType<double>(firstMeasurement.NumericValue));
        Assert.Equal(78.0, Assert.IsType<double>(secondMeasurement.NumericValue));
    }

    /// <summary>Creates an independent runtime with a target of 92 by default.</summary>
    /// <param name="definition">The optional scenario definition override.</param>
    /// <param name="random">The optional random source override.</param>
    /// <returns>A new runtime for the fixture device.</returns>
    private static HighHumidityScenarioRuntime CreateRuntime(
        HighHumidityScenarioDefinition? definition = null, Random? random = null) =>
        new(definition ?? CreateDefinition(), DeviceId, random ?? new SequenceRandom(0.5));

    /// <summary>Creates a runtime whose rise and recovery limits allow the planned values.</summary>
    /// <returns>A runtime with a target of 92 and 100-point per-measurement limits.</returns>
    private static HighHumidityScenarioRuntime CreateFastRuntime() =>
        CreateRuntime(CreateDefinition() with
        {
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        });

    /// <summary>
    /// Creates a scenario starting at 60 seconds and recovering from 240 seconds,
    /// with a planned recovery end at 360 seconds and two-point step limits.
    /// </summary>
    /// <returns>The default definition with valid scenario settings.</returns>
    private static HighHumidityScenarioDefinition CreateDefinition() =>
        new("GradualHighHumidity", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true,
            TimeSpan.FromMinutes(2), 90, 94, 2, 2);

    /// <summary>Creates a humidity measurement with a stable identity and UTC timestamp.</summary>
    /// <param name="humidity">The numeric humidity value, or null for an invalid fixture.</param>
    /// <returns>The measurement supplied to the runtime.</returns>
    private static SimulatorTelemetryMeasurementRequest CreateMeasurement(double? humidity) =>
        new(Guid.Parse("30000000-0000-0000-0000-000000000001"),
            SimulatorTelemetryMetric.Humidity, humidity, null,
            new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc).AddTicks(1234567));

    /// <summary>Checks the returned value and saved state for one calculation.</summary>
    /// <param name="runtime">The runtime to advance.</param>
    /// <param name="normalHumidity">The supplied normal humidity.</param>
    /// <param name="elapsedSeconds">The elapsed time in seconds.</param>
    /// <param name="expectedHumidity">The independently specified expected value.</param>
    /// <param name="expectedPhase">The phase expected after calculation.</param>
    /// <returns>The actual value for additional assertions.</returns>
    private static double AssertMeasurement(
        HighHumidityScenarioRuntime runtime, double normalHumidity, double elapsedSeconds,
        double expectedHumidity, ScenarioPhase expectedPhase)
    {
        var actual = runtime.GetNextHumidity(
            normalHumidity, TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(expectedHumidity, actual, 10);
        AssertCurrentState(runtime, actual, expectedPhase);
        return actual;
    }

    /// <summary>Checks the externally observable saved state.</summary>
    /// <param name="runtime">The runtime to inspect.</param>
    /// <param name="expectedHumidity">The exact expected saved value.</param>
    /// <param name="expectedPhase">The expected execution phase.</param>
    private static void AssertCurrentState(
        HighHumidityScenarioRuntime runtime, double expectedHumidity, ScenarioPhase expectedPhase)
    {
        Assert.Equal(expectedPhase, runtime.Phase);
        Assert.Equal(expectedHumidity, Assert.IsType<double>(runtime.CurrentHumidity));
    }

    /// <summary>Supplies controlled random samples and detects unexpected extra draws.</summary>
    private sealed class SequenceRandom : Random
    {
        private readonly Queue<double> _samples;

        /// <summary>Initializes the samples available to the runtime.</summary>
        /// <param name="samples">Random fractions in the interval [0, 1).</param>
        public SequenceRandom(params double[] samples)
        {
            _samples = new Queue<double>(samples);
        }

        /// <summary>Gets the number of random samples supplied so far.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc />
        public override double NextDouble()
        {
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException("The runtime requested an unexpected random value.");
            }

            CallCount++;
            return _samples.Dequeue();
        }
    }
}
