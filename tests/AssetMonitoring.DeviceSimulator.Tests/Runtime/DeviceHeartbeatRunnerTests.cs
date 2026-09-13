using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies heartbeat scheduling, retry limits, cancellation, logging,
/// and independent device execution without HTTP calls or real interval waits.
/// </summary>
/// <remarks>
/// Requires Microsoft.Extensions.TimeProvider.Testing in the test project.
/// Each test waits for timer registration before advancing simulated time.
/// Real timeouts only bound asynchronous coordination when a regression occurs.
/// </remarks>
public sealed class DeviceHeartbeatRunnerTests
{
    /// <summary>Bounds asynchronous coordination and cleanup.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Defines the configured delay used by the test fixtures.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>Defines a deterministic server timestamp for successful responses.</summary>
    private static readonly DateTime ServerHeartbeatUtc =
        new DateTime(2026, 9, 10, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);

    /// <summary>Verifies that every constructor dependency is required.</summary>
    /// <param name="dependency">The constructor dependency to omit.</param>
    [Theory]
    [InlineData("logger")]
    [InlineData("apiClient")]
    [InlineData("options")]
    [InlineData("timeProvider")]
    public void ConstructorWithNullDependencyThrowsArgumentNullException(string dependency)
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceHeartbeatRunner(
                dependency == "logger" ? null! : new RecordingLogger(),
                dependency == "apiClient" ? null! : new StubApiClient(),
                dependency == "options" ? null! : CreateOptions(),
                dependency == "timeProvider" ? null! : new ObservedTimeProvider()));

        Assert.Equal(dependency, exception.ParamName);
    }

    /// <summary>Verifies that a missing device is rejected before sending.</summary>
    [Fact]
    public async Task RunAsyncWithNullDeviceThrowsArgumentNullException()
    {
        await using var harness = new RunnerHarness();
        var operation = harness.Start(null!);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => AwaitAsync(operation));

        Assert.Equal("device", exception.ParamName);
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>Verifies that an empty identifier is rejected before sending.</summary>
    [Fact]
    public async Task RunAsyncWithEmptyDeviceIdThrowsArgumentException()
    {
        await using var harness = new RunnerHarness();
        var operation = harness.Start(CreateDevice() with { Id = Guid.Empty });

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => AwaitAsync(operation));

        Assert.Equal("device", exception.ParamName);
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>Verifies that only an active device can enter the loop.</summary>
    /// <param name="lifecycle">The lifecycle that must be rejected.</param>
    [Theory]
    [InlineData(SimulatorDeviceLifecycle.Registered)]
    [InlineData(SimulatorDeviceLifecycle.Retired)]
    [InlineData((SimulatorDeviceLifecycle)999)]
    public async Task RunAsyncWithInactiveDeviceThrowsInvalidOperationException(
        SimulatorDeviceLifecycle lifecycle)
    {
        await using var harness = new RunnerHarness();
        var operation = harness.Start(CreateDevice() with { Lifecycle = lifecycle });

        await Assert.ThrowsAsync<InvalidOperationException>(() => AwaitAsync(operation));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>
    /// Verifies the immediate request, configured delay, token forwarding,
    /// and structured logging of the timestamp supplied by the API.
    /// </summary>
    /// <param name="changed">Whether the API updated the stored heartbeat.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncSendsImmediatelyAndLogsServerResponse(bool changed)
    {
        await using var harness = new RunnerHarness((id, _) =>
            Task.FromResult(CreateResponse(id, changed)));
        var device = CreateDevice();
        var operation = harness.Start(device);

        await harness.Clock.WaitForDelayAsync();

        var request = Assert.Single(harness.Client.Requests);
        Assert.Equal(device.Id, request.DeviceId);
        Assert.Equal(harness.Cancellation.Token, request.CancellationToken);
        Assert.False(operation.IsCompleted);

        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(device.Code, Assert.IsType<string>(entry.Properties["DeviceCode"]));
        Assert.Equal(device.Id, Assert.IsType<Guid>(entry.Properties["DeviceId"]));
        Assert.Equal(ServerHeartbeatUtc,
            Assert.IsType<DateTime>(entry.Properties["LastHeartbeatAtUtc"]));
        Assert.Equal(changed, Assert.IsType<bool>(entry.Properties["Changed"]));

        harness.Clock.Advance(HeartbeatInterval - TimeSpan.FromMilliseconds(1));
        Assert.Single(harness.Client.Requests);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await harness.Clock.WaitForDelayAsync();
        Assert.Equal(2, harness.Client.Requests.Count);
    }

    /// <summary>
    /// Verifies that a pending request cannot overlap with another request,
    /// and that a complete interval starts after success or a retryable failure.
    /// </summary>
    /// <param name="requestFails">Whether the pending request eventually fails.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWaitsForRequestCompletionBeforeStartingInterval(bool requestFails)
    {
        var pending = new TaskCompletionSource<SimulatorDeviceHeartbeatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var harness = new RunnerHarness((id, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                requestStarted.TrySetResult();
                return pending.Task.WaitAsync(token);
            }

            return Task.FromResult(CreateResponse(id));
        });
        var device = CreateDevice();
        _ = harness.Start(device);
        await AwaitAsync(requestStarted.Task);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Single(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);

        if (requestFails)
        {
            pending.SetException(new HttpRequestException("Connection was interrupted."));
        }
        else
        {
            pending.SetResult(CreateResponse(device.Id));
        }

        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(HeartbeatInterval - TimeSpan.FromMilliseconds(1));
        Assert.Single(harness.Client.Requests);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await harness.Clock.WaitForDelayAsync();
        Assert.Equal(2, harness.Client.Requests.Count);
        Assert.Equal(requestFails ? 1 : 0,
            harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    /// <summary>
    /// Verifies four delayed retries followed by termination on the fifth
    /// consecutive transient failure, preserving the final exception.
    /// </summary>
    /// <param name="failureKind">The transient failure to simulate.</param>
    [Theory]
    [InlineData("network")]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("599")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    public async Task RunAsyncStopsOnFifthConsecutiveTransientFailure(string failureKind)
    {
        var failures = Enumerable.Range(1, 5)
            .Select(number => CreateFailure(failureKind, number)).ToArray();
        var calls = 0;
        await using var harness = new RunnerHarness((_, _) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return Task.FromException<SimulatorDeviceHeartbeatResponse>(
                failures[Math.Min(index, failures.Length - 1)]);
        });
        var device = CreateDevice();
        var operation = harness.Start(device);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            await harness.Clock.WaitForDelayAsync();
            Assert.Equal(attempt, harness.Client.Requests.Count);
            Assert.False(operation.IsCompleted);
            harness.Clock.Advance(HeartbeatInterval);
        }

        var exception = await Record.ExceptionAsync(() => AwaitAsync(operation));

        Assert.Same(failures[4], exception);
        Assert.Equal(5, harness.Client.Requests.Count);
        Assert.Equal(4, harness.Clock.TimerCount);
        Assert.False(harness.Cancellation.IsCancellationRequested);

        var warnings = harness.Logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(4, warnings.Length);

        for (var index = 0; index < warnings.Length; index++)
        {
            Assert.Same(failures[index], warnings[index].Exception);
            Assert.Equal(index + 1,
                Assert.IsType<int>(warnings[index].Properties["ConsecutiveFailures"]));
            Assert.Equal(5, Assert.IsType<int>(warnings[index].Properties["MaxFailures"]));
            Assert.Equal(HeartbeatInterval,
                Assert.IsType<TimeSpan>(warnings[index].Properties["RetryDelay"]));
            Assert.Equal(device.Id,
                Assert.IsType<Guid>(warnings[index].Properties["DeviceId"]));
        }

        var terminal = Assert.Single(harness.Logger.Entries
            .Where(entry => entry.Level == LogLevel.Error));
        Assert.Same(failures[4], terminal.Exception);
        Assert.Equal(device.Id, Assert.IsType<Guid>(terminal.Properties["DeviceId"]));
        Assert.Equal(device.Code, Assert.IsType<string>(terminal.Properties["DeviceCode"]));
        Assert.Equal(5, Assert.IsType<int>(terminal.Properties["ConsecutiveFailures"]));
        Assert.Equal(5, Assert.IsType<int>(terminal.Properties["MaxFailures"]));
        Assert.DoesNotContain(harness.Logger.Entries,
            entry => entry.Level == LogLevel.Information);

        harness.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(5, harness.Client.Requests.Count);
    }

    /// <summary>
    /// Verifies that any successful response resets the failure count,
    /// including a response that reports no timestamp change.
    /// </summary>
    /// <param name="changed">Whether the successful heartbeat changed the timestamp.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncResetsConsecutiveFailuresAfterSuccess(bool changed)
    {
        var failure = new HttpRequestException("Temporary network failure.");
        var calls = 0;
        await using var harness = new RunnerHarness((id, _) =>
            Interlocked.Increment(ref calls) == 5
                ? Task.FromResult(CreateResponse(id, changed))
                : Task.FromException<SimulatorDeviceHeartbeatResponse>(failure));
        var operation = harness.Start(CreateDevice());

        // Four failures, one success, then four more failures all allow a delay.
        for (var attempt = 1; attempt <= 9; attempt++)
        {
            await harness.Clock.WaitForDelayAsync();
            Assert.Equal(attempt, harness.Client.Requests.Count);
            Assert.False(operation.IsCompleted);
            harness.Clock.Advance(HeartbeatInterval);
        }

        var exception = await Record.ExceptionAsync(() => AwaitAsync(operation));

        Assert.Same(failure, exception);
        Assert.Equal(10, harness.Client.Requests.Count);
        Assert.Equal(9, harness.Clock.TimerCount);
        var warningCounts = harness.Logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => Assert.IsType<int>(entry.Properties["ConsecutiveFailures"]))
            .ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 1, 2, 3, 4 }, warningCounts);
        Assert.Single(harness.Logger.Entries.Where(entry => entry.Level == LogLevel.Information));
        Assert.Single(harness.Logger.Entries.Where(entry => entry.Level == LogLevel.Error));
    }

    /// <summary>
    /// Verifies immediate propagation of errors outside the retry policy,
    /// without scheduling another request or reporting a retry.
    /// </summary>
    /// <param name="failureKind">The nonretryable failure to simulate.</param>
    [Theory]
    [InlineData("400")]
    [InlineData("404")]
    [InlineData("409")]
    [InlineData("429")]
    [InlineData("499")]
    [InlineData("600")]
    [InlineData("json")]
    [InlineData("unexpected")]
    public async Task RunAsyncPropagatesNonretryableFailureImmediately(string failureKind)
    {
        var failure = CreateFailure(failureKind);
        await using var harness = new RunnerHarness((_, _) =>
            Task.FromException<SimulatorDeviceHeartbeatResponse>(failure));
        var operation = harness.Start(CreateDevice());

        var exception = await Record.ExceptionAsync(() => AwaitAsync(operation));

        Assert.Same(failure, exception);
        Assert.Single(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Empty(harness.Logger.Entries);
    }

    /// <summary>
    /// Verifies that a configured limit of one permits no retry or interval delay.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithFailureLimitOfOneStopsAfterFirstFailure()
    {
        var failure = new HttpRequestException("Network is unavailable.");
        await using var harness = new RunnerHarness((_, _) =>
            Task.FromException<SimulatorDeviceHeartbeatResponse>(failure), maxFailures: 1);
        var operation = harness.Start(CreateDevice());

        var exception = await Record.ExceptionAsync(() => AwaitAsync(operation));

        Assert.Same(failure, exception);
        Assert.Single(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        var terminal = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Error, terminal.Level);
        Assert.Same(failure, terminal.Exception);
        Assert.Equal(1, Assert.IsType<int>(terminal.Properties["MaxFailures"]));
    }

    /// <summary>Verifies that caller cancellation prevents the first request.</summary>
    [Fact]
    public async Task RunAsyncWithCanceledTokenDoesNotSendHeartbeat()
    {
        await using var harness = new RunnerHarness();
        await harness.Cancellation.CancelAsync();
        var operation = harness.Start(CreateDevice());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));

        Assert.True(operation.IsCanceled);
        Assert.Empty(harness.Client.Requests);
        Assert.Empty(harness.Logger.Entries);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>
    /// Verifies token forwarding and immediate cancellation of an in-flight
    /// request without counting caller cancellation as a transient failure.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithCancellationDuringRequestDoesNotRetry()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (id, token) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateResponse(id);
        });
        var operation = harness.Start(CreateDevice());
        await AwaitAsync(requestStarted.Task);

        await harness.Cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AwaitAsync(operation));
        Assert.Equal(harness.Cancellation.Token, exception.CancellationToken);
        Assert.True(operation.IsCanceled);
        Assert.Single(harness.Client.Requests);
        Assert.Empty(harness.Logger.Entries);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>
    /// Verifies that cancellation interrupts either a normal interval or a retry
    /// delay, without advancing time or sending another heartbeat.
    /// </summary>
    /// <param name="requestFails">Whether the interval follows a failed request.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWithCancellationDuringDelayStopsImmediately(bool requestFails)
    {
        await using var harness = new RunnerHarness((id, _) => requestFails
            ? Task.FromException<SimulatorDeviceHeartbeatResponse>(
                new HttpRequestException("Temporary failure."))
            : Task.FromResult(CreateResponse(id)));
        var operation = harness.Start(CreateDevice());
        await harness.Clock.WaitForDelayAsync();
        var previousLogCount = harness.Logger.Entries.Count;

        await harness.Cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));

        Assert.True(operation.IsCanceled);
        Assert.Single(harness.Client.Requests);
        Assert.Equal(previousLogCount, harness.Logger.Entries.Count);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(harness.Client.Requests);
    }

    /// <summary>
    /// Verifies that two calls on the same runner have independent failure
    /// counts, and one device can continue after the other reaches its limit.
    /// </summary>
    [Fact]
    public async Task RunAsyncKeepsConcurrentDeviceFailuresIndependent()
    {
        var failingDevice = CreateDevice(1);
        var healthyDevice = CreateDevice(2);
        var failure = new HttpRequestException("First device is unreachable.");
        var healthyRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyResponse = new TaskCompletionSource<SimulatorDeviceHeartbeatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyCalls = 0;
        await using var harness = new RunnerHarness((id, token) =>
        {
            if (id == failingDevice.Id)
            {
                return Task.FromException<SimulatorDeviceHeartbeatResponse>(failure);
            }

            if (Interlocked.Increment(ref healthyCalls) == 1)
            {
                healthyRequestStarted.TrySetResult();
                return healthyResponse.Task.WaitAsync(token);
            }

            return Task.FromResult(CreateResponse(id));
        });

        var failingOperation = harness.Start(failingDevice);
        await harness.Clock.WaitForDelayAsync();
        var healthyOperation = harness.Start(healthyDevice);
        await AwaitAsync(healthyRequestStarted.Task);

        // The second device remains in-flight while the first reaches four failures.
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            harness.Clock.Advance(HeartbeatInterval);
            await harness.Clock.WaitForDelayAsync();
        }

        Assert.Equal(4, harness.Client.Requests.Count(request => request.DeviceId == failingDevice.Id));

        // This success must not reset the first device's failure count.
        healthyResponse.SetResult(CreateResponse(healthyDevice.Id));
        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(HeartbeatInterval);

        var exception = await Record.ExceptionAsync(() => AwaitAsync(failingOperation));
        await harness.Clock.WaitForDelayAsync();

        Assert.Same(failure, exception);
        Assert.Equal(5, harness.Client.Requests.Count(request => request.DeviceId == failingDevice.Id));
        Assert.Equal(2, harness.Client.Requests.Count(request => request.DeviceId == healthyDevice.Id));
        Assert.False(healthyOperation.IsCompleted);

        harness.Clock.Advance(HeartbeatInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(5, harness.Client.Requests.Count(request => request.DeviceId == failingDevice.Id));
        Assert.Equal(3, harness.Client.Requests.Count(request => request.DeviceId == healthyDevice.Id));
        var terminal = Assert.Single(harness.Logger.Entries.Where(entry => entry.Level == LogLevel.Error));
        Assert.Equal(failingDevice.Id, Assert.IsType<Guid>(terminal.Properties["DeviceId"]));
    }

    /// <summary>Creates the validated options expected by the runner.</summary>
    /// <param name="maxFailures">The configured consecutive failure limit.</param>
    /// <returns>Options with a positive interval and failure limit.</returns>
    private static IOptions<DeviceSimulatorOptions> CreateOptions(int maxFailures = 5) =>
        Options.Create(new DeviceSimulatorOptions
        {
            HeartbeatInterval = HeartbeatInterval,
            MaxConsecutiveHeartbeatFailures = maxFailures
        });

    /// <summary>Creates an active device with a deterministic identifier.</summary>
    /// <param name="number">The suffix used to distinguish simulated devices.</param>
    /// <returns>An active temperature sensor.</returns>
    private static SimulatorDeviceResponse CreateDevice(int number = 1) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
            $"WH-{number:D3}",
            $"Warehouse Sensor {number}",
            new[] { SimulatorDeviceCapability.Temperature },
            SimulatorDeviceLifecycle.Active);

    /// <summary>Creates a valid heartbeat response with a server-owned timestamp.</summary>
    /// <param name="deviceId">The responding device identifier.</param>
    /// <param name="changed">Whether the stored timestamp changed.</param>
    /// <returns>A successful heartbeat response.</returns>
    private static SimulatorDeviceHeartbeatResponse CreateResponse(Guid deviceId, bool changed = true) =>
        new(deviceId, ServerHeartbeatUtc, changed);

    /// <summary>Creates a distinct exception for a failure-policy test.</summary>
    /// <param name="kind">A failure name or numeric HTTP status code.</param>
    /// <param name="attempt">The attempt number included in the message.</param>
    /// <returns>The requested exception instance.</returns>
    private static Exception CreateFailure(string kind, int attempt = 1)
    {
        var message = $"Simulated {kind} failure on attempt {attempt}.";
        return kind switch
        {
            "network" => new HttpRequestException(message),
            "cancellation" => new OperationCanceledException(message),
            "timeout" => new TaskCanceledException(message, new TimeoutException("Request timed out.")),
            "json" => new JsonException(message),
            "unexpected" => new NotSupportedException(message),
            _ => new HttpRequestException(message, null, (HttpStatusCode)int.Parse(kind))
        };
    }

    /// <summary>Bounds a task wait using the test cancellation token.</summary>
    /// <param name="operation">The operation to observe.</param>
    /// <returns>A task that completes with the observed operation.</returns>
    private static Task AwaitAsync(Task operation) =>
        operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

    /// <summary>
    /// Owns one runner and all tasks started by a test, with cancellation
    /// and bounded cleanup even when an assertion fails.
    /// </summary>
    private sealed class RunnerHarness : IAsyncDisposable
    {
        private readonly List<Task> _operations = new();

        /// <summary>Creates a runner using a fake API and an observable fake clock.</summary>
        /// <param name="send">The heartbeat behavior, or null for success.</param>
        /// <param name="maxFailures">The configured consecutive failure limit.</param>
        public RunnerHarness(
            Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>>? send = null,
            int maxFailures = 5)
        {
            Client = new StubApiClient(send);
            Runner = new DeviceHeartbeatRunner(Logger, Client, CreateOptions(maxFailures), Clock);
        }

        /// <summary>Gets the caller cancellation source for this test.</summary>
        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Gets the manually advanced clock.</summary>
        public ObservedTimeProvider Clock { get; } = new();

        /// <summary>Gets the captured structured logs.</summary>
        public RecordingLogger Logger { get; } = new();

        /// <summary>Gets the fake API client.</summary>
        public StubApiClient Client { get; }

        /// <summary>Gets the production runner under test.</summary>
        public DeviceHeartbeatRunner Runner { get; }

        /// <summary>Starts a device loop and tracks its task for cleanup.</summary>
        /// <param name="device">The device passed directly to the runner.</param>
        /// <returns>The runner task, including any validation failure.</returns>
        public Task Start(SimulatorDeviceResponse device)
        {
            var operation = Runner.RunAsync(device, Cancellation.Token);
            _operations.Add(operation);
            return operation;
        }

        /// <summary>
        /// Cancels remaining loops and observes their completion. Test bodies
        /// assert expected failures; cleanup also observes faults after a failed
        /// assertion so background tasks do not leak into subsequent tests.
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
                        // Cleanup must remain possible after the test token is canceled.
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
    /// Delegates scheduling to FakeTimeProvider and reports timer registration,
    /// allowing tests to advance time only after a delay has been scheduled.
    /// </summary>
    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(
            new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero));
        private readonly Channel<TimerRegistration> _registrations =
            Channel.CreateUnbounded<TimerRegistration>();
        private int _timerCount;

        /// <summary>Gets the total number of timers created through this provider.</summary>
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
            _registrations.Writer.TryWrite(new TimerRegistration(dueTime, period));
            return timer;
        }

        /// <summary>Advances fake time and runs callbacks that become due.</summary>
        /// <param name="elapsed">The amount of simulated time to advance.</param>
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        /// <summary>
        /// Waits for the next registered delay and checks its configured duration.
        /// </summary>
        /// <returns>A task representing observation of one scheduled delay.</returns>
        public async Task WaitForDelayAsync()
        {
            var registration = await _registrations.Reader
                .ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(HeartbeatInterval, registration.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, registration.Period);
        }
    }

    /// <summary>Records one timer registration for deterministic coordination.</summary>
    /// <param name="DueTime">The delay before the timer becomes due.</param>
    /// <param name="Period">The timer repeat period.</param>
    private sealed record TimerRegistration(TimeSpan DueTime, TimeSpan Period);

    /// <summary>Captures heartbeat requests and invokes a configured response delegate.</summary>
    private sealed class StubApiClient : IDeviceSimulatorApiClient
    {
        private readonly Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>> _send;
        private readonly ConcurrentQueue<HeartbeatRequest> _requests = new();

        /// <summary>Creates an API stub with success as its default behavior.</summary>
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
            throw new NotSupportedException("A heartbeat runner must not send telemetry.");

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
            throw new NotSupportedException("A heartbeat runner must not synchronize the catalog.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A heartbeat runner must not retrieve devices.");

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(
            Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A heartbeat runner must not activate devices.");
    }

    /// <summary>Records the arguments passed to one heartbeat request.</summary>
    /// <param name="DeviceId">The requested device identifier.</param>
    /// <param name="CancellationToken">The caller token forwarded to the API.</param>
    private sealed record HeartbeatRequest(Guid DeviceId, CancellationToken CancellationToken);

    /// <summary>Captures structured runner logs safely across asynchronous callbacks.</summary>
    private sealed class RecordingLogger : ILogger<DeviceHeartbeatRunner>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        /// <summary>Gets a snapshot of all recorded log entries.</summary>
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

            _entries.Enqueue(new LogEntry(logLevel, exception, properties));
        }
    }

    /// <summary>Represents a captured log entry and its structured values.</summary>
    /// <param name="Level">The severity of the entry.</param>
    /// <param name="Exception">The original exception supplied to the logger.</param>
    /// <param name="Properties">The structured properties supplied to the logger.</param>
    private sealed record LogEntry(
        LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> Properties);
}
