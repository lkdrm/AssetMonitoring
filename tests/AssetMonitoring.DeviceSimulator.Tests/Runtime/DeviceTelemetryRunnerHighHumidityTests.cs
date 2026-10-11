using System.Collections.Concurrent;
using System.Threading.Channels;
using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies delivery of humidity scenarios through the production telemetry
/// runner, including independent metrics, phase transitions, and retries.
/// </summary>
/// <remarks>
/// Tests combine real runtimes, sequences, schedules, the normal generator,
/// and the runner with a recording API stub and a manually advanced clock.
/// Normal random measurements are checked by their allowed ranges; exact
/// assertions use fixed targets or an explicitly primed runtime value.
/// Waits are bounded and fake time advances only after delay registration.
/// Existing general runner tests remain separate.
/// </remarks>
public sealed class DeviceTelemetryRunnerHighHumidityTests
{
    /// <summary>Bounds asynchronous coordination and cleanup if a regression occurs.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Defines the fake-time interval between telemetry cycles and retry attempts.</summary>
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Starts fixtures outside warehouse working hours in their configured UTC zone.</summary>
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 10, 11, 22, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies one humidity rise per new batch, despite repeated humidity
    /// capability entries, while other metrics retain normal behavior.
    /// </summary>
    [Fact]
    public async Task RunAsyncAppliesHumidityOncePerMeasurementAndLeavesOtherMetricsNormal()
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
                SimulatorDeviceCapability.Humidity
            }
        };
        var humidity = new HighHumidityScenarioRuntime(
            CreateHumidityDefinition(), device.Id, new MidpointRandom());
        Assert.Equal(82.0, humidity.GetNextHumidity(82, TimeSpan.Zero));
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        var operation = harness.Start(device, CreateSchedule(device.Id, humidity), simulationStartedAt);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal((cycle + 1) * 4, requests.Count);
            var batch = requests.Skip(cycle * 4).Take(4).ToArray();
            AssertBatchMetadata(batch, device.Id, harness.Cancellation.Token,
                InitialUtc + TimeSpan.FromMinutes(cycle + 1));
            var measurement = GetMeasurement(batch, SimulatorTelemetryMetric.Humidity);
            var expectedHumidity = cycle == 0 ? 84.0 : 86.0;
            Assert.Equal(expectedHumidity, Assert.IsType<double>(measurement.NumericValue));
            Assert.Equal(expectedHumidity, Assert.IsType<double>(humidity.CurrentHumidity));
            Assert.Equal(ScenarioPhase.Active, humidity.Phase);
            Assert.Null(measurement.StateValue);
            AssertNormalTemperature(GetMeasurement(batch, SimulatorTelemetryMetric.Temperature));
            AssertNighttimeStates(batch);

            if (cycle == 0)
            {
                harness.Clock.Advance(TelemetryInterval);
            }
        }

        Assert.False(operation.IsCompleted);
        Assert.Equal(8, harness.Client.Requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>
    /// Verifies humidity moves through every phase using simulation time,
    /// and normal delivery continues after the sequence is exhausted.
    /// </summary>
    [Fact]
    public async Task RunAsyncAdvancesHumidityThroughAllPhasesAndThenSendsNormalValues()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice();
        var humidity = new HighHumidityScenarioRuntime(CreateHumidityDefinition() with
        {
            StartsAfter = TimeSpan.FromMinutes(2),
            Duration = TimeSpan.FromMinutes(2),
            RecoveryDuration = TimeSpan.FromMinutes(1),
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        }, device.Id, new MidpointRandom());
        var expectedPhases = new[]
        {
            ScenarioPhase.Pending, ScenarioPhase.Pending,
            ScenarioPhase.Active, ScenarioPhase.Active,
            ScenarioPhase.Recovering, ScenarioPhase.Completed, ScenarioPhase.Completed
        };
        var operation = harness.Start(device, CreateSchedule(device.Id, humidity),
            harness.Clock.GetTimestamp());

        for (var cycle = 0; cycle < expectedPhases.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal(cycle + 1, requests.Count);
            Assert.Equal(expectedPhases[cycle], humidity.Phase);
            var measurement = requests[cycle].Measurement;
            AssertBatchMetadata(new[] { requests[cycle] }, device.Id,
                harness.Cancellation.Token, InitialUtc + TimeSpan.FromMinutes(cycle));
            var value = Assert.IsType<double>(measurement.NumericValue);
            Assert.Equal(SimulatorTelemetryMetric.Humidity, measurement.Metric);
            Assert.Null(measurement.StateValue);
            Assert.InRange(value, 0.0, 100.0);

            if (cycle is >= 2 and <= 4)
            {
                Assert.Equal(92.0, value);
            }
            else
            {
                AssertNormalHumidity(measurement);
            }

            if (cycle < 2)
            {
                // The metric sequence returns pending measurements without invoking the runtime.
                Assert.Null(humidity.CurrentHumidity);
            }
            else if (cycle <= 5)
            {
                Assert.Equal(value, Assert.IsType<double>(humidity.CurrentHumidity));
            }
            else
            {
                // An exhausted sequence no longer invokes the completed runtime.
                Assert.Equal(requests[5].Measurement.NumericValue, humidity.CurrentHumidity);
            }

            if (cycle < expectedPhases.Length - 1)
            {
                harness.Clock.Advance(TelemetryInterval);
            }
        }

        Assert.False(operation.IsCompleted);
        Assert.Equal(7, harness.Client.Requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>
    /// Verifies both numeric metrics execute and recover independently in one
    /// device loop regardless of the device capability order.
    /// </summary>
    /// <param name="humidityFirst">Whether humidity is generated before temperature.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncExecutesBothMetricsIndependentlyOnOneDevice(bool humidityFirst)
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with
        {
            Capabilities = humidityFirst
                ? new[] { SimulatorDeviceCapability.Humidity, SimulatorDeviceCapability.Temperature,
                    SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.LightState }
                : new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity,
                    SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.LightState }
        };
        var temperature = new HighTemperatureScenarioRuntime(CreateTemperatureDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            Duration = TelemetryInterval,
            RecoveryDuration = TelemetryInterval,
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        }, device.Id, new MidpointRandom());
        var humidity = new HighHumidityScenarioRuntime(CreateHumidityDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            Duration = TelemetryInterval,
            RecoveryDuration = TelemetryInterval,
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        }, device.Id, new MidpointRandom());
        var expectedPhases = new[]
        {
            ScenarioPhase.Active, ScenarioPhase.Recovering,
            ScenarioPhase.Completed, ScenarioPhase.Completed
        };
        var operation = harness.Start(device, CreateSchedule(device.Id, humidity, temperature),
            harness.Clock.GetTimestamp());

        for (var cycle = 0; cycle < expectedPhases.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal((cycle + 1) * 4, requests.Count);
            var batch = requests.Skip(cycle * 4).Take(4).ToArray();
            AssertBatchMetadata(batch, device.Id, harness.Cancellation.Token,
                InitialUtc + TimeSpan.FromMinutes(cycle));
            Assert.Equal(expectedPhases[cycle], temperature.Phase);
            Assert.Equal(expectedPhases[cycle], humidity.Phase);
            var temperatureMeasurement = GetMeasurement(batch, SimulatorTelemetryMetric.Temperature);
            var humidityMeasurement = GetMeasurement(batch, SimulatorTelemetryMetric.Humidity);
            AssertNighttimeStates(batch);

            if (cycle < 2)
            {
                Assert.Equal(32.0, Assert.IsType<double>(temperatureMeasurement.NumericValue));
                Assert.Equal(92.0, Assert.IsType<double>(humidityMeasurement.NumericValue));
            }
            else
            {
                AssertNormalTemperature(temperatureMeasurement);
                AssertNormalHumidity(humidityMeasurement);
            }

            if (cycle <= 2)
            {
                Assert.Equal(temperatureMeasurement.NumericValue, temperature.CurrentTemperature);
                Assert.Equal(humidityMeasurement.NumericValue, humidity.CurrentHumidity);
            }
            else
            {
                var completionBatch = requests.Skip(8).Take(4).ToArray();
                Assert.Equal(GetMeasurement(completionBatch, SimulatorTelemetryMetric.Temperature).NumericValue,
                    temperature.CurrentTemperature);
                Assert.Equal(GetMeasurement(completionBatch, SimulatorTelemetryMetric.Humidity).NumericValue,
                    humidity.CurrentHumidity);
            }

            if (cycle < expectedPhases.Length - 1)
            {
                harness.Clock.Advance(TelemetryInterval);
            }
        }

        Assert.False(operation.IsCompleted);
        Assert.Equal(16, harness.Client.Requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>Verifies factory-prepared mixed-metric schedules reach the API through the runner.</summary>
    [Fact]
    public async Task RunAsyncSendsBothMetricsFromFactoryPreparedSchedule()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity }
        };
        var temperatureDefinition = CreateTemperatureDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            MaximumRisePerMeasurement = 100
        };
        var humidityDefinition = CreateHumidityDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            MaximumRisePerMeasurement = 100
        };
        var schedules = new DeviceScenarioScheduleFactory().Create(new[]
        {
            new ResolvedScenario(humidityDefinition, new[] { device }),
            new ResolvedScenario(temperatureDefinition, new[] { device })
        }, new MidpointRandom());

        var operation = harness.Start(device, schedules[device.Id], harness.Clock.GetTimestamp());
        await harness.Clock.WaitForDelayAsync();

        var requests = harness.Client.Requests;
        Assert.Equal(2, requests.Count);
        AssertBatchMetadata(requests, device.Id, harness.Cancellation.Token, InitialUtc);
        Assert.Equal(32.0, Assert.IsType<double>(GetMeasurement(requests,
            SimulatorTelemetryMetric.Temperature).NumericValue));
        Assert.Equal(92.0, Assert.IsType<double>(GetMeasurement(requests,
            SimulatorTelemetryMetric.Humidity).NumericValue));
        Assert.False(operation.IsCompleted);
    }

    /// <summary>
    /// Verifies a humidity schedule is not invoked when no humidity measurement
    /// is generated, including a device with no telemetry capabilities.
    /// </summary>
    /// <param name="noCapabilities">Whether the device produces an empty batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncWithoutHumidityCapabilityLeavesHumidityRuntimeUntouched(bool noCapabilities)
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with
        {
            Capabilities = noCapabilities
                ? Array.Empty<SimulatorDeviceCapability>()
                : new[] { SimulatorDeviceCapability.Temperature,
                    SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.LightState }
        };
        var humidity = new HighHumidityScenarioRuntime(CreateHumidityDefinition() with
        {
            AutoRecover = false,
            RecoveryDuration = null
        }, device.Id, new MidpointRandom());
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        var operation = harness.Start(device, CreateSchedule(device.Id, humidity), simulationStartedAt);

        if (noCapabilities)
        {
            await AwaitAsync(operation);
            Assert.Empty(harness.Client.Requests);
            Assert.Equal(0, harness.Clock.TimerCount);
        }
        else
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal(3, requests.Count);
            AssertBatchMetadata(requests, device.Id, harness.Cancellation.Token,
                InitialUtc + TelemetryInterval);
            AssertNormalTemperature(GetMeasurement(requests, SimulatorTelemetryMetric.Temperature));
            AssertNighttimeStates(requests);
            Assert.DoesNotContain(requests, request =>
                request.Measurement.Metric == SimulatorTelemetryMetric.Humidity);
            Assert.False(operation.IsCompleted);
        }

        Assert.Equal(ScenarioPhase.Pending, humidity.Phase);
        Assert.Null(humidity.CurrentHumidity);
    }

    /// <summary>Verifies a later device loop uses the same simulation origin for humidity activation.</summary>
    [Fact]
    public async Task RunAsyncUsesSharedSimulationTimestampForHumidityOnLaterStartedDevice()
    {
        await using var harness = new RunnerHarness();
        var firstDevice = CreateDevice(1);
        var secondDevice = CreateDevice(2);
        var definition = CreateHumidityDefinition() with { MaximumRisePerMeasurement = 100 };
        var first = new HighHumidityScenarioRuntime(definition, firstDevice.Id, new MidpointRandom());
        var second = new HighHumidityScenarioRuntime(definition, secondDevice.Id, new MidpointRandom());
        var simulationStartedAt = harness.Clock.GetTimestamp();

        _ = harness.Start(firstDevice, CreateSchedule(firstDevice.Id, first), simulationStartedAt);
        await harness.Clock.WaitForDelayAsync();
        Assert.Equal(ScenarioPhase.Pending, first.Phase);
        Assert.Null(first.CurrentHumidity);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();
        _ = harness.Start(secondDevice, CreateSchedule(secondDevice.Id, second), simulationStartedAt);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(ScenarioPhase.Active, first.Phase);
        Assert.Equal(ScenarioPhase.Active, second.Phase);
        Assert.Equal(92.0, Assert.IsType<double>(first.CurrentHumidity));
        Assert.Equal(92.0, Assert.IsType<double>(second.CurrentHumidity));
        var activeRequests = harness.Client.Requests.Where(request =>
            request.Measurement.MeasuredAtUtc == (InitialUtc + TelemetryInterval).UtcDateTime).ToArray();
        Assert.Equal(2, activeRequests.Length);
        Assert.Equal(new[] { firstDevice.Id, secondDevice.Id }.OrderBy(id => id),
            activeRequests.Select(request => request.DeviceId).OrderBy(id => id));
        Assert.All(activeRequests, request =>
            Assert.Equal(92.0, Assert.IsType<double>(request.Measurement.NumericValue)));
    }

    /// <summary>
    /// Verifies humidity sees time spent awaiting a previous metric while its
    /// generated measurement retains the original batch timestamp.
    /// </summary>
    [Fact]
    public async Task RunAsyncRecalculatesHumidityElapsedTimeAfterAwaitingTemperature()
    {
        var temperatureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTemperature = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (_, measurement, token) =>
        {
            if (measurement.Metric == SimulatorTelemetryMetric.Temperature)
            {
                temperatureStarted.TrySetResult();
                await releaseTemperature.Task.WaitAsync(token);
            }

            return CreateResponse(measurement);
        });
        var device = CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity }
        };
        var humidity = new HighHumidityScenarioRuntime(
            CreateHumidityDefinition() with { MaximumRisePerMeasurement = 100 },
            device.Id, new MidpointRandom());
        _ = harness.Start(device, CreateSchedule(device.Id, humidity), harness.Clock.GetTimestamp());
        await AwaitAsync(temperatureStarted.Task);
        Assert.Single(harness.Client.Requests);
        Assert.Null(humidity.CurrentHumidity);

        harness.Clock.Advance(TelemetryInterval);
        releaseTemperature.SetResult();
        await harness.Clock.WaitForDelayAsync();

        var requests = harness.Client.Requests;
        Assert.Equal(2, requests.Count);
        AssertBatchMetadata(requests, device.Id, harness.Cancellation.Token, InitialUtc);
        AssertNormalTemperature(GetMeasurement(requests, SimulatorTelemetryMetric.Temperature));
        var humidityMeasurement = GetMeasurement(requests, SimulatorTelemetryMetric.Humidity);
        Assert.Equal(92.0, Assert.IsType<double>(humidityMeasurement.NumericValue));
        Assert.Equal(ScenarioPhase.Active, humidity.Phase);
        Assert.Equal(92.0, Assert.IsType<double>(humidity.CurrentHumidity));
    }

    /// <summary>
    /// Verifies retries reuse one transformed humidity measurement, and the
    /// next new cycle performs exactly one further humidity rise.
    /// </summary>
    /// <param name="recorded">Whether success records a new row or acknowledges an existing measurement.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsyncRetriesHumidityWithoutAdvancingItsRuntime(bool recorded)
    {
        var calls = 0;
        await using var harness = new RunnerHarness((_, measurement, _) =>
            Interlocked.Increment(ref calls) <= 2
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(
                    new HttpRequestException("Temporary humidity delivery failure."))
                : Task.FromResult(CreateResponse(measurement, recorded)));
        var device = CreateDevice();
        var humidity = new HighHumidityScenarioRuntime(
            CreateHumidityDefinition() with { Duration = TimeSpan.FromMinutes(20) },
            device.Id, new MidpointRandom());
        Assert.Equal(82.0, humidity.GetNextHumidity(82, TimeSpan.Zero));
        var simulationStartedAt = harness.Clock.GetTimestamp();
        harness.Clock.Advance(TelemetryInterval);
        var operation = harness.Start(device, CreateSchedule(device.Id, humidity), simulationStartedAt);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await harness.Clock.WaitForDelayAsync();
            Assert.Equal(attempt, harness.Client.Requests.Count);
            Assert.Equal(84.0, Assert.IsType<double>(humidity.CurrentHumidity));
            Assert.Equal(ScenarioPhase.Active, humidity.Phase);

            if (attempt < 3)
            {
                harness.Clock.Advance(TelemetryInterval);
            }
        }

        var original = harness.Client.Requests[0].Measurement;
        Assert.NotEqual(Guid.Empty, original.MeasurementId);
        Assert.Equal(SimulatorTelemetryMetric.Humidity, original.Metric);
        Assert.Equal(84.0, Assert.IsType<double>(original.NumericValue));
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
        var next = harness.Client.Requests[3].Measurement;
        Assert.NotEqual(original.MeasurementId, next.MeasurementId);
        Assert.Equal(86.0, Assert.IsType<double>(next.NumericValue));
        Assert.Equal(86.0, Assert.IsType<double>(humidity.CurrentHumidity));
        Assert.Equal((InitialUtc + TimeSpan.FromMinutes(4)).UtcDateTime, next.MeasuredAtUtc);
        Assert.False(operation.IsCompleted);
    }

    /// <summary>
    /// Verifies retrying the final recovery measurement cannot start the next
    /// queued humidity scenario before a new measurement is generated.
    /// </summary>
    [Fact]
    public async Task RunAsyncRetryOfHumidityCompletionDoesNotStartNextScenario()
    {
        var calls = 0;
        await using var harness = new RunnerHarness((_, measurement, _) =>
            Interlocked.Increment(ref calls) == 3
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(
                    new HttpRequestException("Humidity completion was not acknowledged."))
                : Task.FromResult(CreateResponse(measurement)));
        var device = CreateDevice();
        var definition = CreateHumidityDefinition() with
        {
            StartsAfter = TimeSpan.Zero,
            Duration = TelemetryInterval,
            RecoveryDuration = TelemetryInterval,
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        };
        var first = new HighHumidityScenarioRuntime(definition, device.Id, new MidpointRandom());
        var second = new HighHumidityScenarioRuntime(definition with
        {
            Name = "SecondHumidity",
            AbnormalMinimum = 94,
            AbnormalMaximum = 98
        }, device.Id, new MidpointRandom());
        var operation = harness.Start(device, CreateSchedule(device.Id, first, second),
            harness.Clock.GetTimestamp());

        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();
        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(3, harness.Client.Requests.Count);
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(second.CurrentHumidity);
        var completion = harness.Client.Requests[2].Measurement;
        AssertNormalHumidity(completion);
        Assert.Equal((InitialUtc + TimeSpan.FromMinutes(2)).UtcDateTime, completion.MeasuredAtUtc);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(4, harness.Client.Requests.Count);
        Assert.Same(completion, harness.Client.Requests[3].Measurement);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(second.CurrentHumidity);
        Assert.Equal(completion.NumericValue, first.CurrentHumidity);

        harness.Clock.Advance(TelemetryInterval);
        await harness.Clock.WaitForDelayAsync();

        Assert.Equal(5, harness.Client.Requests.Count);
        var next = harness.Client.Requests[4].Measurement;
        Assert.NotEqual(completion.MeasurementId, next.MeasurementId);
        Assert.Equal(96.0, Assert.IsType<double>(next.NumericValue));
        Assert.Equal((InitialUtc + TimeSpan.FromMinutes(4)).UtcDateTime, next.MeasuredAtUtc);
        Assert.Equal(ScenarioPhase.Active, second.Phase);
        Assert.Equal(completion.NumericValue, first.CurrentHumidity);
        Assert.False(operation.IsCompleted);
    }

    /// <summary>Verifies canceled execution does not create telemetry or advance a humidity scenario.</summary>
    [Fact]
    public async Task RunAsyncWithCanceledTokenDoesNotAdvanceHumidity()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice();
        var humidity = new HighHumidityScenarioRuntime(
            CreateHumidityDefinition(), device.Id, new MidpointRandom());
        await harness.Cancellation.CancelAsync();

        var operation = harness.Start(device, CreateSchedule(device.Id, humidity),
            harness.Clock.GetTimestamp());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(operation));
        Assert.Empty(harness.Client.Requests);
        Assert.Equal(0, harness.Clock.TimerCount);
        Assert.Equal(ScenarioPhase.Pending, humidity.Phase);
        Assert.Null(humidity.CurrentHumidity);
    }

    /// <summary>
    /// Verifies queued humidity scenarios complete across separate cycles
    /// while the same device's temperature scenario remains active.
    /// </summary>
    [Fact]
    public async Task RunAsyncExecutesQueuedHumidityWithoutInterruptingTemperature()
    {
        await using var harness = new RunnerHarness();
        var device = CreateDevice() with
        {
            Capabilities = new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity }
        };
        var definition = CreateHumidityDefinition() with
        {
            Duration = TelemetryInterval,
            RecoveryDuration = TelemetryInterval,
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        };
        var first = new HighHumidityScenarioRuntime(definition, device.Id, new MidpointRandom());
        var second = new HighHumidityScenarioRuntime(definition with
        {
            Name = "SecondHumidity",
            AbnormalMinimum = 94,
            AbnormalMaximum = 98
        }, device.Id, new MidpointRandom());
        var temperature = new HighTemperatureScenarioRuntime(CreateTemperatureDefinition() with
        {
            Duration = TimeSpan.FromMinutes(20),
            MaximumRisePerMeasurement = 100
        }, device.Id, new MidpointRandom());
        var firstPhases = new[]
        {
            ScenarioPhase.Pending, ScenarioPhase.Active, ScenarioPhase.Recovering,
            ScenarioPhase.Completed, ScenarioPhase.Completed, ScenarioPhase.Completed,
            ScenarioPhase.Completed, ScenarioPhase.Completed
        };
        var secondPhases = new[]
        {
            ScenarioPhase.Pending, ScenarioPhase.Pending, ScenarioPhase.Pending,
            ScenarioPhase.Pending, ScenarioPhase.Active, ScenarioPhase.Recovering,
            ScenarioPhase.Completed, ScenarioPhase.Completed
        };
        var operation = harness.Start(device, CreateSchedule(device.Id, first, temperature, second),
            harness.Clock.GetTimestamp());

        for (var cycle = 0; cycle < firstPhases.Length; cycle++)
        {
            await harness.Clock.WaitForDelayAsync();
            var requests = harness.Client.Requests;
            Assert.Equal((cycle + 1) * 2, requests.Count);
            var batch = requests.Skip(cycle * 2).Take(2).ToArray();
            AssertBatchMetadata(batch, device.Id, harness.Cancellation.Token,
                InitialUtc + TimeSpan.FromMinutes(cycle));
            Assert.Equal(firstPhases[cycle], first.Phase);
            Assert.Equal(secondPhases[cycle], second.Phase);
            var humidityMeasurement = GetMeasurement(batch, SimulatorTelemetryMetric.Humidity);
            var temperatureMeasurement = GetMeasurement(batch, SimulatorTelemetryMetric.Temperature);

            if (cycle is 1 or 2)
            {
                Assert.Equal(92.0, Assert.IsType<double>(humidityMeasurement.NumericValue));
            }
            else if (cycle is 4 or 5)
            {
                Assert.Equal(96.0, Assert.IsType<double>(humidityMeasurement.NumericValue));
            }
            else
            {
                AssertNormalHumidity(humidityMeasurement);
            }

            if (cycle == 0)
            {
                Assert.Equal(ScenarioPhase.Pending, temperature.Phase);
                AssertNormalTemperature(temperatureMeasurement);
            }
            else
            {
                Assert.Equal(ScenarioPhase.Active, temperature.Phase);
                Assert.Equal(32.0, Assert.IsType<double>(temperatureMeasurement.NumericValue));
                Assert.Equal(32.0, Assert.IsType<double>(temperature.CurrentTemperature));
            }

            if (cycle <= 3)
            {
                Assert.Null(second.CurrentHumidity);
            }

            if (cycle >= 3)
            {
                var completedFirst = GetMeasurement(requests.Skip(6).Take(2).ToArray(),
                    SimulatorTelemetryMetric.Humidity);
                Assert.Equal(completedFirst.NumericValue, first.CurrentHumidity);
            }

            if (cycle >= 6)
            {
                var completedSecond = GetMeasurement(requests.Skip(12).Take(2).ToArray(),
                    SimulatorTelemetryMetric.Humidity);
                Assert.Equal(completedSecond.NumericValue, second.CurrentHumidity);
            }

            if (cycle < firstPhases.Length - 1)
            {
                harness.Clock.Advance(TelemetryInterval);
            }
        }

        Assert.False(operation.IsCompleted);
        Assert.Equal(16, harness.Client.Requests.Select(x => x.Measurement.MeasurementId).Distinct().Count());
    }

    /// <summary>Creates a humidity scenario with a midpoint target of 92 and two-point limits.</summary>
    /// <returns>A recoverable scenario starting after one minute.</returns>
    private static HighHumidityScenarioDefinition CreateHumidityDefinition() =>
        new("HighHumidity", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true,
            TimeSpan.FromMinutes(2), 90, 94, 2, 2);

    /// <summary>Creates a temperature scenario with a midpoint target of 32 and two-degree limits.</summary>
    /// <returns>A recoverable scenario starting after one minute.</returns>
    private static HighTemperatureScenarioDefinition CreateTemperatureDefinition() =>
        new("HighTemperature", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true,
            TimeSpan.FromMinutes(2), 30, 34, 2, 2);

    /// <summary>Creates an active humidity device with a deterministic identifier.</summary>
    /// <param name="number">The suffix used to distinguish fixture devices.</param>
    /// <returns>An active device whose default capability is humidity.</returns>
    private static SimulatorDeviceResponse CreateDevice(int number = 1) =>
        new(Guid.Parse($"50000000-0000-0000-0000-{number:D12}"),
            $"WH-{number:D3}", $"Warehouse sensor {number}",
            new[] { SimulatorDeviceCapability.Humidity }, SimulatorDeviceLifecycle.Active);

    /// <summary>Creates metric sequences that retain the supplied observable runtime instances.</summary>
    /// <param name="deviceId">The device owning every runtime and the resulting schedule.</param>
    /// <param name="scenarios">The runtimes grouped by metric in their supplied order.</param>
    /// <returns>A schedule suitable for the production runner overload.</returns>
    private static DeviceScenarioSchedule CreateSchedule(
        Guid deviceId, params IMeasurementScenarioRuntime[] scenarios) =>
        new(deviceId, scenarios.GroupBy(scenario => scenario.Metric)
            .Select(group => new DeviceMetricScenarioSequence(deviceId, group.Key, group.ToArray())));

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

    /// <summary>Checks unaffected door and light measurements outside working hours.</summary>
    /// <param name="requests">The batch containing both state metrics.</param>
    private static void AssertNighttimeStates(IReadOnlyList<TelemetryRequest> requests)
    {
        foreach (var metric in new[] { SimulatorTelemetryMetric.DoorState, SimulatorTelemetryMetric.LightState })
        {
            var measurement = GetMeasurement(requests, metric);
            Assert.Null(measurement.NumericValue);
            Assert.False(Assert.IsType<bool>(measurement.StateValue));
        }
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

    /// <summary>Provides a fixed midpoint for abnormal target selection.</summary>
    private sealed class MidpointRandom : Random
    {
        /// <inheritdoc />
        public override double NextDouble() => 0.5;
    }

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
