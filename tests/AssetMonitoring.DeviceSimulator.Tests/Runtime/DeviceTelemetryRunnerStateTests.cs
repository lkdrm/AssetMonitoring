using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static AssetMonitoring.DeviceSimulator.Tests.Runtime.StateScenarioTestData;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies state scenarios through the production telemetry runner, generator,
/// schedules, and runtimes using fake time and a recording API.
/// </summary>
public sealed class DeviceTelemetryRunnerStateTests
{
    private const SimulatorTelemetryMetric Door = SimulatorTelemetryMetric.DoorState;
    private const SimulatorTelemetryMetric Light = SimulatorTelemetryMetric.LightState;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset InitialUtc = new(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);

    /// <summary>Verifies a full state cycle while all other metrics retain normal behavior.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public async Task RunAsyncAppliesStateScenarioOncePerBatchAndRestoresNormalTelemetry(SimulatorTelemetryMetric metric)
    {
        await using var harness = new RunnerHarness();
        var capability = metric == Door ? SimulatorDeviceCapability.DoorState : SimulatorDeviceCapability.LightState;
        var device = CreateDevice(capabilities: new[]
        {
            SimulatorDeviceCapability.Temperature, capability,
            SimulatorDeviceCapability.Humidity, SimulatorDeviceCapability.DoorState,
            SimulatorDeviceCapability.LightState, capability
        });
        var runtime = CreateRuntime(CreateDefinition(metric), device.Id);
        var observed = new CountingRuntime(runtime);
        var operation = harness.Start(device, CreateSchedule(device.Id, observed), harness.Clock.GetTimestamp());
        var phases = new[] { ScenarioPhase.Pending, ScenarioPhase.Active,
            ScenarioPhase.Active, ScenarioPhase.Completed, ScenarioPhase.Completed };

        for (var cycle = 0; cycle < phases.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal((cycle + 1) * 4, requests.Count);
            var batch = requests.Skip(cycle * 4).Take(4).ToArray();
            AssertBatchMetadata(batch, device.Id, harness.Cancellation.Token,
                InitialUtc + TimeSpan.FromMinutes(cycle));
            var state = GetMeasurement(batch, metric);
            Assert.Null(state.NumericValue);
            Assert.Equal(cycle is 1 or 2, state.StateValue);
            Assert.Equal(phases[cycle], runtime.Phase);
            Assert.Equal(Math.Min(cycle, 3), observed.CallCount);
            if (cycle == 0)
                Assert.Null(CurrentState(runtime));
            else
                Assert.Equal(cycle is 1 or 2, CurrentState(runtime));
            var otherState = GetMeasurement(batch, metric == Door ? Light : Door);
            Assert.False(otherState.StateValue);
            Assert.Null(otherState.NumericValue);
            AssertNormalTemperature(GetMeasurement(batch, SimulatorTelemetryMetric.Temperature));
            AssertNormalHumidity(GetMeasurement(batch, SimulatorTelemetryMetric.Humidity));
            if (cycle < phases.Length - 1)
                harness.Clock.Advance(TelemetryInterval);
        }
        Assert.False(operation.IsCompleted);
        Assert.Equal(20, harness.Client.Requests.Select(request => request.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>Verifies all four scenario types are prepared and applied to the same device.</summary>
    [Fact]
    public async Task RunAsyncWithMixedFactoryScheduleAppliesAllFourMetricsIndependently()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice(capabilities: new[]
        {
            SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.Temperature,
            SimulatorDeviceCapability.LightState, SimulatorDeviceCapability.Humidity
        });
        var target = new ScenarioTargetDefinition(ScenarioTargetMode.All);
        var definitions = new ScenarioDefinition[]
        {
            CreateDefinition(Door, startsAfter: TimeSpan.Zero),
            new HighTemperatureScenarioDefinition("Temperature", TimeSpan.Zero, TimeSpan.FromMinutes(5),
                target, true, TimeSpan.FromMinutes(1), 30, 34, 100, 100),
            CreateDefinition(Light, startsAfter: TimeSpan.Zero),
            new HighHumidityScenarioDefinition("Humidity", TimeSpan.Zero, TimeSpan.FromMinutes(5),
                target, true, TimeSpan.FromMinutes(1), 90, 94, 100, 100)
        };
        var schedule = new DeviceScenarioScheduleFactory().Create(definitions
            .Select(definition => new ResolvedScenario(definition, new[] { device })).ToArray(),
            new MidpointRandom())[device.Id];
        _ = harness.Start(device, schedule, harness.Clock.GetTimestamp());

        await harness.Clock.WaitForDelayAsync();

        var requests = harness.Client.Requests;
        Assert.Equal(4, requests.Count);
        AssertBatchMetadata(requests, device.Id, harness.Cancellation.Token, InitialUtc);
        Assert.True(GetMeasurement(requests, Door).StateValue);
        Assert.True(GetMeasurement(requests, Light).StateValue);
        Assert.Null(GetMeasurement(requests, Door).NumericValue);
        Assert.Null(GetMeasurement(requests, Light).NumericValue);
        Assert.Equal(32d, GetMeasurement(requests, SimulatorTelemetryMetric.Temperature).NumericValue);
        Assert.Equal(92d, GetMeasurement(requests, SimulatorTelemetryMetric.Humidity).NumericValue);
    }

    /// <summary>Verifies retries reuse the same active measurement even after its duration has elapsed.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="recorded">Whether the successful API response reports a newly recorded row.</param>
    [Theory]
    [InlineData(Door, false)]
    [InlineData(Door, true)]
    [InlineData(Light, false)]
    [InlineData(Light, true)]
    public async Task RunAsyncWithRetriesReusesMeasurementWithoutReapplyingStateScenario(
        SimulatorTelemetryMetric metric, bool recorded)
    {
        var attempts = 0;
        await using var harness = new RunnerHarness((_, measurement, _) =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
                throw new HttpRequestException("Transient failure.", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(CreateResponse(measurement, recorded));
        });
        var device = CreateDevice(capabilities: new[] { Capability(metric) });
        var runtime = CreateRuntime(CreateDefinition(metric, startsAfter: TimeSpan.Zero,
            duration: TimeSpan.FromMinutes(1)), device.Id);
        var observed = new CountingRuntime(runtime);
        _ = harness.Start(device, CreateSchedule(device.Id, observed), harness.Clock.GetTimestamp());

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal(attempt + 1, requests.Count);
            Assert.Equal(1, observed.CallCount);
            Assert.Equal(ScenarioPhase.Active, runtime.Phase);
            Assert.Same(observed.LastResult, requests[attempt].Measurement);
            Assert.Same(requests[0].Measurement, requests[attempt].Measurement);
            Assert.True(requests[attempt].Measurement.StateValue);
            Assert.Null(requests[attempt].Measurement.NumericValue);
            Assert.Equal(InitialUtc.UtcDateTime, requests[attempt].Measurement.MeasuredAtUtc);
            Assert.Equal(harness.Cancellation.Token, requests[attempt].CancellationToken);
            if (attempt < 2)
                harness.Clock.Advance(TelemetryInterval);
        }
    }

    /// <summary>Verifies a state schedule is not applied when the device lacks its capability.</summary>
    /// <param name="metric">The state scenario omitted from device capabilities.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public async Task RunAsyncWithoutScenarioCapabilityLeavesRuntimeUnstarted(SimulatorTelemetryMetric metric)
    {
        await using var harness = new RunnerHarness();
        var other = metric == Door ? Light : Door;
        var device = CreateDevice(capabilities: new[] { Capability(other) });
        var runtime = CreateRuntime(CreateDefinition(metric, startsAfter: TimeSpan.Zero), device.Id);
        _ = harness.Start(device, CreateSchedule(device.Id, runtime), harness.Clock.GetTimestamp());

        await harness.Clock.WaitForDelayAsync();

        var request = Assert.Single(harness.Client.Requests);
        Assert.Equal(other, request.Measurement.Metric);
        Assert.False(request.Measurement.StateValue);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(CurrentState(runtime));
    }

    /// <summary>Verifies caller cancellation prevents the first state transformation and request.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public async Task RunAsyncCanceledBeforeStartDoesNotApplyStateScenario(SimulatorTelemetryMetric metric)
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice(capabilities: new[] { Capability(metric) });
        var runtime = CreateRuntime(CreateDefinition(metric, startsAfter: TimeSpan.Zero), device.Id);
        await harness.Cancellation.CancelAsync();
        var operation = harness.Start(device, CreateSchedule(device.Id, runtime), harness.Clock.GetTimestamp());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(CurrentState(runtime));
    }

