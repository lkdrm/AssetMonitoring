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
/// Verifies concurrent heartbeat coordination, failure isolation, cancellation,
/// and completion using the real runner with a fake API and controlled time.
/// </summary>
/// <remarks>
/// Requires xUnit v3 and Microsoft.Extensions.TimeProvider.Testing.
/// Heartbeat intervals are advanced manually; real timeouts only bound
/// asynchronous coordination and cleanup when a regression occurs.
/// </remarks>
public sealed class DeviceHeartbeatCoordinatorTests
{
    /// <summary>Bounds asynchronous test coordination and cleanup.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Defines the delay configured for the real runner.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>Verifies that the coordinator requires a logger.</summary>
    [Fact]
    public void ConstructorWithNullLoggerThrowsArgumentNullException()
    {
        var runner = CreateRunner(new StubApiClient(), new ObservedTimeProvider());

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceHeartbeatCoordinator(null!, runner));

        Assert.Equal("logger", exception.ParamName);
    }

    /// <summary>Verifies that the coordinator requires a heartbeat runner.</summary>
    [Fact]
    public void ConstructorWithNullRunnerThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceHeartbeatCoordinator(new RecordingLogger(), null!));

        Assert.Equal("heartbeatRunner", exception.ParamName);
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
        await using var harness = new CoordinatorHarness(async (id, token) =>
        {
            if (Interlocked.Increment(ref calls) == devices.Length)
            {
                allStarted.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateResponse(id);
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
    /// original exception while another device continues sending heartbeats.
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
        var pendingFailure = new TaskCompletionSource<SimulatorDeviceHeartbeatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new CoordinatorHarness((id, token) =>
            id == failingDevice.Id
                ? pendingFailure.Task.WaitAsync(token)
                : Task.FromResult(CreateResponse(id)), maxFailures: 1);

        var operation = harness.Start(new[] { failingDevice, healthyDevice });
        await harness.Clock.WaitForDelayAsync();
        pendingFailure.SetException(failure);
        var entry = await harness.Logger.WaitForEntryAsync();

        AssertFailureLog(entry, failingDevice, failure);
        Assert.False(harness.Cancellation.IsCancellationRequested);
        Assert.False(operation.IsCompleted);

        harness.Clock.Advance(HeartbeatInterval);
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
        await using var harness = new CoordinatorHarness((id, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromException<SimulatorDeviceHeartbeatResponse>(
                    new HttpRequestException("Temporary network interruption."))
                : Task.FromResult(CreateResponse(id)));
        var device = CreateDevice(1);
        var operation = harness.Start(new[] { device });
        await harness.Clock.WaitForDelayAsync();

        Assert.Single(harness.Client.Requests);
        Assert.Empty(harness.Logger.Entries);
        Assert.False(operation.IsCompleted);

        harness.Clock.Advance(HeartbeatInterval);
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
        await using var harness = new CoordinatorHarness((id, _) =>
            Task.FromException<SimulatorDeviceHeartbeatResponse>(failures[id]), maxFailures: 1);

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
        await using var harness = new CoordinatorHarness(async (_, token) =>
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

    /// <summary>Creates a successful response with a server timestamp in UTC.</summary>
    /// <param name="deviceId">The device whose heartbeat was accepted.</param>
    /// <returns>A valid heartbeat response.</returns>
    private static SimulatorDeviceHeartbeatResponse CreateResponse(Guid deviceId) =>
        new(deviceId, new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc), true);

    /// <summary>Creates an exception for a device failure scenario.</summary>
    /// <param name="kind">The failure to simulate.</param>
    /// <returns>A distinct exception instance.</returns>
    private static Exception CreateFailure(string kind) => kind switch
    {
        "notFound" => new HttpRequestException("Device was not found.", null, HttpStatusCode.NotFound),
        "conflict" => new HttpRequestException("Heartbeat was rejected.", null, HttpStatusCode.Conflict),
        "json" => new JsonException("The API returned an invalid heartbeat response."),
        "independentCancellation" => new OperationCanceledException("Request cancellation without host shutdown."),
        "timeout" => new TaskCanceledException("Heartbeat request timed out.", new TimeoutException()),
        "unexpected" => new NotSupportedException("Unexpected device-specific failure."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported test failure.")
    };

    /// <summary>Constructs the real runner with a fake client and controlled time.</summary>
    /// <param name="client">The fake heartbeat API.</param>
    /// <param name="clock">The manually advanced time provider.</param>
    /// <param name="maxFailures">The configured consecutive failure limit.</param>
    /// <returns>The production runner used by the coordinator.</returns>
    private static DeviceHeartbeatRunner CreateRunner(
        StubApiClient client, ObservedTimeProvider clock, int maxFailures = 5) =>
        new(
            NullLogger<DeviceHeartbeatRunner>.Instance,
            client,
            Options.Create(new DeviceSimulatorOptions
            {
                HeartbeatInterval = HeartbeatInterval,
                MaxConsecutiveHeartbeatFailures = maxFailures
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
            "Heartbeat loop stopped for device {DeviceCode} ({DeviceId}) because cancellation was requested.",
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
            "Heartbeat loop failed for device {DeviceCode} ({DeviceId}). This device will no longer send heartbeats.",
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
            Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>>? send = null,
            int maxFailures = 5)
        {
            Client = new StubApiClient(send);
            Coordinator = new DeviceHeartbeatCoordinator(Logger, CreateRunner(Client, Clock, maxFailures));
        }

        /// <summary>Gets the caller cancellation source linked to the current test.</summary>
        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Gets the fake heartbeat API.</summary>
        public StubApiClient Client { get; }

        /// <summary>Gets the time provider used for runner delays.</summary>
        public ObservedTimeProvider Clock { get; } = new();

        /// <summary>Gets the captured coordinator logs.</summary>
        public RecordingLogger Logger { get; } = new();

        /// <summary>Gets the production coordinator under test.</summary>
        public DeviceHeartbeatCoordinator Coordinator { get; }

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

        /// <summary>Waits for the next heartbeat or retry delay to be scheduled.</summary>
        /// <returns>A task representing observation of one configured delay.</returns>
        public async Task WaitForDelayAsync()
        {
            var delay = await _delays.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(HeartbeatInterval, delay);
        }
    }

    /// <summary>Records heartbeat requests and provides configurable asynchronous responses.</summary>
    private sealed class StubApiClient : IDeviceSimulatorApiClient
    {
        private readonly Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>> _send;
        private readonly ConcurrentQueue<HeartbeatRequest> _requests = new();

        /// <summary>Creates the API stub, defaulting to successful heartbeats.</summary>
        /// <param name="send">The heartbeat callback, or null for success.</param>
        public StubApiClient(
            Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>>? send = null)
        {
            _send = send ?? ((id, _) => Task.FromResult(CreateResponse(id)));
        }

        /// <summary>Gets a snapshot of all heartbeat requests.</summary>
        public IReadOnlyList<HeartbeatRequest> Requests => _requests.ToArray();

        /// <inheritdoc />
        public Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(
            Guid deviceId,
            SimulatorTelemetryMeasurementRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Heartbeat coordination must not send telemetry.");

        /// <inheritdoc />
        public Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(
            Guid deviceId, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(new HeartbeatRequest(deviceId, cancellationToken));
            return _send(deviceId, cancellationToken);
        }

        /// <inheritdoc />
        public Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Heartbeat coordination must not synchronize the catalog.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Heartbeat coordination must use the supplied devices.");

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(
            Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Heartbeat coordination must not activate devices.");
    }

    /// <summary>Records the arguments forwarded to one heartbeat request.</summary>
    /// <param name="DeviceId">The requested device identifier.</param>
    /// <param name="CancellationToken">The token forwarded to the API.</param>
    private sealed record HeartbeatRequest(Guid DeviceId, CancellationToken CancellationToken);

    /// <summary>Captures structured logs and supports deterministic log observation.</summary>
    private sealed class RecordingLogger : ILogger<DeviceHeartbeatCoordinator>
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
