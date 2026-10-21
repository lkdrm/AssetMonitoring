using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Xunit;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies offline window validation, collection snapshots, interval unions,
/// and separation from measurement scenario processing.
/// </summary>
public sealed class DeviceScenarioScheduleOfflineTests
{
    /// <summary>Identifies the owner of valid fixture schedules and windows.</summary>
    private static readonly Guid DeviceId = Guid.Parse("a6cb56ac-c65e-4c48-9a69-43e9cb7f461a");

    /// <summary>Identifies a device that cannot contribute windows to the fixture schedule.</summary>
    private static readonly Guid OtherDeviceId = Guid.Parse("feb3769b-b259-4160-8079-a0b3df2cb12d");

    /// <summary>Verifies backward compatibility with omitted or explicitly null windows.</summary>
    /// <param name="passNullExplicitly">Whether to supply the optional argument explicitly.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorWithoutOfflineWindowsKeepsDeviceOnline(bool passNullExplicitly)
    {
        var schedule = passNullExplicitly
            ? new DeviceScenarioSchedule(DeviceId, Array.Empty<DeviceMetricScenarioSequence>(), null)
            : new DeviceScenarioSchedule(DeviceId, Array.Empty<DeviceMetricScenarioSequence>());

        Assert.False(schedule.IsOffline(TimeSpan.Zero));
        Assert.False(schedule.IsOffline(TimeSpan.FromHours(1)));
    }

    /// <summary>Verifies that an explicitly empty window collection is valid.</summary>
    [Fact]
    public void ConstructorWithEmptyOfflineWindowsKeepsDeviceOnline()
    {
        var schedule = CreateSchedule();

        Assert.False(schedule.IsOffline(TimeSpan.FromMinutes(2)));
    }

    /// <summary>Verifies that null window entries are rejected as invalid arguments.</summary>
    [Fact]
    public void ConstructorWithNullOfflineWindowThrowsArgumentException()
    {
        var windows = new DeviceOfflineWindow[] { CreateWindow(), null! };

        var exception = Assert.Throws<ArgumentException>(() => new DeviceScenarioSchedule(
            DeviceId, Array.Empty<DeviceMetricScenarioSequence>(), windows));

        Assert.Equal("deviceOfflineWindows", exception.ParamName);
    }

    /// <summary>Verifies that every window must belong to the schedule's device.</summary>
    [Fact]
    public void ConstructorWithOfflineWindowForAnotherDeviceThrowsArgumentException()
    {
        var windows = new[] { CreateWindow(deviceId: OtherDeviceId) };

        var exception = Assert.Throws<ArgumentException>(() => new DeviceScenarioSchedule(
            DeviceId, Array.Empty<DeviceMetricScenarioSequence>(), windows));

        Assert.Equal("deviceOfflineWindows", exception.ParamName);
    }

    /// <summary>
    /// Verifies that removing original entries and adding new entries to the
    /// caller's list cannot change an already constructed schedule.
    /// </summary>
    [Fact]
    public void ConstructorCopiesOfflineWindowsBeforeTheInputListChanges()
    {
        var originalWindow = CreateWindow(startsAfterMinutes: 1, durationMinutes: 2);
        var windows = new List<DeviceOfflineWindow> { originalWindow };
        var schedule = new DeviceScenarioSchedule(
            DeviceId, Array.Empty<DeviceMetricScenarioSequence>(), windows);
        var probeTime = TimeSpan.FromMinutes(2);

        Assert.True(originalWindow.IsActiveAt(probeTime),
            $"The original window must be active at {probeTime}. " +
            $"StartsAfter: {originalWindow.Definition.StartsAfter}; " +
            $"Duration: {originalWindow.Definition.Duration}.");
        Assert.True(schedule.IsOffline(probeTime),
            "The schedule must be offline BEFORE the caller's window list is changed.");

        windows.Clear();
        windows.Add(CreateWindow(startsAfterMinutes: 5, durationMinutes: 1));

        Assert.True(schedule.IsOffline(probeTime),
            "The schedule must remain offline AFTER the caller's list is changed. " +
            "It must retain the original window collection snapshot.");
        Assert.False(schedule.IsOffline(TimeSpan.FromMinutes(5)),
            "A window added to the caller's list after construction must not appear in the schedule.");
    }

