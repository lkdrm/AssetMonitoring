using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies concurrent telemetry coordination, failure isolation, cancellation,
/// and completion using the real runner and generator with a fake API and controlled time.
/// </summary>
/// <remarks>
/// Requires xUnit v3 and Microsoft.Extensions.TimeProvider.Testing.
/// Telemetry intervals are advanced manually; real timeouts only bound
/// asynchronous coordination and cleanup when a regression occurs.
/// </remarks>
public sealed class DeviceTelemetryCoordinatorTests
{
    /// <summary>Bounds asynchronous test coordination and cleanup.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Defines the delay configured for the real runner.</summary>
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Verifies that the coordinator requires a logger.</summary>
    [Fact]
    public void ConstructorWithNullLoggerThrowsArgumentNullException()
    {
        var runner = CreateRunner(new StubApiClient(), new ObservedTimeProvider());

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceTelemetryCoordinator(null!, runner));

        Assert.Equal("logger", exception.ParamName);
    }

    /// <summary>Verifies that the coordinator requires a telemetry runner.</summary>
    [Fact]
    public void ConstructorWithNullRunnerThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceTelemetryCoordinator(new RecordingLogger(), null!));

        Assert.Equal("telemetryRunner", exception.ParamName);
    }

    /// <summary>Verifies that a missing collection is rejected before any requests.</summary>
    [Fact]
    public async Task RunAsyncWithNullDevicesThrowsArgumentNullException()
    {
        await using var harness = new CoordinatorHarness();
        var operation = harness.Start(null!);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => AwaitAsync(operation));

        Assert.Equal("devices", exception.ParamName);
        Assert.Empty(harness.Client.Requests);
        Assert.Empty(harness.Logger.Entries);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>
    /// Verifies that an empty collection completes immediately, including
    /// when there are no device loops to cancel for an already canceled token.
    /// </summary>
    /// <param name="cancelFirst">Whether the caller token is canceled before starting.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWithEmptyDevicesCompletesImmediately(bool cancelFirst)
    {
        await using var harness = new CoordinatorHarness();
        if (cancelFirst)
        {
            await harness.Cancellation.CancelAsync();
        }

        var operation = harness.Start(Array.Empty<SimulatorDeviceResponse>());

        Assert.True(operation.IsCompletedSuccessfully);
        await AwaitAsync(operation);
        Assert.Empty(harness.Client.Requests);
        Assert.Empty(harness.Logger.Entries);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>
    /// Verifies that all device requests start before any request completes,
    /// receive the caller token, and stop normally when it is canceled.
    /// </summary>
    [Fact]
    public async Task RunAsyncStartsAllDeviceLoopsConcurrentlyAndForwardsCancellation()
    {
        var devices = new[] { CreateDevice(1), CreateDevice(2), CreateDevice(3) };
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var harness = new CoordinatorHarness(async (_, measurement, token) =>
        {
            if (Interlocked.Increment(ref calls) == devices.Length)
            {
                allStarted.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateResponse(measurement);
        });

        var operation = harness.Start(devices);
        await AwaitAsync(allStarted.Task);

        Assert.False(operation.IsCompleted);
        Assert.Equal(devices.Select(device => device.Id).OrderBy(id => id).ToArray(),
            harness.Client.Requests.Select(request => request.DeviceId).OrderBy(id => id).ToArray());
        Assert.All(harness.Client.Requests, request =>
            Assert.Equal(harness.Cancellation.Token, request.CancellationToken));
        Assert.Empty(harness.Logger.Entries);

        await harness.Cancellation.CancelAsync();
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.Equal(devices.Length, harness.Client.Requests.Count);
        Assert.Equal(devices.Length, harness.Logger.Entries.Count);
        foreach (var device in devices)
        {
            AssertCancellationLog(harness.Logger, device);
        }
    }

    /// <summary>
    /// Verifies that an already canceled caller token prevents API requests
    /// and each device loop records normal cancellation without faulting the coordinator.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithCanceledTokenStopsEveryLoopWithoutSending()
    {
        await using var harness = new CoordinatorHarness();
        var devices = new[] { CreateDevice(1), CreateDevice(2) };
        await harness.Cancellation.CancelAsync();

        var operation = harness.Start(devices);
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Equal(devices.Length, harness.Logger.Entries.Count);
        foreach (var device in devices)
        {
            AssertCancellationLog(harness.Logger, device);
        }
    }

    /// <summary>
    /// Verifies that a terminal failure in one device is logged with its
    /// original exception while another device continues sending telemetry.
    /// </summary>
    /// <param name="failureKind">The terminal failure produced by the first device.</param>
    [Theory]
    [InlineData("notFound")]
    [InlineData("conflict")]
    [InlineData("json")]
    [InlineData("independentCancellation")]
    [InlineData("timeout")]
    [InlineData("unexpected")]
    public async Task RunAsyncWithOneFailedDeviceKeepsOtherDeviceRunning(string failureKind)
    {
        var failingDevice = CreateDevice(1);
        var healthyDevice = CreateDevice(2);
        var failure = CreateFailure(failureKind);
        var pendingFailure = new TaskCompletionSource<SimulatorTelemetryRecordingResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new CoordinatorHarness((id, measurement, token) =>
            id == failingDevice.Id
                ? pendingFailure.Task.WaitAsync(token)
                : Task.FromResult(CreateResponse(measurement)), maxFailures: 1);

        var operation = harness.Start(new[] { failingDevice, healthyDevice });
        await harness.Clock.WaitForDelayAsync();
        pendingFailure.SetException(failure);
        var entry = await harness.Logger.WaitForEntryAsync();

        AssertFailureLog(entry, failingDevice, failure);
        Assert.False(harness.Cancellation.IsCancellationRequested);
        Assert.False(operation.IsCompleted);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(1, harness.Client.Requests.Count(request => request.DeviceId == failingDevice.Id));
        Assert.Equal(2, harness.Client.Requests.Count(request => request.DeviceId == healthyDevice.Id));
        Assert.Single(harness.Logger.Entries);
        Assert.False(operation.IsCompleted);

        await harness.Cancellation.CancelAsync();
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.Equal(2, harness.Logger.Entries.Count);
        AssertCancellationLog(harness.Logger, healthyDevice);
        Assert.Single(harness.Logger.Entries.Where(log => log.Level == LogLevel.Error));
    }

    /// <summary>
    /// Verifies that the coordinator allows the runner to retry and recover
    /// without reporting a terminal device failure for a transient interruption.
    /// </summary>
    [Fact]
    public async Task RunAsyncAllowsRunnerToRecoverWithoutLoggingTerminalFailure()
    {
        var calls = 0;
        await using var harness = new CoordinatorHarness((id, measurement, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(
                    new HttpRequestException("Temporary network interruption."))
                : Task.FromResult(CreateResponse(measurement)));
        var device = CreateDevice(1);
        var operation = harness.Start(new[] { device });
        await harness.Clock.WaitForDelayAsync();

        Assert.Single(harness.Client.Requests);
        Assert.Empty(harness.Logger.Entries);
        Assert.False(operation.IsCompleted);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(2, harness.Client.Requests.Count);
        Assert.Empty(harness.Logger.Entries);
        Assert.False(operation.IsCompleted);

        await harness.Cancellation.CancelAsync();
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.Single(harness.Logger.Entries);
        AssertCancellationLog(harness.Logger, device);
    }

    /// <summary>
    /// Verifies that all terminal failures are observed and logged, and the
    /// coordinator completes when no device loop remains without canceling the caller.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithAllDevicesFailingCompletesAfterLoggingEveryFailure()
    {
        var devices = new[] { CreateDevice(1), CreateDevice(2), CreateDevice(3) };
        var failures = new Dictionary<Guid, Exception>
        {
            [devices[0].Id] = CreateFailure("conflict"),
            [devices[1].Id] = CreateFailure("json"),
            [devices[2].Id] = CreateFailure("timeout")
        };
        await using var harness = new CoordinatorHarness((id, measurement, _) =>
            Task.FromException<SimulatorTelemetryRecordingResponse>(failures[id]), maxFailures: 1);

        var operation = harness.Start(devices);
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.False(harness.Cancellation.IsCancellationRequested);
        Assert.Equal(devices.Length, harness.Client.Requests.Count);
        Assert.Equal(devices.Length, harness.Logger.Entries.Count);
        Assert.Equal(0, harness.Clock.TimerCount);
        foreach (var device in devices)
        {
            var entry = Assert.Single(harness.Logger.Entries.Where(log =>
                Assert.IsType<Guid>(log.Properties["DeviceId"]) == device.Id));
            AssertFailureLog(entry, device, failures[device.Id]);
        }
    }

    /// <summary>
    /// Verifies that cancellation does not complete the coordinator until
    /// every in-flight device request has actually acknowledged cancellation.
    /// </summary>
    [Fact]
    public async Task RunAsyncWaitsForEveryDeviceLoopToFinishAfterCancellation()
    {
        var firstDevice = CreateDevice(1);
        var secondDevice = CreateDevice(2);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var harness = new CoordinatorHarness(async (_, _, token) =>
        {
            var requestNumber = Interlocked.Increment(ref calls);
            if (requestNumber == 2)
            {
                allStarted.TrySetResult();
            }

            if (requestNumber == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                // Deliberately delay acknowledgement of the caller's cancellation.
                await AwaitAsync(releaseSecond.Task);
            }

            throw new OperationCanceledException(token);
        });
        var operation = harness.Start(new[] { firstDevice, secondDevice });

        try
        {
            await AwaitAsync(allStarted.Task);
            await harness.Cancellation.CancelAsync();
            var firstEntry = await harness.Logger.WaitForEntryAsync();

            Assert.Equal(LogLevel.Information, firstEntry.Level);
            Assert.Equal(firstDevice.Id, Assert.IsType<Guid>(firstEntry.Properties["DeviceId"]));
            Assert.False(operation.IsCompleted);

            releaseSecond.SetResult();
            await AwaitAsync(operation);

            Assert.True(operation.IsCompletedSuccessfully);
            Assert.Equal(2, harness.Client.Requests.Count);
            Assert.Equal(2, harness.Logger.Entries.Count);
            AssertCancellationLog(harness.Logger, firstDevice);
            AssertCancellationLog(harness.Logger, secondDevice);
        }
        finally
        {
            // Release the callback even if an assertion fails before normal cleanup.
            releaseSecond.TrySetResult();
        }
    }

    /// <summary>
    /// Verifies that devices without capabilities finish normally and the
    /// coordinator completes when every supplied loop has returned.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithAllDevicesWithoutCapabilitiesCompletesNormally()
    {
        await using var harness = new CoordinatorHarness();
        var devices = new[]
        {
            CreateDevice(1) with { Capabilities = Array.Empty<SimulatorDeviceCapability>() },
            CreateDevice(2) with { Capabilities = Array.Empty<SimulatorDeviceCapability>() }
        };

        var operation = harness.Start(devices);
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.False(harness.Cancellation.IsCancellationRequested);
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        // The runner owns the no-capabilities warning; the coordinator has no failure to report.
        Assert.Empty(harness.Logger.Entries);
    }

    /// <summary>
    /// Verifies that normal completion of one device neither stops the other
    /// device nor completes the coordinator while telemetry is still running.
    /// </summary>
    /// <param name="finishedDeviceFirst">Whether the device that returns normally is listed first.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWithOneCompletedDeviceKeepsOtherDeviceRunning(bool finishedDeviceFirst)
    {
        await using var harness = new CoordinatorHarness();
        var finishedDevice = CreateDevice(1) with
        {
            Capabilities = Array.Empty<SimulatorDeviceCapability>()
        };
        var runningDevice = CreateDevice(2);
        var devices = finishedDeviceFirst
            ? new[] { finishedDevice, runningDevice }
            : new[] { runningDevice, finishedDevice };
        var operation = harness.Start(devices);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(runningDevice.Id, Assert.Single(harness.Client.Requests).DeviceId);
        Assert.Empty(harness.Logger.Entries);
        Assert.False(operation.IsCompleted);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(2, harness.Client.Requests.Count);
        Assert.All(harness.Client.Requests, request => Assert.Equal(runningDevice.Id, request.DeviceId));
        Assert.False(operation.IsCompleted);
        Assert.False(harness.Cancellation.IsCancellationRequested);

        await harness.Cancellation.CancelAsync();
        await AwaitAsync(operation);

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.Single(harness.Logger.Entries);
        AssertCancellationLog(harness.Logger, runningDevice);
    }

    /// <summary>Creates an active device with deterministic identity.</summary>
    /// <param name="number">The suffix distinguishing the device.</param>
    /// <returns>An active temperature sensor.</returns>
    private static SimulatorDeviceResponse CreateDevice(int number) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
            $"WH-{number:D3}",
            $"Warehouse Sensor {number}",
            new[] { SimulatorDeviceCapability.Temperature },
            SimulatorDeviceLifecycle.Active);

    /// <summary>Creates a successful acknowledgment of an existing measurement.</summary>
    /// <param name="measurement">The measurement accepted by the API.</param>
    /// <returns>A valid telemetry recording response.</returns>
    private static SimulatorTelemetryRecordingResponse CreateResponse(
        SimulatorTelemetryMeasurementRequest measurement) =>
        new(measurement.MeasurementId, true);

    /// <summary>Creates an exception for a device failure scenario.</summary>
    /// <param name="kind">The failure to simulate.</param>
    /// <returns>A distinct exception instance.</returns>
    private static Exception CreateFailure(string kind) => kind switch
    {
        "notFound" => new HttpRequestException("Device was not found.", null, HttpStatusCode.NotFound),
        "conflict" => new HttpRequestException("Telemetry was rejected.", null, HttpStatusCode.Conflict),
        "json" => new JsonException("The API returned an invalid telemetry response."),
        "independentCancellation" => new OperationCanceledException("Request cancellation without host shutdown."),
        "timeout" => new TaskCanceledException("Telemetry request timed out.", new TimeoutException()),
        "unexpected" => new NotSupportedException("Unexpected device-specific failure."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported test failure.")
    };

    /// <summary>Constructs the real runner with a fake client and controlled time.</summary>
    /// <param name="client">The fake telemetry API.</param>
    /// <param name="clock">The manually advanced time provider.</param>
    /// <param name="maxFailures">The configured consecutive failure limit.</param>
    /// <returns>The production runner used by the coordinator.</returns>
    private static DeviceTelemetryRunner CreateRunner(
        StubApiClient client, ObservedTimeProvider clock, int maxFailures = 5) =>
        new(
            NullLogger<DeviceTelemetryRunner>.Instance,
            client,
            new NormalTelemetryGenerator(clock),
            Options.Create(new DeviceSimulatorOptions
            {
                TelemetryInterval = TelemetryInterval,
                MaxConsecutiveTelemetryFailures = maxFailures,
                WarehouseTimeZoneId = "UTC"
            }),
            clock);

    /// <summary>Bounds a task wait using the current test cancellation token.</summary>
    /// <param name="operation">The operation whose completion is required.</param>
    /// <returns>A task representing the bounded wait.</returns>
    private static Task AwaitAsync(Task operation) =>
        operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

    /// <summary>Checks the single cancellation log for a particular device.</summary>
    /// <param name="logger">The captured coordinator logs.</param>
    /// <param name="device">The canceled device.</param>
    private static void AssertCancellationLog(RecordingLogger logger, SimulatorDeviceResponse device)
    {
        var entry = Assert.Single(logger.Entries.Where(log =>
            Assert.IsType<Guid>(log.Properties["DeviceId"]) == device.Id));

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(device.Code, Assert.IsType<string>(entry.Properties["DeviceCode"]));
        Assert.Equal(
            "Telemetry loop stopped for device {DeviceCode} ({DeviceId}) because cancellation was requested.",
            Assert.IsType<string>(entry.Properties["{OriginalFormat}"]));
    }

    /// <summary>Checks that a terminal failure retains the device and original exception.</summary>
    /// <param name="entry">The captured failure log.</param>
    /// <param name="device">The failed device.</param>
    /// <param name="exception">The original failure instance.</param>
    private static void AssertFailureLog(
        LogEntry entry, SimulatorDeviceResponse device, Exception exception)
    {
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(device.Id, Assert.IsType<Guid>(entry.Properties["DeviceId"]));
        Assert.Equal(device.Code, Assert.IsType<string>(entry.Properties["DeviceCode"]));
        Assert.Equal(
            "Telemetry loop failed for device {DeviceCode} ({DeviceId}). This device will no longer send telemetry.",
            Assert.IsType<string>(entry.Properties["{OriginalFormat}"]));
    }

    /// <summary>Owns coordinator execution and cleans up remaining device loops.</summary>
    private sealed class CoordinatorHarness : IAsyncDisposable
    {
        private readonly List<Task> _operations = new();

        /// <summary>Creates the coordinator, real runner, and fake dependencies.</summary>
        /// <param name="send">The API behavior, or null for success.</param>
        /// <param name="maxFailures">The runner's consecutive failure limit.</param>
        public CoordinatorHarness(
            Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken, Task<SimulatorTelemetryRecordingResponse>>? send = null,
            int maxFailures = 5)
        {
            Client = new StubApiClient(send);
            Coordinator = new DeviceTelemetryCoordinator(Logger, CreateRunner(Client, Clock, maxFailures));
        }

        /// <summary>Gets the caller cancellation source linked to the current test.</summary>
        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Gets the fake telemetry API.</summary>
        public StubApiClient Client { get; }

        /// <summary>Gets the time provider used for runner delays.</summary>
        public ObservedTimeProvider Clock { get; } = new();

        /// <summary>Gets the captured coordinator logs.</summary>
        public RecordingLogger Logger { get; } = new();

        /// <summary>Gets the production coordinator under test.</summary>
        public DeviceTelemetryCoordinator Coordinator { get; }

        /// <summary>Starts coordination and tracks its task for cleanup.</summary>
        /// <param name="devices">The supplied device collection.</param>
        /// <returns>The task representing all device loops.</returns>
        public Task Start(IReadOnlyList<SimulatorDeviceResponse> devices)
        {
            var operation = Coordinator.RunAsync(devices, Cancellation.Token);
            _operations.Add(operation);
            return operation;
        }

        /// <summary>
        /// Cancels unfinished loops and observes their tasks. Assertions in each
        /// test check expected outcomes; cleanup also observes already faulted
        /// tasks without replacing the original assertion failure.
        /// </summary>
        /// <returns>A task representing bounded cleanup.</returns>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Cancellation.CancelAsync();
                foreach (var operation in _operations)
                {
                    try
                    {
                        // Cleanup must still work after cancellation of the test token.
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

    /// <summary>
    /// Delegates to FakeTimeProvider and exposes timer registration so tests
    /// can advance time after the runner has actually scheduled a delay.
    /// </summary>
    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(
            new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero));
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        private int _timerCount;

        /// <summary>Gets the total number of registered timers.</summary>
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
        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timerCount);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }

        /// <summary>Advances simulated time and triggers due callbacks.</summary>
        /// <param name="elapsed">The simulated elapsed duration.</param>
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        /// <summary>Waits for the next telemetry or retry delay to be scheduled.</summary>
        /// <returns>A task representing observation of one configured delay.</returns>
        public async Task WaitForDelayAsync()
        {
            var delay = await _delays.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(TelemetryInterval, delay);
        }
    }

    /// <summary>Records telemetry requests and provides configurable asynchronous responses.</summary>
    private sealed class StubApiClient : IDeviceSimulatorApiClient
    {
        private readonly Func<Guid, SimulatorTelemetryMeasurementRequest,
            CancellationToken, Task<SimulatorTelemetryRecordingResponse>> _send;
        private readonly ConcurrentQueue<TelemetryRequest> _requests = new();

        /// <summary>Creates the API stub, defaulting to successful telemetry responses.</summary>
        /// <param name="send">The telemetry callback, or null for success.</param>
        public StubApiClient(
            Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken,
                Task<SimulatorTelemetryRecordingResponse>>? send = null)
        {
            _send = send ?? ((_, measurement, _) => Task.FromResult(CreateResponse(measurement)));
        }

        /// <summary>Gets a snapshot of all telemetry requests.</summary>
        public IReadOnlyList<TelemetryRequest> Requests => _requests.ToArray();

        /// <inheritdoc />
        public Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(
            Guid deviceId,
            SimulatorTelemetryMeasurementRequest request,
            CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(new TelemetryRequest(deviceId, request, cancellationToken));
            return _send(deviceId, request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(
            Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Telemetry coordination must not send heartbeats.");

        /// <inheritdoc />
        public Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Telemetry coordination must not synchronize the catalog.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Telemetry coordination must use the supplied devices.");

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(
            Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Telemetry coordination must not activate devices.");
    }

    /// <summary>Records the arguments forwarded to one telemetry request.</summary>
    /// <param name="DeviceId">The requested device identifier.</param>
    /// <param name="Measurement">The measurement passed to the API.</param>
    /// <param name="CancellationToken">The token forwarded to the API.</param>
    private sealed record TelemetryRequest(
        Guid DeviceId, SimulatorTelemetryMeasurementRequest Measurement,
        CancellationToken CancellationToken);

    /// <summary>Captures structured logs and supports deterministic log observation.</summary>
    private sealed class RecordingLogger : ILogger<DeviceTelemetryCoordinator>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly Channel<LogEntry> _notifications = Channel.CreateUnbounded<LogEntry>();

        /// <summary>Gets a snapshot of all captured coordinator logs.</summary>
        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            var entry = new LogEntry(logLevel, exception, properties);
            _entries.Enqueue(entry);
            _notifications.Writer.TryWrite(entry);
        }

        /// <summary>Waits for the next coordinator log without polling.</summary>
        /// <returns>The next captured log entry.</returns>
        public async Task<LogEntry> WaitForEntryAsync() =>
            await _notifications.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>Represents a captured coordinator log entry.</summary>
    /// <param name="Level">The log severity.</param>
    /// <param name="Exception">The original logged exception.</param>
    /// <param name="Properties">The structured log values.</param>
    private sealed record LogEntry(
        LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> Properties);
}
