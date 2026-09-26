using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies device ownership, stable ordering, sequential scenario execution,
/// delayed starts, recovery blocking, and normal telemetry between scenarios.
/// </summary>
/// <remarks>
/// Tests exercise the public sequence API with real scenario runtimes,
/// explicit elapsed times, and deterministic abnormal targets.
/// Every GetNextTemperature call represents a new measurement.
/// No HTTP requests, timers, real delays, or additional packages are required.
/// </remarks>
public sealed class DeviceTemperatureScenarioSequenceTests
{
    /// <summary>Identifies the device used by the default fixtures.</summary>
    private static readonly Guid DeviceId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");

    /// <summary>Verifies that a sequence requires a nonempty device identifier.</summary>
    [Fact]
    public void ConstructorWithEmptyDeviceIdThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceTemperatureScenarioSequence(Guid.Empty, Array.Empty<HighTemperatureScenarioRuntime>()));

        Assert.Equal("deviceId", exception.ParamName);
    }

    /// <summary>Verifies that a null collection is rejected.</summary>
    [Fact]
    public void ConstructorWithNullScenariosThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceTemperatureScenarioSequence(DeviceId, null!));

        Assert.Equal("scenarios", exception.ParamName);
    }

    /// <summary>Verifies that every collection entry must be a runtime.</summary>
    /// <param name="nullIndex">The position of the null entry.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ConstructorWithNullScenarioThrowsArgumentException(int nullIndex)
    {
        var scenarios = new[] { CreateRuntime(), CreateRuntime() };
        scenarios[nullIndex] = null!;

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceTemperatureScenarioSequence(DeviceId, scenarios));

        Assert.Equal("scenarios", exception.ParamName);
    }

    /// <summary>
    /// Verifies ownership for all entries, including a foreign device after
    /// a valid first scenario.
    /// </summary>
    [Fact]
    public void ConstructorWithScenarioForAnotherDeviceThrowsArgumentException()
    {
        var own = CreateRuntime();
        var foreign = CreateRuntime(deviceId:
            Guid.Parse("20000000-0000-0000-0000-000000000002"));

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceTemperatureScenarioSequence(DeviceId, new[] { own, foreign }));

        Assert.Equal("scenarios", exception.ParamName);
        AssertPending(own);
        AssertPending(foreign);
    }

    /// <summary>
    /// Verifies chronological ordering without changing the caller's list,
    /// and verifies that later mutations of that list cannot change the queue.
    /// </summary>
    [Fact]
    public void ConstructorCopiesAndOrdersScenariosWithoutMutatingCallerCollection()
    {
        var earlier = CreateRuntime();
        var later = CreateRuntime(CreateDefinition() with
        {
            Name = "Later",
            StartsAfter = TimeSpan.FromSeconds(120)
        });
        var supplied = new List<HighTemperatureScenarioRuntime> { later, earlier };
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, supplied);

        Assert.Same(later, supplied[0]);
        Assert.Same(earlier, supplied[1]);
        supplied.Clear();

        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(60)));
        AssertState(earlier, ScenarioPhase.Active, 32);
        AssertPending(later);

        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(120)));
        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(180)));
        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(181)));
        AssertState(later, ScenarioPhase.Active, 32);
    }

    /// <summary>
    /// Verifies stable input order when planned starts are equal. Completion
    /// does not process the next scenario within the same measurement.
    /// A separate measurement may have the same elapsed timestamp.
    /// </summary>
    /// <param name="reverse">Whether to reverse the order of the two runtimes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetNextTemperaturePreservesInputOrderForEqualStartTimes(bool reverse)
    {
        var cooler = CreateRuntime(CreateDefinition() with { Name = "Zeta" });
        var hotter = CreateRuntime(CreateDefinition() with
        {
            Name = "Alpha",
            AbnormalMinimum = 40,
            AbnormalMaximum = 44
        });
        var first = reverse ? hotter : cooler;
        var second = reverse ? cooler : hotter;
        var firstTarget = reverse ? 42.0 : 32.0;
        var secondTarget = reverse ? 32.0 : 42.0;
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { first, second });

        Assert.Equal(firstTarget, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(60)));
        AssertState(first, ScenarioPhase.Active, firstTarget);
        AssertPending(second);
        Assert.Equal(firstTarget, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(120)));
        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(180)));
        AssertState(first, ScenarioPhase.Completed, 22);
        AssertPending(second);

        // A new call is a new measurement, even when no simulated time passes.
        Assert.Equal(secondTarget, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(180)));
        AssertState(second, ScenarioPhase.Active, secondTarget);
        AssertState(first, ScenarioPhase.Completed, 22);
    }

    /// <summary>Verifies normal telemetry for a sequence with no scenarios.</summary>
    /// <param name="normalTemperature">The finite value that must pass through unchanged.</param>
    [Theory]
    [InlineData(-5.5)]
    [InlineData(0.0)]
    [InlineData(23.45)]
    public void GetNextTemperatureWithEmptySequenceReturnsNormalTemperature(double normalTemperature)
    {
        var sequence = new DeviceTemperatureScenarioSequence(
            DeviceId, Array.Empty<HighTemperatureScenarioRuntime>());

        Assert.Equal(normalTemperature, sequence.GetNextTemperature(normalTemperature, TimeSpan.Zero));
        Assert.Equal(normalTemperature, sequence.GetNextTemperature(normalTemperature, TimeSpan.FromHours(1)));
    }

    /// <summary>Verifies rejection of nonfinite temperatures before any scenario advances.</summary>
    /// <param name="normalTemperature">The invalid temperature.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GetNextTemperatureWithNonfiniteTemperatureThrowsArgumentOutOfRangeException(
        double normalTemperature)
    {
        var runtime = CreateRuntime();
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { runtime });

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            sequence.GetNextTemperature(normalTemperature, TimeSpan.FromSeconds(60)));

        Assert.Equal("normalTemperature", exception.ParamName);
        AssertPending(runtime);
    }

    /// <summary>Verifies validation even when the queue is empty.</summary>
    [Fact]
    public void GetNextTemperatureWithEmptySequenceStillRejectsNonfiniteTemperature()
    {
        var sequence = new DeviceTemperatureScenarioSequence(
            DeviceId, Array.Empty<HighTemperatureScenarioRuntime>());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            sequence.GetNextTemperature(double.NaN, TimeSpan.Zero));

        Assert.Equal("normalTemperature", exception.ParamName);
    }

    /// <summary>Verifies that elapsed time cannot be negative, even for an empty queue.</summary>
    /// <param name="ticks">The negative number of elapsed ticks.</param>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-600000000L)]
    public void GetNextTemperatureWithNegativeElapsedTimeThrowsArgumentOutOfRangeException(long ticks)
    {
        var sequence = new DeviceTemperatureScenarioSequence(
            DeviceId, Array.Empty<HighTemperatureScenarioRuntime>());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            sequence.GetNextTemperature(22, TimeSpan.FromTicks(ticks)));

        Assert.Equal("elapsed", exception.ParamName);
    }

    /// <summary>
    /// Verifies waiting before the start, retention of the actual start across
    /// calls, all phase transitions, and normal values after the queue ends.
    /// </summary>
    [Fact]
    public void GetNextTemperatureRetainsActualStartAcrossPhaseTransitions()
    {
        var runtime = CreateRuntime();
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { runtime });

        Assert.Equal(23.0, sequence.GetNextTemperature(23, TimeSpan.Zero));
        Assert.Equal(21.0, sequence.GetNextTemperature(21, TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1)));
        AssertPending(runtime);

        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(60)));
        AssertState(runtime, ScenarioPhase.Active, 32);
        Assert.Equal(32.0, sequence.GetNextTemperature(24, TimeSpan.FromSeconds(90)));
        AssertState(runtime, ScenarioPhase.Active, 32);
        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(120)));
        AssertState(runtime, ScenarioPhase.Recovering, 32);
        Assert.Equal(27.0, sequence.GetNextTemperature(24, TimeSpan.FromSeconds(150)));
        AssertState(runtime, ScenarioPhase.Recovering, 27);
        Assert.Equal(22.0, sequence.GetNextTemperature(20, TimeSpan.FromSeconds(180)));
        AssertState(runtime, ScenarioPhase.Completed, 22);

        Assert.Equal(24.0, sequence.GetNextTemperature(24, TimeSpan.FromSeconds(181)));
        Assert.Equal(20.0, sequence.GetNextTemperature(20, TimeSpan.FromSeconds(182)));
        AssertState(runtime, ScenarioPhase.Completed, 22);
    }

    /// <summary>
    /// Verifies a full active duration when the first measurement occurs late,
    /// even after the scenario's original planned end and recovery end.
    /// </summary>
    /// <param name="actualStartSeconds">The first eligible measurement time.</param>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(600)]
    public void GetNextTemperatureWithLateFirstMeasurementPreservesFullActiveDuration(int actualStartSeconds)
    {
        var runtime = CreateRuntime();
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { runtime });
        var actualStart = TimeSpan.FromSeconds(actualStartSeconds);

        Assert.Equal(32.0, sequence.GetNextTemperature(22, actualStart));
        AssertState(runtime, ScenarioPhase.Active, 32);
        Assert.Equal(32.0, sequence.GetNextTemperature(
            22, actualStart + TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1)));
        AssertState(runtime, ScenarioPhase.Active, 32);
        Assert.Equal(32.0, sequence.GetNextTemperature(22, actualStart + TimeSpan.FromSeconds(60)));
        AssertState(runtime, ScenarioPhase.Recovering, 32);
        Assert.Equal(22.0, sequence.GetNextTemperature(22, actualStart + TimeSpan.FromSeconds(120)));
        AssertState(runtime, ScenarioPhase.Completed, 22);
    }

    /// <summary>
    /// Verifies that an overlapping scenario waits for its predecessor's
    /// completion, starts on the next measurement, and retains its full duration.
    /// </summary>
    [Fact]
    public void GetNextTemperatureDelaysOverlappingScenarioUntilPreviousRecoveryCompletes()
    {
        var first = CreateRuntime();
        var second = CreateRuntime(CreateDefinition() with
        {
            Name = "Second",
            StartsAfter = TimeSpan.FromSeconds(90),
            AbnormalMinimum = 40,
            AbnormalMaximum = 44
        });
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { first, second });

        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(60)));
        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(120)));
        AssertPending(second);
        Assert.Equal(27.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(150)));
        AssertPending(second);
        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(180)));
        AssertState(first, ScenarioPhase.Completed, 22);
        AssertPending(second);

        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(181)));
        AssertState(second, ScenarioPhase.Active, 42);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(240)));
        AssertState(second, ScenarioPhase.Active, 42);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(241)));
        AssertState(second, ScenarioPhase.Recovering, 42);
        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(271)));
        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(301)));
        AssertState(second, ScenarioPhase.Completed, 22);
        Assert.Equal(23.0, sequence.GetNextTemperature(23, TimeSpan.FromSeconds(302)));
    }

    /// <summary>
    /// Verifies that the next scenario remains blocked when a recovery step
    /// limit extends the predecessor beyond its planned recovery duration.
    /// </summary>
    [Fact]
    public void GetNextTemperatureWaitsForActualRecoveryCompletionBeyondPlannedDeadline()
    {
        var first = CreateRuntime(CreateDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            MaximumRecoveryPerMeasurement = 2
        });
        var second = CreateRuntime(CreateDefinition() with
        {
            Name = "WaitingScenario",
            AbnormalMinimum = 40,
            AbnormalMaximum = 44
        });
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { first, second });

        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.Zero));
        var recoverySteps = new[]
        {
            (Seconds: 60, Temperature: 32.0),
            (Seconds: 90, Temperature: 30.0),
            (Seconds: 120, Temperature: 28.0),
            (Seconds: 150, Temperature: 26.0),
            (Seconds: 180, Temperature: 24.0)
        };
        foreach (var step in recoverySteps)
        {
            Assert.Equal(step.Temperature,
                sequence.GetNextTemperature(22, TimeSpan.FromSeconds(step.Seconds)));
            AssertState(first, ScenarioPhase.Recovering, step.Temperature);
            AssertPending(second);
        }

        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(210)));
        AssertState(first, ScenarioPhase.Completed, 22);
        AssertPending(second);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(211)));
        AssertState(second, ScenarioPhase.Active, 42);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(270)));
        AssertState(second, ScenarioPhase.Active, 42);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(271)));
        AssertState(second, ScenarioPhase.Recovering, 42);
    }

    /// <summary>
    /// Verifies normal telemetry during a gap between scenarios and a fresh
    /// actual start when the next scenario becomes eligible.
    /// </summary>
    [Fact]
    public void GetNextTemperatureReturnsNormalValuesWhileWaitingForNextScheduledStart()
    {
        var first = CreateRuntime(CreateDefinition() with { StartsAfter = TimeSpan.Zero });
        var second = CreateRuntime(CreateDefinition() with
        {
            Name = "LaterScenario",
            StartsAfter = TimeSpan.FromSeconds(300),
            AbnormalMinimum = 40,
            AbnormalMaximum = 44
        });
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { first, second });

        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.Zero));
        Assert.Equal(32.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(60)));
        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(120)));
        Assert.Equal(21.0, sequence.GetNextTemperature(21, TimeSpan.FromSeconds(180)));
        Assert.Equal(23.0, sequence.GetNextTemperature(
            23, TimeSpan.FromSeconds(300) - TimeSpan.FromTicks(1)));
        AssertPending(second);
        AssertState(first, ScenarioPhase.Completed, 22);

        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(300)));
        AssertState(second, ScenarioPhase.Active, 42);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(359)));
        AssertState(second, ScenarioPhase.Active, 42);
        Assert.Equal(42.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(360)));
        AssertState(second, ScenarioPhase.Recovering, 42);
    }

    /// <summary>
    /// Verifies that a completed sequence still validates input and does not
    /// resume or modify its completed runtime after a rejected call.
    /// </summary>
    [Fact]
    public void GetNextTemperatureAfterCompletionStillValidatesInput()
    {
        var runtime = CreateRuntime(CreateDefinition() with { StartsAfter = TimeSpan.Zero });
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { runtime });
        _ = sequence.GetNextTemperature(22, TimeSpan.Zero);
        _ = sequence.GetNextTemperature(22, TimeSpan.FromSeconds(60));
        Assert.Equal(22.0, sequence.GetNextTemperature(22, TimeSpan.FromSeconds(120)));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            sequence.GetNextTemperature(double.PositiveInfinity, TimeSpan.FromSeconds(121)));

        Assert.Equal("normalTemperature", exception.ParamName);
        Assert.Equal(23.0, sequence.GetNextTemperature(23, TimeSpan.FromSeconds(122)));
        AssertState(runtime, ScenarioPhase.Completed, 22);
    }

    /// <summary>
    /// Verifies that a runtime failure propagates and does not silently skip
    /// the failing scenario or start its successor.
    /// </summary>
    [Fact]
    public void GetNextTemperatureWithUnsupportedRecoveryPropagatesFailureWithoutSkippingScenario()
    {
        var failing = CreateRuntime(CreateDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            AutoRecover = false,
            RecoveryDuration = null
        });
        var waiting = CreateRuntime(CreateDefinition() with { StartsAfter = TimeSpan.Zero });
        var sequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { failing, waiting });

        Assert.Throws<NotSupportedException>(() => sequence.GetNextTemperature(22, TimeSpan.Zero));
        AssertPending(waiting);
        Assert.Throws<NotSupportedException>(() => sequence.GetNextTemperature(22, TimeSpan.FromSeconds(1)));
        AssertPending(waiting);
    }

    /// <summary>Verifies independent queue and temperature state for different devices.</summary>
    [Fact]
    public void GetNextTemperatureKeepsDeviceSequencesIndependent()
    {
        var otherDeviceId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var definition = CreateDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            MaximumRisePerMeasurement = 2
        };
        var first = CreateRuntime(definition);
        var second = CreateRuntime(definition, otherDeviceId);
        var firstSequence = new DeviceTemperatureScenarioSequence(DeviceId, new[] { first });
        var secondSequence = new DeviceTemperatureScenarioSequence(otherDeviceId, new[] { second });

        Assert.Equal(22.0, firstSequence.GetNextTemperature(20, TimeSpan.Zero));
        AssertPending(second);
        Assert.Equal(26.0, secondSequence.GetNextTemperature(24, TimeSpan.Zero));
        AssertState(first, ScenarioPhase.Active, 22);
        Assert.Equal(24.0, firstSequence.GetNextTemperature(20, TimeSpan.FromSeconds(30)));
        AssertState(second, ScenarioPhase.Active, 26);
    }

    /// <summary>Creates a runtime with deterministic midpoint target selection.</summary>
    /// <param name="definition">The definition override, or null for the default.</param>
    /// <param name="deviceId">The device override, or null for the fixture device.</param>
    /// <returns>A fresh runtime with its own temperature state.</returns>
    private static HighTemperatureScenarioRuntime CreateRuntime(
        HighTemperatureScenarioDefinition? definition = null,
        Guid? deviceId = null) =>
        new(definition ?? CreateDefinition(), deviceId ?? DeviceId, new FixedRandom());

    /// <summary>
    /// Creates a scenario starting at 60 seconds, with 60 seconds of active time,
    /// 60 seconds of recovery, a target of 32, and large enough steps for exact traces.
    /// </summary>
    /// <returns>A valid automatic-recovery definition.</returns>
    private static HighTemperatureScenarioDefinition CreateDefinition() =>
        new(
            "TemperatureScenario",
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true,
            TimeSpan.FromSeconds(60),
            30,
            34,
            20,
            20);

    /// <summary>Checks that a queued runtime has never processed a measurement.</summary>
    /// <param name="runtime">The waiting runtime to inspect.</param>
    private static void AssertPending(HighTemperatureScenarioRuntime runtime)
    {
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentTemperature);
    }

    /// <summary>Checks the runtime state exposed after processing a measurement.</summary>
    /// <param name="runtime">The runtime to inspect.</param>
    /// <param name="phase">The expected phase.</param>
    /// <param name="temperature">The expected saved temperature.</param>
    private static void AssertState(
        HighTemperatureScenarioRuntime runtime, ScenarioPhase phase, double temperature)
    {
        Assert.Equal(phase, runtime.Phase);
        Assert.Equal(temperature, Assert.IsType<double>(runtime.CurrentTemperature));
    }

    /// <summary>Supplies the midpoint of the configured abnormal range.</summary>
    private sealed class FixedRandom : Random
    {
        /// <inheritdoc />
        public override double NextDouble() => 0.5;
    }
}
