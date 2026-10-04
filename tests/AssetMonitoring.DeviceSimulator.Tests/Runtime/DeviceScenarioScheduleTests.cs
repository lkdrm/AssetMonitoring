using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies device ownership, unique metric sequences, measurement routing,
/// independent metric progression, and argument validation.
/// </summary>
/// <remarks>
/// Uses real metric sequences with recording scenario runtimes.
/// Explicit elapsed times replace timers and real delays.
/// The recording runtime isolates schedule behavior from temperature
/// and humidity value-generation algorithms.
/// </remarks>
public sealed class DeviceScenarioScheduleTests
{
    /// <summary>Identifies the device used by the default fixtures.</summary>
    private static readonly Guid DeviceId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");

    /// <summary>Identifies a different device for ownership checks.</summary>
    private static readonly Guid OtherDeviceId =
        Guid.Parse("30000000-0000-0000-0000-000000000002");

    /// <summary>Verifies that a schedule requires a nonempty device identifier.</summary>
    [Fact]
    public void ConstructorWithEmptyDeviceIdThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceScenarioSchedule(Guid.Empty, Array.Empty<DeviceMetricScenarioSequence>()));

        Assert.Equal("deviceId", exception.ParamName);
    }

    /// <summary>Verifies that the sequence collection cannot be null.</summary>
    [Fact]
    public void ConstructorWithNullSequencesThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceScenarioSchedule(DeviceId, null!));

        Assert.Equal("sequences", exception.ParamName);
    }

    /// <summary>Verifies that every collection entry must be a sequence.</summary>
    /// <param name="nullIndex">The position of the null entry.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ConstructorWithNullSequenceThrowsArgumentException(int nullIndex)
    {
        var sequences = new[]
        {
            CreateSchedule(CreateRuntime(SimulatorTelemetryMetric.Temperature)),
            CreateSchedule(CreateRuntime(SimulatorTelemetryMetric.Humidity))
        };
        sequences[nullIndex] = null!;

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceScenarioSchedule(DeviceId, sequences));

        Assert.Equal("sequences", exception.ParamName);
    }

    /// <summary>Verifies ownership of entries after a valid first sequence.</summary>
    [Fact]
    public void ConstructorWithSequenceForAnotherDeviceThrowsArgumentException()
    {
        var own = CreateRuntime(SimulatorTelemetryMetric.Temperature);
        var foreign = CreateRuntime(SimulatorTelemetryMetric.Humidity, deviceId: OtherDeviceId);

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceScenarioSchedule(DeviceId, new[]
            {
                CreateSchedule(own), CreateSchedule(foreign)
            }));

        Assert.Equal("sequences", exception.ParamName);
        Assert.Empty(own.Calls);
        Assert.Empty(foreign.Calls);
    }

    /// <summary>Verifies that distinct sequences cannot share a metric.</summary>
    /// <param name="metric">The metric duplicated by the two sequences.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void ConstructorWithDuplicateMetricThrowsArgumentException(SimulatorTelemetryMetric metric)
    {
        var first = CreateSchedule(CreateRuntime(metric));
        var second = CreateSchedule(CreateRuntime(metric));

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceScenarioSchedule(DeviceId, new[] { first, second }));

        Assert.Equal("sequences", exception.ParamName);
    }

    /// <summary>Verifies that adding the same sequence twice is also rejected.</summary>
    [Fact]
    public void ConstructorWithRepeatedSequenceThrowsArgumentException()
    {
        var sequence = CreateSchedule(CreateRuntime(SimulatorTelemetryMetric.Temperature));

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceScenarioSchedule(DeviceId, new[] { sequence, sequence }));

        Assert.Equal("sequences", exception.ParamName);
    }

    /// <summary>Verifies that an empty schedule retains its device and passes measurements through.</summary>
    [Fact]
    public void ConstructorWithEmptySequencesAllowsNormalMeasurements()
    {
        var schedule = new DeviceScenarioSchedule(DeviceId, Array.Empty<DeviceMetricScenarioSequence>());
        var measurement = CreateMeasurement(SimulatorTelemetryMetric.Temperature);

        var result = schedule.Apply(measurement, TimeSpan.FromSeconds(60));

        Assert.Equal(DeviceId, schedule.DeviceId);
        Assert.Same(measurement, result);
    }

    /// <summary>Verifies that later changes to the supplied collection do not remove stored sequences.</summary>
    [Fact]
    public void ConstructorCopiesCollectionAndReusesSequenceInstances()
    {
        var runtime = CreateRuntime(SimulatorTelemetryMetric.Temperature);
        var sequence = CreateSchedule(runtime);
        var supplied = new List<DeviceMetricScenarioSequence> { sequence };
        var schedule = new DeviceScenarioSchedule(DeviceId, supplied);

        Assert.Single(supplied);
        Assert.Same(sequence, supplied[0]);
        Assert.Empty(runtime.Calls);
        supplied.Clear();

        var result = schedule.Apply(CreateMeasurement(runtime.Metric), TimeSpan.Zero);

        Assert.Single(runtime.Calls);
        Assert.Same(runtime.LastResult, result);
        Assert.Same(runtime, sequence.CurrentScenario);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>Verifies that only the matching metric is processed, including state metrics.</summary>
    /// <param name="metric">The metric of the measurement being routed.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void ApplyRoutesOnlyMatchingMetricAndReturnsItsResult(SimulatorTelemetryMetric metric)
    {
        var runtimes = new[]
        {
            CreateRuntime(SimulatorTelemetryMetric.LightState),
            CreateRuntime(SimulatorTelemetryMetric.Humidity),
            CreateRuntime(SimulatorTelemetryMetric.DoorState),
            CreateRuntime(SimulatorTelemetryMetric.Temperature)
        };
        var schedule = new DeviceScenarioSchedule(DeviceId, runtimes.Select(CreateSchedule));
        var measurement = CreateMeasurement(metric);

        var result = schedule.Apply(measurement, TimeSpan.Zero);

        var selected = Assert.Single(runtimes.Where(runtime => runtime.Metric == metric));
        var call = Assert.Single(selected.Calls);
        Assert.Same(measurement, call.Measurement);
        Assert.Equal(TimeSpan.Zero, call.Elapsed);
        Assert.Same(selected.LastResult, result);
        Assert.NotSame(measurement, result);
        Assert.Equal(measurement.MeasurementId, result.MeasurementId);
        Assert.Equal(measurement.Metric, result.Metric);
        Assert.Equal(measurement.MeasuredAtUtc, result.MeasuredAtUtc);

        if (metric == SimulatorTelemetryMetric.Temperature || metric == SimulatorTelemetryMetric.Humidity)
        {
            Assert.Equal(metric == SimulatorTelemetryMetric.Temperature ? 34.0 : 94.0,
                Assert.IsType<double>(result.NumericValue));
            Assert.Equal(metric == SimulatorTelemetryMetric.Temperature ? 22.0 : 80.0,
                Assert.IsType<double>(measurement.NumericValue));
            Assert.Null(result.StateValue);
        }
        else
        {
            Assert.True(Assert.IsType<bool>(result.StateValue));
            Assert.False(Assert.IsType<bool>(measurement.StateValue));
            Assert.Null(result.NumericValue);
        }

        foreach (var other in runtimes.Where(runtime => runtime.Metric != metric))
        {
            Assert.Empty(other.Calls);
            Assert.Equal(ScenarioPhase.Pending, other.Phase);
        }
    }

    /// <summary>Verifies that an unconfigured metric returns the same original measurement.</summary>
    /// <param name="metric">A metric without a sequence in the schedule.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void ApplyWithoutMatchingSequenceReturnsSameMeasurement(SimulatorTelemetryMetric metric)
    {
        var temperature = CreateRuntime(SimulatorTelemetryMetric.Temperature);
        var schedule = new DeviceScenarioSchedule(DeviceId, new[] { CreateSchedule(temperature) });
        var measurement = CreateMeasurement(metric);

        var result = schedule.Apply(measurement, TimeSpan.FromSeconds(60));

        Assert.Same(measurement, result);
        Assert.Empty(temperature.Calls);
        Assert.Equal(ScenarioPhase.Pending, temperature.Phase);
    }

    /// <summary>Verifies that null input is rejected before advancing any runtime.</summary>
    [Fact]
    public void ApplyWithNullMeasurementThrowsArgumentNullException()
    {
        var runtime = CreateRuntime(SimulatorTelemetryMetric.Temperature);
        var schedule = new DeviceScenarioSchedule(DeviceId, new[] { CreateSchedule(runtime) });

        var exception = Assert.Throws<ArgumentNullException>(() =>
            schedule.Apply(null!, TimeSpan.Zero));

        Assert.Equal("measurement", exception.ParamName);
        Assert.Empty(runtime.Calls);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
    }

    /// <summary>Verifies negative time is rejected whether or not the metric is configured.</summary>
    /// <param name="metric">A configured or unconfigured metric.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyWithNegativeElapsedThrowsBeforeRouting(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(SimulatorTelemetryMetric.Temperature);
        var schedule = new DeviceScenarioSchedule(DeviceId, new[] { CreateSchedule(runtime) });
        var elapsed = TimeSpan.FromTicks(-1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            schedule.Apply(CreateMeasurement(metric), elapsed));

        Assert.Equal("elapsed", exception.ParamName);
        Assert.Equal(elapsed, Assert.IsType<TimeSpan>(exception.ActualValue));
        Assert.Empty(runtime.Calls);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
    }

    /// <summary>Verifies that empty schedules still enforce elapsed-time validation.</summary>
    [Fact]
    public void ApplyWithNegativeElapsedAndEmptyScheduleThrowsArgumentOutOfRangeException()
    {
        var schedule = new DeviceScenarioSchedule(DeviceId, Array.Empty<DeviceMetricScenarioSequence>());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            schedule.Apply(CreateMeasurement(SimulatorTelemetryMetric.Temperature), TimeSpan.FromTicks(-1)));

        Assert.Equal("elapsed", exception.ParamName);
    }

    /// <summary>Verifies that the schedule preserves the sequence's configured start boundary.</summary>
    [Fact]
    public void ApplyWaitsUntilTheConfiguredStartAndThenActivates()
    {
        var startsAfter = TimeSpan.FromSeconds(60);
        var runtime = CreateRuntime(SimulatorTelemetryMetric.Temperature, startsAfter);
        var schedule = new DeviceScenarioSchedule(DeviceId, new[] { CreateSchedule(runtime) });
        var pendingMeasurement = CreateMeasurement(runtime.Metric);

        var pendingResult = schedule.Apply(pendingMeasurement, startsAfter - TimeSpan.FromTicks(1));

        Assert.Same(pendingMeasurement, pendingResult);
        Assert.Empty(runtime.Calls);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);

        var activeMeasurement = CreateMeasurement(runtime.Metric);
        var activeResult = schedule.Apply(activeMeasurement, startsAfter);

        var call = Assert.Single(runtime.Calls);
        Assert.Equal(startsAfter, call.Elapsed);
        Assert.Same(activeMeasurement, call.Measurement);
        Assert.Same(runtime.LastResult, activeResult);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>Verifies that elapsed time is not reset, scaled, or offset by the schedule.</summary>
    [Fact]
    public void ApplyPreservesElapsedTimeAcrossSuccessiveMeasurements()
    {
        var runtime = CreateRuntime(SimulatorTelemetryMetric.Temperature, TimeSpan.FromSeconds(60));
        var schedule = new DeviceScenarioSchedule(DeviceId, new[] { CreateSchedule(runtime) });

        schedule.Apply(CreateMeasurement(runtime.Metric), TimeSpan.FromSeconds(60));
        schedule.Apply(CreateMeasurement(runtime.Metric), TimeSpan.FromSeconds(90));

        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), runtime.Calls[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(90), runtime.Calls[1].Elapsed);
    }

    /// <summary>Verifies interleaved metrics retain independent actual starts and runtime state.</summary>
    [Fact]
    public void ApplyAdvancesTemperatureAndHumidityIndependently()
    {
        var temperature = CreateRuntime(SimulatorTelemetryMetric.Temperature, TimeSpan.FromSeconds(60));
        var humidity = CreateRuntime(SimulatorTelemetryMetric.Humidity, TimeSpan.FromSeconds(100));
        var schedule = new DeviceScenarioSchedule(DeviceId, new[]
        {
            CreateSchedule(temperature), CreateSchedule(humidity)
        });

        schedule.Apply(CreateMeasurement(temperature.Metric), TimeSpan.FromSeconds(60));
        Assert.Empty(humidity.Calls);
        Assert.Equal(ScenarioPhase.Pending, humidity.Phase);

        schedule.Apply(CreateMeasurement(humidity.Metric), TimeSpan.FromSeconds(120));
        Assert.Single(temperature.Calls);
        Assert.Equal(ScenarioPhase.Active, temperature.Phase);
        Assert.Equal(ScenarioPhase.Active, humidity.Phase);

        schedule.Apply(CreateMeasurement(temperature.Metric), TimeSpan.FromSeconds(125));
        schedule.Apply(CreateMeasurement(humidity.Metric), TimeSpan.FromSeconds(130));

        Assert.Equal(2, temperature.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), temperature.Calls[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(125), temperature.Calls[1].Elapsed);
        Assert.Equal(2, humidity.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(100), humidity.Calls[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(110), humidity.Calls[1].Elapsed);
    }

    /// <summary>Verifies that an exhausted matching sequence also passes through the original instance.</summary>
    [Fact]
    public void ApplyAfterSequenceCompletionReturnsSameNormalMeasurement()
    {
        var runtime = CreateRuntime(SimulatorTelemetryMetric.Temperature, completeOnApply: true);
        var sequence = CreateSchedule(runtime);
        var schedule = new DeviceScenarioSchedule(DeviceId, new[] { sequence });

        schedule.Apply(CreateMeasurement(runtime.Metric), TimeSpan.Zero);
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
        Assert.Null(sequence.CurrentScenario);

        var normalMeasurement = CreateMeasurement(runtime.Metric);
        var result = schedule.Apply(normalMeasurement, TimeSpan.FromSeconds(1));

        Assert.Same(normalMeasurement, result);
        Assert.Single(runtime.Calls);
    }

    /// <summary>Verifies that a sequence failure is propagated without advancing other metrics.</summary>
    [Fact]
    public void ApplyPropagatesRuntimeFailureWithoutAdvancingAnotherMetric()
    {
        var failure = new InvalidOperationException("Scenario failure.");
        var temperature = CreateRuntime(SimulatorTelemetryMetric.Temperature, failure: failure);
        var humidity = CreateRuntime(SimulatorTelemetryMetric.Humidity);
        var schedule = new DeviceScenarioSchedule(DeviceId, new[]
        {
            CreateSchedule(temperature), CreateSchedule(humidity)
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            schedule.Apply(CreateMeasurement(temperature.Metric), TimeSpan.Zero));

        Assert.Same(failure, exception);
        Assert.Single(temperature.Calls);
        Assert.Empty(humidity.Calls);
        Assert.Equal(ScenarioPhase.Pending, humidity.Phase);
    }

    /// <summary>Creates one recording scenario with configurable start and completion behavior.</summary>
    /// <param name="metric">The metric changed by the scenario.</param>
    /// <param name="startsAfter">The start delay, or null for immediate activation.</param>
    /// <param name="deviceId">The owning device, or null for the fixture device.</param>
    /// <param name="completeOnApply">Whether the first application completes the scenario.</param>
    /// <param name="failure">The exception to throw on application, or null for success.</param>
    /// <returns>A new independent recording runtime.</returns>
    private static RecordingRuntime CreateRuntime(
        SimulatorTelemetryMetric metric,
        TimeSpan? startsAfter = null,
        Guid? deviceId = null,
        bool completeOnApply = false,
        Exception? failure = null) =>
        new(deviceId ?? DeviceId, metric, startsAfter ?? TimeSpan.Zero, completeOnApply, failure);

    /// <summary>Wraps a recording runtime in a real sequence for its device and metric.</summary>
    /// <param name="runtime">The sole scenario in the sequence.</param>
    /// <returns>A new metric sequence retaining the supplied runtime.</returns>
    private static DeviceMetricScenarioSequence CreateSchedule(RecordingRuntime runtime) =>
        new(runtime.DeviceId, runtime.Metric, new IMeasurementScenarioRuntime[] { runtime });

    /// <summary>Creates a fresh normal measurement for one telemetry metric.</summary>
    /// <param name="metric">The measurement metric.</param>
    /// <returns>A measurement with a unique identifier and a fixed UTC timestamp.</returns>
    private static SimulatorTelemetryMeasurementRequest CreateMeasurement(SimulatorTelemetryMetric metric) =>
        new(
            Guid.NewGuid(),
            metric,
            metric switch
            {
                SimulatorTelemetryMetric.Temperature => 22.0,
                SimulatorTelemetryMetric.Humidity => 80.0,
                _ => null
            },
            metric == SimulatorTelemetryMetric.DoorState || metric == SimulatorTelemetryMetric.LightState
                ? false : null,
            new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>Provides configuration for a recording scenario without depending on a production scenario type.</summary>
    private sealed record RecordingDefinition : ScenarioDefinition
    {
        /// <summary>Initializes the common scenario timing used by the fixture.</summary>
        /// <param name="startsAfter">The configured delay before eligibility.</param>
        public RecordingDefinition(TimeSpan startsAfter)
            : base("RecordingScenario", startsAfter, TimeSpan.FromMinutes(1),
                new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromMinutes(1))
        {
        }
    }

    /// <summary>Records routed calls and supplies a predictable numeric or state transformation.</summary>
    private sealed class RecordingRuntime : IMeasurementScenarioRuntime
    {
        private readonly bool _completeOnApply;
        private readonly Exception? _failure;

        /// <inheritdoc />
        public Guid DeviceId { get; }

        /// <inheritdoc />
        public ScenarioDefinition Definition { get; }

        /// <inheritdoc />
        public SimulatorTelemetryMetric Metric { get; }

        /// <inheritdoc />
        public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Pending;

        /// <summary>Gets the measurements and scenario times received by this runtime.</summary>
        public List<(SimulatorTelemetryMeasurementRequest Measurement, TimeSpan Elapsed)> Calls { get; } = new();

        /// <summary>Gets the exact instance returned by the last successful application.</summary>
        public SimulatorTelemetryMeasurementRequest? LastResult { get; private set; }

        /// <summary>Initializes the recording runtime.</summary>
        /// <param name="deviceId">The owning device identifier.</param>
        /// <param name="metric">The metric processed by the runtime.</param>
        /// <param name="startsAfter">The configured scenario start delay.</param>
        /// <param name="completeOnApply">Whether a successful application completes the scenario.</param>
        /// <param name="failure">The failure to propagate, or null for success.</param>
        public RecordingRuntime(Guid deviceId, SimulatorTelemetryMetric metric,
            TimeSpan startsAfter, bool completeOnApply, Exception? failure)
        {
            DeviceId = deviceId;
            Metric = metric;
            Definition = new RecordingDefinition(startsAfter);
            _completeOnApply = completeOnApply;
            _failure = failure;
        }

        /// <inheritdoc />
        public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
        {
            Calls.Add((measurement, elapsed));

            if (_failure is not null)
            {
                throw _failure;
            }

            Phase = _completeOnApply ? ScenarioPhase.Completed : ScenarioPhase.Active;
            var result = Metric switch
            {
                SimulatorTelemetryMetric.Temperature => measurement with { NumericValue = 34.0 },
                SimulatorTelemetryMetric.Humidity => measurement with { NumericValue = 94.0 },
                _ => measurement with { StateValue = true }
            };

            LastResult = result;
            return result;
        }
    }
}