    /// <summary>
    /// Verifies that a device may have an outage without any metric scenarios
    /// and that an unconfigured measurement is still returned unchanged by Apply.
    /// </summary>
    [Fact]
    public void ScheduleWithOnlyOfflineWindowsSupportsConnectivityAndMeasurementQueries()
    {
        var schedule = CreateSchedule(CreateWindow());
        var measurement = CreateMeasurement(SimulatorTelemetryMetric.Temperature, 22d, null);
        var elapsed = TimeSpan.FromMinutes(2);

        Assert.True(schedule.IsOffline(elapsed));
        Assert.Same(measurement, schedule.Apply(measurement, elapsed));
    }

    /// <summary>Verifies negative time guards independently of whether any windows exist.</summary>
    /// <param name="hasWindows">Whether the schedule contains a valid offline window.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsOfflineWithNegativeElapsedThrowsArgumentOutOfRangeException(bool hasWindows)
    {
        var schedule = hasWindows ? CreateSchedule(CreateWindow()) : CreateSchedule();
        var elapsed = TimeSpan.FromTicks(-1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => schedule.IsOffline(elapsed));

        Assert.Equal("elapsed", exception.ParamName);
        Assert.Equal(elapsed, Assert.IsType<TimeSpan>(exception.ActualValue));
    }

    /// <summary>Verifies that separate windows leave an online gap between outages.</summary>
    /// <param name="elapsedMinutes">The elapsed simulation time in minutes.</param>
    /// <param name="expected">Whether at least one window is active.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void IsOfflineWithSeparateWindowsPreservesTheOnlineGap(int elapsedMinutes, bool expected)
    {
        var schedule = CreateSchedule(
            CreateWindow(startsAfterMinutes: 1, durationMinutes: 2),
            CreateWindow(startsAfterMinutes: 5, durationMinutes: 1));

        Assert.Equal(expected, schedule.IsOffline(TimeSpan.FromMinutes(elapsedMinutes)));
    }

    /// <summary>Verifies that overlapping outages are combined using their union.</summary>
    /// <param name="elapsedMinutes">The elapsed simulation time in minutes.</param>
    /// <param name="expected">Whether at least one of the overlapping windows is active.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void IsOfflineWithOverlappingWindowsReturnsTrueForTheirUnion(int elapsedMinutes, bool expected)
    {
        var schedule = CreateSchedule(
            CreateWindow(startsAfterMinutes: 1, durationMinutes: 2),
            CreateWindow(startsAfterMinutes: 2, durationMinutes: 2));

        Assert.Equal(expected, schedule.IsOffline(TimeSpan.FromMinutes(elapsedMinutes)));
    }

    /// <summary>Verifies that touching windows do not introduce a gap at their shared boundary.</summary>
    /// <param name="elapsedTicks">The elapsed simulation time in ticks.</param>
    /// <param name="expected">Whether the device is offline at that time.</param>
    [Theory]
    [InlineData(3 * TimeSpan.TicksPerMinute - 1, true)]
    [InlineData(3 * TimeSpan.TicksPerMinute, true)]
    [InlineData(5 * TimeSpan.TicksPerMinute, false)]
    public void IsOfflineWithAdjacentWindowsHasNoGapAtTheSharedBoundary(long elapsedTicks, bool expected)
    {
        var schedule = CreateSchedule(
            CreateWindow(startsAfterMinutes: 1, durationMinutes: 2),
            CreateWindow(startsAfterMinutes: 3, durationMinutes: 2));

        Assert.Equal(expected, schedule.IsOffline(TimeSpan.FromTicks(elapsedTicks)));
    }

    /// <summary>Verifies that connectivity queries do not retain a completed or active phase.</summary>
    [Fact]
    public void IsOfflineCanQueryWindowsInNonchronologicalOrder()
    {
        var schedule = CreateSchedule(
            CreateWindow(startsAfterMinutes: 1, durationMinutes: 2),
            CreateWindow(startsAfterMinutes: 5, durationMinutes: 1));

        Assert.False(schedule.IsOffline(TimeSpan.FromMinutes(6)));
        Assert.True(schedule.IsOffline(TimeSpan.FromMinutes(1)));
        Assert.True(schedule.IsOffline(TimeSpan.FromMinutes(5)));
        Assert.False(schedule.IsOffline(TimeSpan.FromMinutes(4)));
    }

