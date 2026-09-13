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
/// Verifies telemetry batches, stable measurements during retries, failure limits,
/// cancellation, and independent device execution using controlled time.
/// </summary>
/// <remarks>
/// Uses the production NormalTelemetryGenerator and DeviceTelemetryRunner.
/// Requires Microsoft.Extensions.TimeProvider.Testing, as do the heartbeat tests.
/// No real HTTP requests or interval waits are used. Real timeouts only bound
/// asynchronous coordination and cleanup when a regression occurs.
/// </remarks>
public sealed class DeviceTelemetryRunnerTests
{
    /// <summary>Bounds asynchronous coordination and cleanup.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Defines the configured cycle and retry delay.</summary>
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Defines the initial UTC instant supplied to the generator.</summary>
    private static readonly DateTimeOffset InitialUtc =
        new DateTimeOffset(2026, 9, 12, 22, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    /// <summary>Verifies that every constructor dependency is required.</summary>
    /// <param name="dependency">The constructor dependency to omit.</param>
    [Theory]
    [InlineData("logger")]
    [InlineData("apiClient")]
    [InlineData("telemetryGenerator")]
    [InlineData("options")]
    [InlineData("timeProvider")]
    public void ConstructorWithNullDependencyThrowsArgumentNullException(string dependency)
    {
        var clock = new ObservedTimeProvider();
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new DeviceTelemetryRunner(
                dependency == "logger" ? null! : new RecordingLogger(),
                dependency == "apiClient" ? null! : new StubApiClient(),
                dependency == "telemetryGenerator" ? null! : new NormalTelemetryGenerator(clock),
                dependency == "options" ? null! : CreateOptions(),
                dependency == "timeProvider" ? null! : clock));

        Assert.Equal(dependency, exception.ParamName);
    }