    /// <summary>Verifies queued state scenarios start on later telemetry cycles with full duration.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public async Task RunAsyncExecutesQueuedStateScenariosAcrossDistinctCycles(SimulatorTelemetryMetric metric)
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice(capabilities: new[] { Capability(metric) });
        var first = CreateRuntime(CreateDefinition(metric, "First", TimeSpan.Zero, TimeSpan.FromMinutes(1)), device.Id);
        var second = CreateRuntime(CreateDefinition(metric, "Second", TimeSpan.Zero, TimeSpan.FromMinutes(2)), device.Id);
        _ = harness.Start(device, CreateSchedule(device.Id, first, second), harness.Clock.GetTimestamp());
        var outputs = new[] { true, false, true, true, false, false };

        for (var cycle = 0; cycle < outputs.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal(cycle + 1, requests.Count);
            Assert.Equal(outputs[cycle], requests[cycle].Measurement.StateValue);
            AssertBatchMetadata(new[] { requests[cycle] }, device.Id, harness.Cancellation.Token,
                InitialUtc + TimeSpan.FromMinutes(cycle));
            if (cycle < 2)
            {
                Assert.Equal(ScenarioPhase.Pending, second.Phase);
                Assert.Null(CurrentState(second));
            }
            else if (cycle < 4)
                Assert.Equal(ScenarioPhase.Active, second.Phase);
            else
                Assert.Equal(ScenarioPhase.Completed, second.Phase);
            if (cycle < outputs.Length - 1)
                harness.Clock.Advance(TelemetryInterval);
        }
    }

    /// <summary>Verifies elapsed time is recalculated after an earlier request in the same batch.</summary>
    /// <param name="metric">The second metric, whose scenario becomes eligible during the first request.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public async Task RunAsyncRecalculatesElapsedBeforeEachStateMeasurement(SimulatorTelemetryMetric metric)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = metric == Door ? Light : Door;
        await using var harness = new RunnerHarness(async (_, measurement, token) =>
        {
            if (measurement.Metric == other)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return CreateResponse(measurement);
        });
        var device = CreateDevice(capabilities: new[] { Capability(other), Capability(metric) });
        var runtime = CreateRuntime(CreateDefinition(metric), device.Id);
        _ = harness.Start(device, CreateSchedule(device.Id, runtime), harness.Clock.GetTimestamp());
        await AwaitAsync(entered.Task);
        Assert.Null(CurrentState(runtime));
        harness.Clock.Advance(TelemetryInterval);
        release.TrySetResult();

        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(2, harness.Client.Requests.Count);
        Assert.False(GetMeasurement(harness.Client.Requests, other).StateValue);
        Assert.True(GetMeasurement(harness.Client.Requests, metric).StateValue);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
        AssertBatchMetadata(harness.Client.Requests, device.Id, harness.Cancellation.Token, InitialUtc);
    }

    /// <summary>Verifies in-flight caller cancellation propagates without retrying the state measurement.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public async Task RunAsyncCanceledDuringStateRequestStopsWithoutRetry(SimulatorTelemetryMetric metric)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (_, measurement, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateResponse(measurement);
        });
        var device = CreateDevice(capabilities: new[] { Capability(metric) });
        var runtime = CreateRuntime(CreateDefinition(metric, startsAfter: TimeSpan.Zero), device.Id);
        var operation = harness.Start(device, CreateSchedule(device.Id, runtime), harness.Clock.GetTimestamp());
        await AwaitAsync(entered.Task);
        await harness.Cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));

        Assert.True(Assert.Single(harness.Client.Requests).Measurement.StateValue);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>Maps a state metric to the corresponding device capability.</summary>
    /// <param name="metric">The door or light metric.</param>
    /// <returns>The required capability.</returns>
    private static SimulatorDeviceCapability Capability(SimulatorTelemetryMetric metric) =>
        metric == Door ? SimulatorDeviceCapability.DoorState : SimulatorDeviceCapability.LightState;

    /// <summary>Observes real runtime applications without replacing its execution behavior.</summary>
    private sealed class CountingRuntime : IMeasurementScenarioRuntime
    {
        private readonly IMeasurementScenarioRuntime _inner;

        /// <summary>Wraps a production runtime.</summary>
        /// <param name="inner">The runtime to observe.</param>
        public CountingRuntime(IMeasurementScenarioRuntime inner) => _inner = inner;

        /// <inheritdoc />
        public Guid DeviceId => _inner.DeviceId;
        /// <inheritdoc />
        public ScenarioDefinition Definition => _inner.Definition;
        /// <inheritdoc />
        public SimulatorTelemetryMetric Metric => _inner.Metric;
        /// <inheritdoc />
        public ScenarioPhase Phase => _inner.Phase;
        /// <summary>Gets the number of applications observed so far.</summary>
        public int CallCount { get; private set; }
        /// <summary>Gets the exact last measurement returned by the real runtime.</summary>
        public SimulatorTelemetryMeasurementRequest? LastResult { get; private set; }

        /// <inheritdoc />
        public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
        {
            CallCount++;
            var result = _inner.Apply(measurement, elapsed);
            LastResult = result;
            return result;
        }
    }

    /// <summary>Finds the single measurement for one metric in a generated batch.</summary>
    /// <param name="requests">The captured batch to inspect.</param>
    /// <param name="metric">The metric expected exactly once in the batch.</param>
    /// <returns>The measurement passed to the API.</returns>
    private static SimulatorTelemetryMeasurementRequest GetMeasurement(
        IReadOnlyList<TelemetryRequest> requests, SimulatorTelemetryMetric metric) =>
        Assert.Single(requests.Where(request => request.Measurement.Metric == metric)).Measurement;

    /// <summary>Checks request identity, cancellation forwarding, and original batch timestamps.</summary>
    /// <param name="requests">The successful batch of outgoing measurements.</param>
    /// <param name="deviceId">The device identifier expected by the API.</param>
    /// <param name="cancellationToken">The caller token expected by the API.</param>
    /// <param name="measuredAt">The UTC time when the batch was generated.</param>
    private static void AssertBatchMetadata(IReadOnlyList<TelemetryRequest> requests,
        Guid deviceId, CancellationToken cancellationToken, DateTimeOffset measuredAt)
    {
        Assert.Equal(requests.Count, requests.Select(request => request.Measurement.MeasurementId).Distinct().Count());
        Assert.All(requests, request =>
        {
            Assert.Equal(deviceId, request.DeviceId);
            Assert.Equal(cancellationToken, request.CancellationToken);
            Assert.NotEqual(Guid.Empty, request.Measurement.MeasurementId);
            Assert.Equal(measuredAt.UtcDateTime, request.Measurement.MeasuredAtUtc);
            Assert.Equal(DateTimeKind.Utc, request.Measurement.MeasuredAtUtc.Kind);
        });
    }

    /// <summary>Checks normal temperature output from the production generator.</summary>
    /// <param name="measurement">The temperature measurement to inspect.</param>
    private static void AssertNormalTemperature(SimulatorTelemetryMeasurementRequest measurement)
    {
        Assert.Equal(SimulatorTelemetryMetric.Temperature, measurement.Metric);
        Assert.InRange(Assert.IsType<double>(measurement.NumericValue), 20.0, 24.0);
        Assert.Null(measurement.StateValue);
    }

    /// <summary>Checks normal humidity output or a captured normal recovery target.</summary>
    /// <param name="measurement">The humidity measurement to inspect.</param>
    private static void AssertNormalHumidity(SimulatorTelemetryMeasurementRequest measurement)
    {
        Assert.Equal(SimulatorTelemetryMetric.Humidity, measurement.Metric);
        Assert.InRange(Assert.IsType<double>(measurement.NumericValue), 78.0, 82.0);
        Assert.Null(measurement.StateValue);
    }

    /// <summary>Creates a successful recording acknowledgment.</summary>
    /// <param name="measurement">The measurement acknowledged by the API.</param>
    /// <param name="recorded">Whether a new row was inserted.</param>
    /// <returns>The successful response for that measurement identifier.</returns>
    private static SimulatorTelemetryRecordingResponse CreateResponse(
        SimulatorTelemetryMeasurementRequest measurement, bool recorded = true) =>
        new(measurement.MeasurementId, recorded);

    /// <summary>Bounds a wait without relying on fake time for the timeout.</summary>
    /// <param name="operation">The operation to observe.</param>
    /// <returns>A task with the same completion or failure as the observed operation.</returns>
    private static Task AwaitAsync(Task operation) =>
        operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);


    /// <summary>Owns runner operations, fake dependencies, and cancellation with bounded cleanup.</summary>
    private sealed class RunnerHarness : IAsyncDisposable
    {
        private readonly List<Task> _operations = new();

        /// <summary>Creates a production runner using a recording API stub and an observed fake clock.</summary>
        /// <param name="send">The optional behavior of telemetry requests.</param>
        public RunnerHarness(Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken,
            Task<SimulatorTelemetryRecordingResponse>>? send = null)
        {
            Client = new StubApiClient(send);
            var options = Options.Create(new DeviceSimulatorOptions
            {
                TelemetryInterval = TelemetryInterval,
                MaxConsecutiveTelemetryFailures = 5,
                WarehouseTimeZoneId = "UTC"
            });
            Runner = new DeviceTelemetryRunner(NullLogger<DeviceTelemetryRunner>.Instance,
                Client, new NormalTelemetryGenerator(Clock), options, Clock);
        }

        /// <summary>Gets the linked caller cancellation source.</summary>
        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Gets the manually advanced clock and delay-registration observer.</summary>
        public ObservedTimeProvider Clock { get; } = new();

        /// <summary>Gets the API request recorder.</summary>
        public StubApiClient Client { get; }

        /// <summary>Gets the production telemetry runner.</summary>
        public DeviceTelemetryRunner Runner { get; }

        /// <summary>Starts a loop using the shared simulation timestamp and tracks it for cleanup.</summary>
        /// <param name="device">The active device whose loop is started.</param>
        /// <param name="schedule">The prepared schedule for this device.</param>
        /// <param name="simulationStartedAt">The timestamp captured from this harness clock.</param>
        /// <returns>The task representing the telemetry loop.</returns>
        public Task Start(SimulatorDeviceResponse device, DeviceScenarioSchedule schedule,
            long simulationStartedAt)
        {
            var operation = Runner.RunAsync(device, schedule, simulationStartedAt, Cancellation.Token);
            _operations.Add(operation);
            return operation;
        }

        /// <summary>Cancels remaining loops and observes completion even when a test assertion fails.</summary>
        /// <returns>An asynchronous operation representing bounded cleanup.</returns>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Cancellation.CancelAsync();

                foreach (var operation in _operations)
                {
                    try
                    {
                        await operation.WaitAsync(TestTimeout, CancellationToken.None);
                    }
                    catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
                    {
                    }
                    catch (Exception) when (operation.IsFaulted)
                    {
                        _ = operation.Exception;
                    }
                }
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }

    /// <summary>Forwards fake-time behavior and records when delays are registered.</summary>
    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(InitialUtc);
        private readonly Channel<TimerRegistration> _registrations = Channel.CreateUnbounded<TimerRegistration>();
        private int _timerCount;

        /// <summary>Gets the total number of timers registered with this provider.</summary>
        public int TimerCount => Volatile.Read(ref _timerCount);

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        /// <inheritdoc />
        public override long GetTimestamp() => _clock.GetTimestamp();

        /// <inheritdoc />
        public override long TimestampFrequency => _clock.TimestampFrequency;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timerCount);
            _registrations.Writer.TryWrite(new TimerRegistration(dueTime, period));
            return timer;
        }

        /// <summary>Advances simulated time and triggers due callbacks.</summary>
        /// <param name="elapsed">The duration by which fake time advances.</param>
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        /// <summary>Waits until the next telemetry interval or retry delay has been registered.</summary>
        /// <returns>A bounded wait for one one-shot delay registration.</returns>
        public async Task WaitForDelayAsync()
        {
            var registration = await _registrations.Reader
                .ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(TelemetryInterval, registration.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, registration.Period);
        }
    }

    /// <summary>Records the timing arguments of one delay.</summary>
    /// <param name="DueTime">The delay before the timer fires.</param>
    /// <param name="Period">The repeat period, expected to be infinite for a task delay.</param>
    private sealed record TimerRegistration(TimeSpan DueTime, TimeSpan Period);

    /// <summary>Captures outgoing API requests before invoking the configured delivery behavior.</summary>
    private sealed class StubApiClient : IDeviceSimulatorApiClient
    {
        private readonly ConcurrentQueue<TelemetryRequest> _requests = new();
        private readonly Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken,
            Task<SimulatorTelemetryRecordingResponse>> _send;

        /// <summary>Creates a request recorder with successful delivery as its default.</summary>
        /// <param name="send">The optional telemetry delivery callback.</param>
        public StubApiClient(Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken,
            Task<SimulatorTelemetryRecordingResponse>>? send = null)
        {
            _send = send ?? ((_, measurement, _) => Task.FromResult(CreateResponse(measurement)));
        }

        /// <summary>Gets a snapshot of recorded request attempts in delivery order.</summary>
        public IReadOnlyList<TelemetryRequest> Requests => _requests.ToArray();

        /// <inheritdoc />
        public Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(Guid deviceId,
            SimulatorTelemetryMeasurementRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(new TelemetryRequest(deviceId, request, cancellationToken));
            return _send(deviceId, request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not synchronize the catalog.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not retrieve devices.");

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(Guid deviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not activate devices.");

        /// <inheritdoc />
        public Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(Guid deviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not send heartbeats.");
    }

    /// <summary>Records the arguments supplied for one telemetry delivery attempt.</summary>
    /// <param name="DeviceId">The device identifier supplied to the API.</param>
    /// <param name="Measurement">The measurement instance sent by the runner.</param>
    /// <param name="CancellationToken">The caller token forwarded by the runner.</param>
    private sealed record TelemetryRequest(Guid DeviceId,
        SimulatorTelemetryMeasurementRequest Measurement, CancellationToken CancellationToken);
}