    /// <summary>Verifies that checking connectivity does not execute a measurement scenario.</summary>
    [Fact]
    public void IsOfflineDoesNotAdvanceMeasurementScenarios()
    {
        var schedule = CreateScheduleWithDoorScenario(out var runtime);

        Assert.True(schedule.IsOffline(TimeSpan.FromMinutes(1)));

        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(runtime.CurrentDoorState);
    }

    /// <summary>
    /// Verifies that Apply retains its transformation contract even during an
    /// outage; the runner owns the separate decision to process and send data.
    /// </summary>
    [Fact]
    public void ApplyStillDelegatesToTheMetricSequenceDuringAnOfflineWindow()
    {
        var schedule = CreateScheduleWithDoorScenario(out var runtime);
        var measurement = CreateMeasurement(SimulatorTelemetryMetric.DoorState, null, false);
        var elapsed = TimeSpan.FromMinutes(1);

        Assert.True(schedule.IsOffline(elapsed));

        var result = schedule.Apply(measurement, elapsed);

        Assert.True(result.StateValue is true);
        Assert.False(measurement.StateValue is true);
        Assert.Null(result.NumericValue);
        Assert.Equal(measurement.MeasurementId, result.MeasurementId);
        Assert.Equal(measurement.MeasuredAtUtc, result.MeasuredAtUtc);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>Creates a schedule with no metric scenarios and the supplied windows.</summary>
    /// <param name="windows">The windows to copy into the schedule.</param>
    /// <returns>A schedule owned by the fixture device.</returns>
    private static DeviceScenarioSchedule CreateSchedule(params DeviceOfflineWindow[] windows)
        => new(DeviceId, Array.Empty<DeviceMetricScenarioSequence>(), windows);

    /// <summary>Creates a valid automatically recovering offline window.</summary>
    /// <param name="startsAfterMinutes">The delay before the outage, in minutes.</param>
    /// <param name="durationMinutes">The outage duration, in minutes.</param>
    /// <param name="deviceId">The owner, or null to use the fixture device.</param>
    /// <returns>A new immutable window.</returns>
    private static DeviceOfflineWindow CreateWindow(
        int startsAfterMinutes = 1, int durationMinutes = 2, Guid? deviceId = null)
    {
        var definition = new DeviceOfflineScenarioDefinition(
            Name: $"NetworkOutageAt{startsAfterMinutes}",
            StartsAfter: TimeSpan.FromMinutes(startsAfterMinutes),
            Duration: TimeSpan.FromMinutes(durationMinutes),
            Target: new ScenarioTargetDefinition(ScenarioTargetMode.All),
            AutoRecover: true,
            RecoveryDuration: null);

        return new DeviceOfflineWindow(definition, deviceId ?? DeviceId);
    }

    /// <summary>Creates a schedule containing both a door scenario and an offline window.</summary>
    /// <param name="runtime">The independently observable door runtime.</param>
    /// <returns>A schedule that separates metric processing from connectivity queries.</returns>
    private static DeviceScenarioSchedule CreateScheduleWithDoorScenario(out DoorOpenScenarioRuntime runtime)
    {
        var definition = new DoorOpenScenarioDefinition(
            Name: "OpenDoor",
            StartsAfter: TimeSpan.Zero,
            Duration: TimeSpan.FromMinutes(10),
            Target: new ScenarioTargetDefinition(ScenarioTargetMode.All),
            AutoRecover: true,
            RecoveryDuration: null);
        runtime = new DoorOpenScenarioRuntime(definition, DeviceId);
        var sequence = new DeviceMetricScenarioSequence(
            DeviceId, SimulatorTelemetryMetric.DoorState, [runtime]);

        return new DeviceScenarioSchedule(DeviceId, [sequence], [CreateWindow()]);
    }

    /// <summary>Creates an immutable numeric or state measurement for routing checks.</summary>
    /// <param name="metric">The telemetry metric.</param>
    /// <param name="numericValue">The numeric value, or null for state telemetry.</param>
    /// <param name="stateValue">The state value, or null for numeric telemetry.</param>
    /// <returns>A measurement with a new identifier and a fixed UTC timestamp.</returns>
    private static SimulatorTelemetryMeasurementRequest CreateMeasurement(
        SimulatorTelemetryMetric metric, double? numericValue, bool? stateValue)
        => new(
            Guid.NewGuid(), metric, numericValue, stateValue,
            new DateTime(2026, 10, 21, 20, 0, 0, DateTimeKind.Utc));
}
