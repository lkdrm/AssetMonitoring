using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
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
/// cancellation, scenario integration, and independent device execution using
/// controlled time.
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

    /// <summary>
    /// Verifies that the scenario overload accepts null and preserves normal
    /// numeric ranges, scheduled states, timestamps, and request identities.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithNullScenarioSendsNormalTelemetry()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with
        {
            Capabilities = new[]
            {
                SimulatorDeviceCapability.Temperature,
                SimulatorDeviceCapability.Humidity,
                SimulatorDeviceCapability.DoorState,
                SimulatorDeviceCapability.LightState
            }
        };

        var operation = harness.Start(device, null, harness.Clock.GetTimestamp());
        await harness.Clock.WaitForDelayAsync();

        var requests = harness.Client.Requests;
        Assert.Equal(4, requests.Count);
        Assert.False(operation.IsCompleted);
        Assert.Equal(4, requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
        Assert.All(requests, request =>
        {
            Assert.Equal(device.Id, request.DeviceId);
            Assert.Equal(harness.Cancellation.Token, request.CancellationToken);
            Assert.NotEqual(Guid.Empty, request.Measurement.MeasurementId);
            Assert.Equal(InitialUtc.UtcDateTime, request.Measurement.MeasuredAtUtc);
            Assert.Equal(DateTimeKind.Utc, request.Measurement.MeasuredAtUtc.Kind);
        });
        var temperature = Assert.Single(requests.Where(x =>
            x.Measurement.Metric == SimulatorTelemetryMetric.Temperature)).Measurement;
        Assert.InRange(Assert.IsType<double>(temperature.NumericValue), 20.0, 24.0);
        Assert.Null(temperature.StateValue);
        AssertNormalNonTemperatureMeasurements(requests);
    }

    /// <summary>
    /// Verifies that elapsed simulation time drives pending, active, recovering,
    /// and completed phases across successive telemetry cycles.
    /// </summary>
    [Fact]
    public async Task RunAsyncAdvancesScenarioThroughAllPhasesUsingSimulationTime()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice();
        var definition = CreateScenarioDefinition() with
        {
            StartsAfter = TimeSpan.FromMinutes(2),
            Duration = TimeSpan.FromMinutes(2),
            RecoveryDuration = TimeSpan.FromMinutes(1),
            MaximumRisePerMeasurement = 20,
            MaximumRecoveryPerMeasurement = 20
        };
        var scenario = new HighTemperatureScenarioRuntime(definition, device.Id, new FixedScenarioRandom());
        var expectedPhases = new[]
        {
            ScenarioPhase.Pending, ScenarioPhase.Pending,
            ScenarioPhase.Active, ScenarioPhase.Active,
            ScenarioPhase.Recovering, ScenarioPhase.Completed, ScenarioPhase.Completed
        };
        var operation = harness.Start(device, CreateSequence(device.Id, scenario), harness.Clock.GetTimestamp());

        for (var cycle = 0; cycle < expectedPhases.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();

            Assert.Equal(cycle + 1, harness.Client.Requests.Count);
            Assert.Equal(expectedPhases[cycle], scenario.Phase);
            var measurement = harness.Client.Requests[cycle].Measurement;
            var temperature = Assert.IsType<double>(measurement.NumericValue);
            if (cycle < 2)
                Assert.Null(scenario.CurrentTemperature);
            else if (cycle <= 5)
                Assert.Equal(temperature, Assert.IsType<double>(scenario.CurrentTemperature));
            else
                Assert.Equal(harness.Client.Requests[5].Measurement.NumericValue,
                    scenario.CurrentTemperature);
            Assert.Equal(SimulatorTelemetryMetric.Temperature, measurement.Metric);
            Assert.Null(measurement.StateValue);
            Assert.Equal((InitialUtc + TimeSpan.FromMinutes(cycle)).UtcDateTime, measurement.MeasuredAtUtc);
            if (cycle is >= 2 and <= 4)
                Assert.Equal(32.0, temperature);
            else
                Assert.InRange(temperature, 20.0, 24.0);

            if (cycle < expectedPhases.Length - 1)
                harness.Clock.Advance(TelemetryInterval);
        }

        Assert.False(operation.IsCompleted);
        Assert.Equal(expectedPhases.Length, harness.Client.Requests
            .Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>
    /// Verifies one temperature increase per new batch, state reuse across
    /// cycles, and unchanged humidity, door, and light measurements.
    /// </summary>
    [Fact]
    public async Task RunAsyncAppliesScenarioOnlyOncePerTemperatureMeasurement()
    {
        await using var harness = new RunnerHarness();
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
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition(), device.Id, new FixedScenarioRandom());
        Assert.Equal(24.0, scenario.GetNextTemperature(24, TimeSpan.Zero));
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        _ = harness.Start(device, CreateSequence(device.Id, scenario), simulationStartedAt);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();

            Assert.Equal((cycle + 1) * 4, harness.Client.Requests.Count);
            var batch = harness.Client.Requests.Skip(cycle * 4).Take(4).ToArray();
            var temperature = Assert.Single(batch.Where(x =>
                x.Measurement.Metric == SimulatorTelemetryMetric.Temperature)).Measurement;
            var expectedTemperature = cycle == 0 ? 26.0 : 28.0;
            Assert.Equal(expectedTemperature, Assert.IsType<double>(temperature.NumericValue));
            Assert.Equal(expectedTemperature, Assert.IsType<double>(scenario.CurrentTemperature));
            Assert.Equal(ScenarioPhase.Active, scenario.Phase);
            Assert.Null(temperature.StateValue);
            Assert.All(batch, request =>
            {
                Assert.Equal(device.Id, request.DeviceId);
                Assert.NotEqual(Guid.Empty, request.Measurement.MeasurementId);
                Assert.Equal((InitialUtc + TimeSpan.FromMinutes(cycle + 1)).UtcDateTime,
                    request.Measurement.MeasuredAtUtc);
            });
            AssertNormalNonTemperatureMeasurements(batch);

            if (cycle == 0)
                harness.Clock.Advance(TelemetryInterval);
        }

        Assert.Equal(8, harness.Client.Requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>
    /// Verifies that a device without temperature capability never invokes the
    /// scenario, even when the supplied runtime would reject a temperature call.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithoutTemperatureCapabilityLeavesScenarioUntouched()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with
        {
            Capabilities = new[]
            {
                SimulatorDeviceCapability.Humidity,
                SimulatorDeviceCapability.DoorState,
                SimulatorDeviceCapability.LightState
            }
        };
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition() with { AutoRecover = false, RecoveryDuration = null },
            device.Id, new FixedScenarioRandom());
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        _ = harness.Start(device, CreateSequence(device.Id, scenario), simulationStartedAt);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(3, harness.Client.Requests.Count);
        AssertNormalNonTemperatureMeasurements(harness.Client.Requests);
        Assert.Equal(ScenarioPhase.Pending, scenario.Phase);
        Assert.Null(scenario.CurrentTemperature);
    }

    /// <summary>
    /// Verifies that a device entering the runner later still uses the shared
    /// simulation origin rather than restarting its scenario timeline.
    /// </summary>
    [Fact]
    public async Task RunAsyncUsesSharedStartTimestampForDevicesStartedAtDifferentTimes()
    {
        await using var harness = new RunnerHarness();
        var firstDevice = CreateDevice(1);
        var secondDevice = CreateDevice(2);
        var definition = CreateScenarioDefinition() with { MaximumRisePerMeasurement = 20 };
        var firstScenario = new HighTemperatureScenarioRuntime(
            definition, firstDevice.Id, new FixedScenarioRandom());
        var secondScenario = new HighTemperatureScenarioRuntime(
            definition, secondDevice.Id, new FixedScenarioRandom());
        var simulationStartedAt = harness.Clock.GetTimestamp();

        _ = harness.Start(firstDevice, CreateSequence(firstDevice.Id, firstScenario), simulationStartedAt);
        await harness.Clock.WaitForDelayAsync();
        Assert.Equal(ScenarioPhase.Pending, firstScenario.Phase);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();
        _ = harness.Start(secondDevice, CreateSequence(secondDevice.Id, secondScenario), simulationStartedAt);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(ScenarioPhase.Active, firstScenario.Phase);
        Assert.Equal(ScenarioPhase.Active, secondScenario.Phase);
        Assert.Equal(32.0, Assert.IsType<double>(firstScenario.CurrentTemperature));
        Assert.Equal(32.0, Assert.IsType<double>(secondScenario.CurrentTemperature));
        var requestsAtSharedStart = harness.Client.Requests.Where(x =>
            x.Measurement.MeasuredAtUtc == (InitialUtc + TelemetryInterval).UtcDateTime).ToArray();
        Assert.Equal(2, requestsAtSharedStart.Length);
        Assert.Equal(new[] { firstDevice.Id, secondDevice.Id }.OrderBy(x => x),
            requestsAtSharedStart.Select(x => x.DeviceId).OrderBy(x => x));
        Assert.All(requestsAtSharedStart, request =>
            Assert.Equal(32.0, Assert.IsType<double>(request.Measurement.NumericValue)));
    }

    /// <summary>
    /// Verifies that retries preserve the already transformed measurement and
    /// do not advance scenario state. Only the next new cycle raises temperature.
    /// </summary>
    /// <param name="recorded">Whether success reports a new insert or an existing measurement.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncRetriesTransformedMeasurementWithoutAdvancingScenario(bool recorded)
    {
        var calls = 0;
        await using var harness = new RunnerHarness((_, measurement, _) =>
            Interlocked.Increment(ref calls) <= 2
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(
                    new HttpRequestException("Temporary telemetry failure."))
                : Task.FromResult(CreateResponse(measurement, recorded)));
        var device = CreateDevice();
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition() with { Duration = TimeSpan.FromMinutes(20) },
            device.Id, new FixedScenarioRandom());
        Assert.Equal(24.0, scenario.GetNextTemperature(24, TimeSpan.Zero));
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        var operation = harness.Start(device, CreateSequence(device.Id, scenario), simulationStartedAt);

        // Two retry delays, followed by the cycle delay after success.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await harness.Clock.WaitForDelayAsync();
            Assert.Equal(attempt, harness.Client.Requests.Count);
            Assert.Equal(26.0, Assert.IsType<double>(scenario.CurrentTemperature));
            Assert.Equal(ScenarioPhase.Active, scenario.Phase);
            if (attempt < 3)
                harness.Clock.Advance(TelemetryInterval);
        }

        var original = harness.Client.Requests[0].Measurement;
        Assert.NotEqual(Guid.Empty, original.MeasurementId);
        Assert.Equal(SimulatorTelemetryMetric.Temperature, original.Metric);
        Assert.Equal(26.0, Assert.IsType<double>(original.NumericValue));
        Assert.Null(original.StateValue);
        Assert.Equal((InitialUtc + TelemetryInterval).UtcDateTime, original.MeasuredAtUtc);
        Assert.All(harness.Client.Requests, request =>
        {
            Assert.Same(original, request.Measurement);
            Assert.Equal(device.Id, request.DeviceId);
            Assert.Equal(harness.Cancellation.Token, request.CancellationToken);
        });

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(4, harness.Client.Requests.Count);
        Assert.False(operation.IsCompleted);
        var next = harness.Client.Requests[3].Measurement;
        Assert.NotEqual(original.MeasurementId, next.MeasurementId);
        Assert.Equal(28.0, Assert.IsType<double>(next.NumericValue));
        Assert.Equal(28.0, Assert.IsType<double>(scenario.CurrentTemperature));
        Assert.Equal((InitialUtc + TimeSpan.FromMinutes(4)).UtcDateTime, next.MeasuredAtUtc);
    }

    /// <summary>
    /// Verifies that elapsed time is evaluated when temperature is processed,
    /// including time spent awaiting a preceding metric from the same batch.
    /// The batch's original measurement timestamp is preserved.
    /// </summary>
    [Fact]
    public async Task RunAsyncRecalculatesElapsedTimeAfterAwaitingPreviousMetric()
    {
        var humidityStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHumidity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (_, measurement, token) =>
        {
            if (measurement.Metric == SimulatorTelemetryMetric.Humidity)
            {
                humidityStarted.TrySetResult();
                await releaseHumidity.Task.WaitAsync(token);
            }
            return CreateResponse(measurement);
        });
        var device = CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Humidity, SimulatorDeviceCapability.Temperature }
        };
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition() with { MaximumRisePerMeasurement = 20 },
            device.Id, new FixedScenarioRandom());
        _ = harness.Start(device, CreateSequence(device.Id, scenario), harness.Clock.GetTimestamp());
        await AwaitAsync(humidityStarted.Task);
        Assert.Single(harness.Client.Requests);
        Assert.Null(scenario.CurrentTemperature);

        harness.Clock.Advance(TelemetryInterval);
        releaseHumidity.SetResult();
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(2, harness.Client.Requests.Count);
        Assert.Equal(ScenarioPhase.Active, scenario.Phase);
        var temperature = harness.Client.Requests[1].Measurement;
        Assert.Equal(SimulatorTelemetryMetric.Temperature, temperature.Metric);
        Assert.Equal(32.0, Assert.IsType<double>(temperature.NumericValue));
        Assert.Equal(InitialUtc.UtcDateTime, temperature.MeasuredAtUtc);
        Assert.Null(temperature.StateValue);
    }

    /// <summary>
    /// Verifies that cancellation before execution prevents requests, delays,
    /// and any mutation of the supplied scenario runtime.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithCanceledTokenDoesNotAdvanceScenario()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice();
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition(), device.Id, new FixedScenarioRandom());
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        await harness.Cancellation.CancelAsync();

        var operation = harness.Start(device, CreateSequence(device.Id, scenario), simulationStartedAt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Equal(ScenarioPhase.Pending, scenario.Phase);
        Assert.Null(scenario.CurrentTemperature);
    }

    /// <summary>
    /// Verifies that an applicable scenario failure propagates before sending
    /// telemetry and is not handled as a transient HTTP failure.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithUnsupportedScenarioPropagatesFailureBeforeSending()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice();
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition() with { AutoRecover = false, RecoveryDuration = null },
            device.Id, new FixedScenarioRandom());

        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        var operation = harness.Start(device, CreateSequence(device.Id, scenario), simulationStartedAt);
        await Assert.ThrowsAsync<NotSupportedException>(() => AwaitAsync(operation));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Empty(harness.Logger.Entries);
        Assert.Null(scenario.CurrentTemperature);
    }

    /// <summary>
    /// Verifies normal completion for a device without capabilities while the
    /// supplied scenario remains unused.
    /// </summary>
    [Fact]
    public async Task RunAsyncWithScenarioAndNoCapabilitiesCompletesWithoutAdvancingScenario()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with { Capabilities = Array.Empty<SimulatorDeviceCapability>() };
        var scenario = new HighTemperatureScenarioRuntime(
            CreateScenarioDefinition(), device.Id, new FixedScenarioRandom());

        await AwaitAsync(harness.Start(device, CreateSequence(device.Id, scenario), harness.Clock.GetTimestamp()));

        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Equal(ScenarioPhase.Pending, scenario.Phase);
        Assert.Null(scenario.CurrentTemperature);
        Assert.Equal(LogLevel.Warning, Assert.Single(harness.Logger.Entries).Level);
    }

    /// <summary>
    /// Verifies sequential execution of overlapping scenarios through the runner,
    /// including a separate final recovery measurement and the full active duration
    /// of the delayed second scenario.
    /// </summary>
    [Fact]
    public async Task RunAsyncExecutesQueuedScenariosAcrossSeparateTelemetryCycles()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice();
        var definition = CreateScenarioDefinition() with
        {
            Duration = TimeSpan.FromMinutes(1),
            RecoveryDuration = TimeSpan.FromMinutes(1),
            MaximumRisePerMeasurement = 30,
            MaximumRecoveryPerMeasurement = 30
        };
        var first = new HighTemperatureScenarioRuntime(definition, device.Id, new FixedScenarioRandom());
        var second = new HighTemperatureScenarioRuntime(definition with
        {
            Name = "SecondScenario",
            AbnormalMinimum = 40,
            AbnormalMaximum = 44
        }, device.Id, new FixedScenarioRandom());
        var sequence = CreateSequence(device.Id, first, second);
        var operation = harness.Start(device, sequence, harness.Clock.GetTimestamp());
        var expectedFirstPhases = new[]
        {
            ScenarioPhase.Pending, ScenarioPhase.Active, ScenarioPhase.Recovering,
            ScenarioPhase.Completed, ScenarioPhase.Completed, ScenarioPhase.Completed,
            ScenarioPhase.Completed, ScenarioPhase.Completed
        };
        var expectedSecondPhases = new[]
        {
            ScenarioPhase.Pending, ScenarioPhase.Pending, ScenarioPhase.Pending,
            ScenarioPhase.Pending, ScenarioPhase.Active, ScenarioPhase.Recovering,
            ScenarioPhase.Completed, ScenarioPhase.Completed
        };

        for (var cycle = 0; cycle < expectedFirstPhases.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();

            Assert.Equal(cycle + 1, harness.Client.Requests.Count);
            Assert.Equal(expectedFirstPhases[cycle], first.Phase);
            Assert.Equal(expectedSecondPhases[cycle], second.Phase);
            var measurement = harness.Client.Requests[cycle].Measurement;
            var temperature = Assert.IsType<double>(measurement.NumericValue);
            Assert.Equal(SimulatorTelemetryMetric.Temperature, measurement.Metric);
            Assert.Null(measurement.StateValue);
            Assert.Equal((InitialUtc + TimeSpan.FromMinutes(cycle)).UtcDateTime,
                measurement.MeasuredAtUtc);
            if (cycle is 1 or 2)
                Assert.Equal(32.0, temperature);
            else if (cycle is 4 or 5)
                Assert.Equal(42.0, temperature);
            else
                Assert.InRange(temperature, 20.0, 24.0);

            if (cycle <= 3)
                Assert.Null(second.CurrentTemperature);
            if (cycle >= 3)
                Assert.Equal(harness.Client.Requests[3].Measurement.NumericValue, first.CurrentTemperature);
            if (cycle >= 6)
                Assert.Equal(harness.Client.Requests[6].Measurement.NumericValue, second.CurrentTemperature);

            if (cycle < expectedFirstPhases.Length - 1)
                harness.Clock.Advance(TelemetryInterval);
        }

        Assert.False(operation.IsCompleted);
        Assert.Equal(8, harness.Client.Requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>
    /// Verifies that retrying a final recovery measurement does not start the
    /// next queued scenario. The successor starts only with a new measurement
    /// after the prior measurement has been acknowledged and the cycle delay ends.
    /// </summary>
    [Fact]
    public async Task RunAsyncRetryOfCompletionMeasurementDoesNotStartNextScenario()
    {
        var calls = 0;
        await using var harness = new RunnerHarness((_, measurement, _) =>
            Interlocked.Increment(ref calls) == 3
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(
                    new HttpRequestException("Final recovery measurement was not acknowledged."))
                : Task.FromResult(CreateResponse(measurement)));
        var device = CreateDevice();
        var definition = CreateScenarioDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            Duration = TimeSpan.FromMinutes(1),
            RecoveryDuration = TimeSpan.FromMinutes(1),
            MaximumRisePerMeasurement = 30,
            MaximumRecoveryPerMeasurement = 30
        };
        var first = new HighTemperatureScenarioRuntime(definition, device.Id, new FixedScenarioRandom());
        var second = new HighTemperatureScenarioRuntime(definition with
        {
            Name = "SecondScenario",
            AbnormalMinimum = 40,
            AbnormalMaximum = 44
        }, device.Id, new FixedScenarioRandom());
        var operation = harness.Start(device, CreateSequence(device.Id, first, second),
            harness.Clock.GetTimestamp());

        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(3, harness.Client.Requests.Count);
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(second.CurrentTemperature);
        var completionMeasurement = harness.Client.Requests[2].Measurement;
        Assert.InRange(Assert.IsType<double>(completionMeasurement.NumericValue), 20.0, 24.0);
        Assert.Equal((InitialUtc + TimeSpan.FromMinutes(2)).UtcDateTime, completionMeasurement.MeasuredAtUtc);

        // A retry sends the same final value even though the queue is now ready for its successor.
        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(4, harness.Client.Requests.Count);
        Assert.Same(completionMeasurement, harness.Client.Requests[3].Measurement);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(second.CurrentTemperature);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(5, harness.Client.Requests.Count);
        Assert.False(operation.IsCompleted);
        var nextMeasurement = harness.Client.Requests[4].Measurement;
        Assert.NotEqual(completionMeasurement.MeasurementId, nextMeasurement.MeasurementId);
        Assert.Equal(42.0, Assert.IsType<double>(nextMeasurement.NumericValue));
        Assert.Equal((InitialUtc + TimeSpan.FromMinutes(4)).UtcDateTime, nextMeasurement.MeasuredAtUtc);
        Assert.Equal(ScenarioPhase.Active, second.Phase);
        Assert.Equal(completionMeasurement.NumericValue, first.CurrentTemperature);
    }

    /// <summary>Creates a device sequence from existing, independently observable runtimes.</summary>
    /// <param name="deviceId">The device that owns the runtimes.</param>
    /// <param name="scenarios">The runtimes to execute in planned start order.</param>
    /// <returns>A sequence used by the production runner overload.</returns>
    private static DeviceTemperatureScenarioSequence CreateSequence(
        Guid deviceId, params HighTemperatureScenarioRuntime[] scenarios) =>
        new(deviceId, scenarios);

    /// <summary>Creates a recoverable scenario with a target of 32 for the fixed random source.</summary>
    /// <returns>A definition with a one-minute start delay and limited temperature steps.</returns>
    private static HighTemperatureScenarioDefinition CreateScenarioDefinition() =>
        new(
            "HighTemperature",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true,
            TimeSpan.FromMinutes(2),
            30,
            34,
            2,
            2);

    /// <summary>Checks the three unaffected metrics in a nighttime telemetry batch.</summary>
    /// <param name="requests">The batch containing humidity, door, and light measurements.</param>
    private static void AssertNormalNonTemperatureMeasurements(IReadOnlyList<TelemetryRequest> requests)
    {
        var humidity = Assert.Single(requests.Where(x =>
            x.Measurement.Metric == SimulatorTelemetryMetric.Humidity)).Measurement;
        Assert.InRange(Assert.IsType<double>(humidity.NumericValue), 78.0, 82.0);
        Assert.Null(humidity.StateValue);
        foreach (var metric in new[] { SimulatorTelemetryMetric.DoorState, SimulatorTelemetryMetric.LightState })
        {
            var state = Assert.Single(requests.Where(x => x.Measurement.Metric == metric)).Measurement;
            Assert.Null(state.NumericValue);
            Assert.False(Assert.IsType<bool>(state.StateValue));
        }
    }

    /// <summary>Supplies a deterministic midpoint for scenario target selection.</summary>
    private sealed class FixedScenarioRandom : Random
    {
        /// <inheritdoc />
        public override double NextDouble() => 0.5;
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

        /// <summary>Starts the scenario overload and tracks its task for cleanup.</summary>
        /// <param name="device">The device passed directly to the runner.</param>
        /// <param name="scenarioSequence">The optional sequence used for temperature measurements.</param>
        /// <param name="simulationStartedAt">The shared timestamp obtained from this harness clock.</param>
        /// <returns>The runner task, including any validation or scenario failure.</returns>
        public Task Start(
            SimulatorDeviceResponse device,
            DeviceTemperatureScenarioSequence? scenarioSequence,
            long simulationStartedAt)
        {
            var operation = Runner.RunAsync(device, scenarioSequence, simulationStartedAt, Cancellation.Token);
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