    /// <summary>Verifies that a missing device is rejected before sending.</summary>
    [Fact]
    public async Task RunAsyncWithNullDeviceThrowsArgumentNullException()
    {
        await using var harness = new RunnerHarness();
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => AwaitAsync(harness.Start(null!)));

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
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => AwaitAsync(operation));

        Assert.Equal("device", exception.ParamName);
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>Verifies that only active devices can enter the loop.</summary>
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

    /// <summary>Verifies propagation of a missing capability collection.</summary>
    [Fact]
    public async Task RunAsyncWithNullCapabilitiesDoesNotSend()
    {
        await using var harness = new RunnerHarness();
        var operation = harness.Start(CreateDevice() with { Capabilities = null! });
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => AwaitAsync(operation));

        Assert.Equal("device", exception.ParamName);
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>Verifies that an invalid batch is rejected before any HTTP request.</summary>
    /// <param name="capability">The unsupported capability following a valid one.</param>
    [Theory]
    [InlineData((SimulatorDeviceCapability)(-1))]
    [InlineData((SimulatorDeviceCapability)999)]
    public async Task RunAsyncWithUnsupportedCapabilityDoesNotSendPartialBatch(
        SimulatorDeviceCapability capability)
    {
        await using var harness = new RunnerHarness();
        var operation = harness.Start(CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, capability }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => AwaitAsync(operation));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
    }

    /// <summary>Verifies normal completion and a warning when no metrics are available.</summary>
    [Fact]
    public async Task RunAsyncWithNoCapabilitiesLogsWarningAndCompletes()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with { Capabilities = Array.Empty<SimulatorDeviceCapability>() };
        await AwaitAsync(harness.Start(device));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(device.Code, Assert.IsType<string>(entry.Properties["DeviceCode"]));
        Assert.Equal(device.Id, Assert.IsType<Guid>(entry.Properties["DeviceId"]));
    }

    /// <summary>
    /// Verifies an immediate batch, one delay per completed cycle, new measurements
    /// on the next cycle, and successful handling of duplicate acknowledgments.
    /// </summary>
    /// <param name="recorded">Whether the API reports inserting a new measurement.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncSendsBatchImmediatelyAndRegeneratesAfterInterval(bool recorded)
    {
        await using var harness = new RunnerHarness((_, measurement, _) =>
            Task.FromResult(CreateResponse(measurement, recorded)));
        var device = CreateDevice() with
        {
            Capabilities = new[]
            {
                SimulatorDeviceCapability.Temperature,
                SimulatorDeviceCapability.Humidity,
                SimulatorDeviceCapability.DoorState,
                SimulatorDeviceCapability.LightState,
                SimulatorDeviceCapability.Temperature
            }
        };
        var operation = harness.Start(device);
        await harness.Clock.WaitForDelayAsync();

        var firstBatch = harness.Client.Requests.ToArray();
        Assert.Equal(4, firstBatch.Length);
        Assert.Equal(1, harness.Clock.TimerCount);
        Assert.False(operation.IsCompleted);
        Assert.Equal(
            new[] { SimulatorTelemetryMetric.Temperature, SimulatorTelemetryMetric.Humidity,
                SimulatorTelemetryMetric.DoorState, SimulatorTelemetryMetric.LightState }.OrderBy(x => x),
            firstBatch.Select(x => x.Measurement.Metric).OrderBy(x => x));
        Assert.Equal(4, firstBatch.Select(x => x.Measurement.MeasurementId).Distinct().Count());
        Assert.All(firstBatch, request =>
        {
            Assert.Equal(device.Id, request.DeviceId);
            Assert.Equal(harness.Cancellation.Token, request.CancellationToken);
            Assert.NotEqual(Guid.Empty, request.Measurement.MeasurementId);
            Assert.Equal(InitialUtc.UtcDateTime, request.Measurement.MeasuredAtUtc);
            Assert.Equal(DateTimeKind.Utc, request.Measurement.MeasuredAtUtc.Kind);
        });
        // At 22:00 in the configured UTC zone, both scheduled states are false.
        Assert.False(firstBatch.Single(x => x.Measurement.Metric == SimulatorTelemetryMetric.DoorState)
            .Measurement.StateValue);
        Assert.False(firstBatch.Single(x => x.Measurement.Metric == SimulatorTelemetryMetric.LightState)
            .Measurement.StateValue);
        Assert.Equal(4, harness.Logger.Entries.Count);
        foreach (var request in firstBatch)
        {
            var entry = Assert.Single(harness.Logger.Entries.Where(x =>
                Equals(x.Properties["MeasurementId"], request.Measurement.MeasurementId)));
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal(device.Id, Assert.IsType<Guid>(entry.Properties["DeviceId"]));
            Assert.Equal(request.Measurement.Metric,
                Assert.IsType<SimulatorTelemetryMetric>(entry.Properties["Metric"]));
            Assert.Equal(recorded, Assert.IsType<bool>(entry.Properties["Recorded"]));
        }

        harness.Clock.Advance(TelemetryInterval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(4, harness.Client.Requests.Count);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await harness.Clock.WaitForDelayAsync();

        var all = harness.Client.Requests;
        Assert.Equal(8, all.Count);
        Assert.Equal(2, harness.Clock.TimerCount);
        Assert.Equal(8, all.Select(x => x.Measurement.MeasurementId).Distinct().Count());
        Assert.All(all.Skip(4), request =>
            Assert.Equal((InitialUtc + TelemetryInterval).UtcDateTime, request.Measurement.MeasuredAtUtc));
    }

    /// <summary>
    /// Verifies sequential sends and a full delay after the batch completes,
    /// including when a pending request must be retried before the next metric.
    /// </summary>
    /// <param name="requestFails">Whether the pending request initially fails.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWaitsForRequestsBeforeStartingDelay(bool requestFails)
    {
        var pending = new TaskCompletionSource<SimulatorTelemetryRecordingResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var harness = new RunnerHarness((_, measurement, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                started.TrySetResult();
                return pending.Task.WaitAsync(token);
            }
            return Task.FromResult(CreateResponse(measurement));
        });
        _ = harness.Start(CreateDevice() with
        {
            Capabilities = new[]
            {
                SimulatorDeviceCapability.Temperature,
                SimulatorDeviceCapability.Humidity,
                SimulatorDeviceCapability.LightState
            }
        });
        await AwaitAsync(started.Task);
        var lastMeasurement = harness.Client.Requests[1].Measurement;

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, harness.Client.Requests.Count);
        Assert.Equal(0, harness.Clock.TimerCount);

        if (requestFails)
            pending.SetException(new HttpRequestException("Connection was interrupted."));
        else
            pending.SetResult(CreateResponse(lastMeasurement));

        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(TelemetryInterval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(requestFails ? 2 : 3, harness.Client.Requests.Count);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(requestFails ? 4 : 6, harness.Client.Requests.Count);
        if (requestFails)
            Assert.Same(lastMeasurement, harness.Client.Requests[2].Measurement);
    }

    /// <summary>
    /// Verifies exactly five failed attempts, four delays, unchanged payloads,
    /// no later metric, structured diagnostics, and the original terminal exception.
    /// </summary>
    /// <param name="failureKind">The transient failure to simulate.</param>
    [Theory]
    [InlineData("network")]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("599")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    public async Task RunAsyncRetriesSameMeasurementAndStopsOnFifthFailure(string failureKind)
    {
        var failures = Enumerable.Range(1, 5).Select(n => CreateFailure(failureKind, n)).ToArray();
        var calls = 0;
        await using var harness = new RunnerHarness((_, _, _) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return Task.FromException<SimulatorTelemetryRecordingResponse>(
                failures[Math.Min(index, failures.Length - 1)]);
        });
        var device = CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity }
        };
        var operation = harness.Start(device);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            await harness.Clock.WaitForDelayAsync();
            Assert.Equal(attempt, harness.Client.Requests.Count);
            Assert.False(operation.IsCompleted);
            harness.Clock.Advance(TelemetryInterval - TimeSpan.FromMilliseconds(1));
            Assert.Equal(attempt, harness.Client.Requests.Count);
            harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        var exception = await Record.ExceptionAsync(() => AwaitAsync(operation));

        Assert.Same(failures[4], exception);
        Assert.Equal(5, harness.Client.Requests.Count);
        Assert.Equal(4, harness.Clock.TimerCount);
        Assert.False(harness.Cancellation.IsCancellationRequested);
        var original = harness.Client.Requests[0].Measurement;
        Assert.All(harness.Client.Requests, request =>
        {
            Assert.Same(original, request.Measurement);
            Assert.Equal(device.Id, request.DeviceId);
            Assert.Equal(harness.Cancellation.Token, request.CancellationToken);
            Assert.Equal(InitialUtc.UtcDateTime, request.Measurement.MeasuredAtUtc);
        });
        var warnings = harness.Logger.Entries.Where(x => x.Level == LogLevel.Warning).ToArray();
        Assert.Equal(4, warnings.Length);
        for (var index = 0; index < warnings.Length; index++)
        {
            Assert.Same(failures[index], warnings[index].Exception);
            Assert.Equal(index + 1, Assert.IsType<int>(warnings[index].Properties["ConsecutiveFailures"]));
            Assert.Equal(5, Assert.IsType<int>(warnings[index].Properties["MaxFailures"]));
            Assert.Equal(TelemetryInterval, Assert.IsType<TimeSpan>(warnings[index].Properties["RetryDelay"]));
        }
        var terminal = Assert.Single(harness.Logger.Entries.Where(x => x.Level == LogLevel.Error));
        Assert.Same(failures[4], terminal.Exception);
        Assert.Equal(5, Assert.IsType<int>(terminal.Properties["ConsecutiveFailures"]));
        Assert.All(harness.Logger.Entries, entry =>
        {
            Assert.Equal(device.Id, Assert.IsType<Guid>(entry.Properties["DeviceId"]));
            Assert.Equal(original.MeasurementId, Assert.IsType<Guid>(entry.Properties["MeasurementId"]));
            Assert.Equal(original.Metric, Assert.IsType<SimulatorTelemetryMetric>(entry.Properties["Metric"]));
        });
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Information);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(5, harness.Client.Requests.Count);
    }

    /// <summary>
    /// Verifies that a successful acknowledgment releases the next measurement
    /// with its own failure budget, including when Recorded is false.
    /// </summary>
    /// <param name="recorded">Whether the successful response reports a new insert.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncStartsFreshFailureCountForNextMeasurement(bool recorded)
    {
        var attempts = new Dictionary<Guid, int>();
        await using var harness = new RunnerHarness((_, measurement, _) =>
        {
            attempts.TryGetValue(measurement.MeasurementId, out var count);
            attempts[measurement.MeasurementId] = ++count;
            return count < 5
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(new HttpRequestException("Transient failure."))
                : Task.FromResult(CreateResponse(measurement, recorded));
        });
        var operation = harness.Start(CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity }
        });
        // Four retry delays per measurement; the ninth timer is the cycle delay.
        for (var delay = 0; delay < 8; delay++)
        {
            await harness.Clock.WaitForDelayAsync();
            harness.Clock.Advance(TelemetryInterval);
        }
        await harness.Clock.WaitForDelayAsync();

        var requests = harness.Client.Requests;
        Assert.Equal(10, requests.Count);
        Assert.False(operation.IsCompleted);
        Assert.Equal(9, harness.Clock.TimerCount);
        Assert.All(requests.Take(5), request => Assert.Same(requests[0].Measurement, request.Measurement));
        Assert.All(requests.Skip(5), request => Assert.Same(requests[5].Measurement, request.Measurement));
        Assert.NotEqual(requests[0].Measurement.MeasurementId, requests[5].Measurement.MeasurementId);
        Assert.NotEqual(requests[0].Measurement.Metric, requests[5].Measurement.Metric);
        Assert.Equal(new[] { 1, 2, 3, 4, 1, 2, 3, 4 }, harness.Logger.Entries
            .Where(x => x.Level == LogLevel.Warning)
            .Select(x => Assert.IsType<int>(x.Properties["ConsecutiveFailures"])));
        Assert.Equal(2, harness.Logger.Entries.Count(x => x.Level == LogLevel.Information));
        Assert.DoesNotContain(harness.Logger.Entries, x => x.Level == LogLevel.Error);
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
        await using var harness = new RunnerHarness((_, _, _) =>
            Task.FromException<SimulatorTelemetryRecordingResponse>(failure));
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
        await using var harness = new RunnerHarness((_, _, _) =>
            Task.FromException<SimulatorTelemetryRecordingResponse>(failure), maxFailures: 1);
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
    public async Task RunAsyncWithCanceledTokenDoesNotSendTelemetry()
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
        await using var harness = new RunnerHarness(async (_, measurement, token) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateResponse(measurement);
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
    /// delay, without advancing time or sending another telemetry request.
    /// </summary>
    /// <param name="requestFails">Whether the interval follows a failed request.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWithCancellationDuringDelayStopsImmediately(bool requestFails)
    {
        await using var harness = new RunnerHarness((_, measurement, _) => requestFails
            ? Task.FromException<SimulatorTelemetryRecordingResponse>(
                new HttpRequestException("Temporary failure."))
            : Task.FromResult(CreateResponse(measurement)));
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
    /// Verifies cancellation between successful measurements, even when the API
    /// returns a success response after the caller has requested cancellation.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithCancellationAfterSuccessDoesNotSendNextMeasurement()
    {
        CancellationTokenSource? caller = null;
        await using var harness = new RunnerHarness((_, measurement, _) =>
        {
            caller!.Cancel();
            return Task.FromResult(CreateResponse(measurement));
        });
        caller = harness.Cancellation;
        var operation = harness.Start(CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));

        Assert.Single(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.DoesNotContain(harness.Logger.Entries,
            entry => entry.Level is LogLevel.Warning or LogLevel.Error);
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
        var healthyResponse = new TaskCompletionSource<SimulatorTelemetryRecordingResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyCalls = 0;
        await using var harness = new RunnerHarness((id, measurement, token) =>
        {
            if (id == failingDevice.Id)
            {
                return Task.FromException<SimulatorTelemetryRecordingResponse>(failure);
            }

            if (Interlocked.Increment(ref healthyCalls) == 1)
            {
                healthyRequestStarted.TrySetResult();
                return healthyResponse.Task.WaitAsync(token);
            }

            return Task.FromResult(CreateResponse(measurement));
        });

        var failingOperation = harness.Start(failingDevice);
        await harness.Clock.WaitForDelayAsync();
        var healthyOperation = harness.Start(healthyDevice);
        await AwaitAsync(healthyRequestStarted.Task);

        // The second device remains in-flight while the first reaches four failures.
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            harness.Clock.Advance(TelemetryInterval);
            await harness.Clock.WaitForDelayAsync();
        }

        Assert.Equal(4, harness.Client.Requests.Count(request => request.DeviceId == failingDevice.Id));

        // This success must not reset the first device's failure count.
        healthyResponse.SetResult(CreateResponse(harness.Client.Requests
            .Single(request => request.DeviceId == healthyDevice.Id).Measurement));
        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(TelemetryInterval);

        var exception = await Record.ExceptionAsync(() => AwaitAsync(failingOperation));
        await harness.Clock.WaitForDelayAsync();

        Assert.Same(failure, exception);
        Assert.Equal(5, harness.Client.Requests.Count(request => request.DeviceId == failingDevice.Id));
        Assert.Equal(2, harness.Client.Requests.Count(request => request.DeviceId == healthyDevice.Id));
        Assert.False(healthyOperation.IsCompleted);

        harness.Clock.Advance(TelemetryInterval);
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
            TelemetryInterval = TelemetryInterval,
            MaxConsecutiveTelemetryFailures = maxFailures,
            WarehouseTimeZoneId = "UTC"
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

    /// <summary>Creates a valid acknowledgment for an existing measurement.</summary>
    /// <param name="measurement">The measurement acknowledged by the API.</param>
    /// <param name="recorded">Whether the API inserted a new measurement.</param>
    /// <returns>A successful telemetry response.</returns>
    private static SimulatorTelemetryRecordingResponse CreateResponse(
        SimulatorTelemetryMeasurementRequest measurement, bool recorded = true) =>
        new(measurement.MeasurementId, recorded);

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
        /// <param name="send">The telemetry behavior, or null for success.</param>
        /// <param name="maxFailures">The configured consecutive failure limit.</param>
        public RunnerHarness(
            Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken, Task<SimulatorTelemetryRecordingResponse>>? send = null,
            int maxFailures = 5)
        {
            Client = new StubApiClient(send);
            Runner = new DeviceTelemetryRunner(
                Logger, Client, new NormalTelemetryGenerator(Clock), CreateOptions(maxFailures), Clock);
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
        public DeviceTelemetryRunner Runner { get; }

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
        private readonly FakeTimeProvider _clock = new(InitialUtc);
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

            Assert.Equal(TelemetryInterval, registration.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, registration.Period);
        }
    }

    /// <summary>Records one timer registration for deterministic coordination.</summary>
    /// <param name="DueTime">The delay before the timer becomes due.</param>
    /// <param name="Period">The timer repeat period.</param>
    private sealed record TimerRegistration(TimeSpan DueTime, TimeSpan Period);

    /// <summary>Captures telemetry requests and invokes a configured response delegate.</summary>
    private sealed class StubApiClient : IDeviceSimulatorApiClient
    {
        private readonly Func<Guid, SimulatorTelemetryMeasurementRequest,
            CancellationToken, Task<SimulatorTelemetryRecordingResponse>> _send;
        private readonly ConcurrentQueue<TelemetryRequest> _requests = new();

        /// <summary>Creates an API stub with success as its default behavior.</summary>
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
            throw new NotSupportedException("A telemetry runner must not send heartbeats.");

        /// <inheritdoc />
        public Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not synchronize the catalog.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not retrieve devices.");

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(
            Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A telemetry runner must not activate devices.");
    }

    /// <summary>Records the arguments passed to one telemetry request.</summary>
    /// <param name="DeviceId">The requested device identifier.</param>
    /// <param name="Measurement">The original measurement object sent to the API.</param>
    /// <param name="CancellationToken">The caller token forwarded to the API.</param>
    private sealed record TelemetryRequest(
        Guid DeviceId, SimulatorTelemetryMeasurementRequest Measurement,
        CancellationToken CancellationToken);

    /// <summary>Captures structured runner logs safely across asynchronous callbacks.</summary>
    private sealed class RecordingLogger : ILogger<DeviceTelemetryRunner>
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
