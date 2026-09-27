using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Load;
using AssetMonitoring.DeviceSimulator.Preparation;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using AssetMonitoring.DeviceSimulator.Workers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace AssetMonitoring.DeviceSimulator.Tests.Workers;

/// <summary>
/// Verifies simulation plan loading, catalog synchronization, device retrieval,
/// device preparation, scenario resolution, sequence preparation, shared scenario
/// time, concurrent heartbeat and telemetry execution, shutdown, and cancellation.
/// Uses production services with a fake API and controlled clocks.
/// </summary>
public sealed class SimulationWorkerTests
{
    /// <summary>
    /// Bounds background execution and asynchronous coordination in tests.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that a valid plan is read once using the configured name,
    /// receives a cancellable token, and is prepared before catalog synchronization.
    /// Devices are retrieved after synchronization and prepared before readiness
    /// is reported with updated lifecycle values and structured device details.
    /// Both runtime groups start only after sequence preparation and keep the
    /// worker running, including when the plan contains no scenarios.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithValidPlanSynchronizesBeforeRetrievingDevices()
    {
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var planWasPreparedBeforeSynchronization = false;
        var catalogWasSynchronizedBeforeRetrieval = false;
        var devicesWereRetrievedBeforeActivation = false;
        var preparationWasCompletedBeforeHeartbeat = new ConcurrentQueue<bool>();
        var sequencesWerePreparedBeforeRuntime = new ConcurrentQueue<bool>();
        var devices = CreateAvailableDevices();
        var apiClient = new StubDeviceSimulatorApiClient(_ =>
        {
            planWasPreparedBeforeSynchronization = reader.CallCount == 1
                && logger.Entries.Any(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Properties.TryGetValue("PlanName", out var name)
                    && Equals(name, "selected-plan"));
            return Task.FromResult(CreateSuccessfulSynchronizationResponse());
        }, _ =>
        {
            catalogWasSynchronizedBeforeRetrieval = logger.Entries.Any(entry =>
                entry.Level == LogLevel.Information
                && entry.Properties.ContainsKey("Created"));
            return Task.FromResult(devices);
        }, (deviceId, _) =>
        {
            devicesWereRetrievedBeforeActivation = logger.Entries.Any(entry =>
                entry.Level == LogLevel.Information
                && entry.Message == "Retrieved 2 devices from the API.");
            return Task.FromResult(CreateSuccessfulActivationResponse(deviceId));
        }, (deviceId, token) =>
        {
            preparationWasCompletedBeforeHeartbeat.Enqueue(
                logger.Entries.Count(entry => entry.Properties.ContainsKey("Lifecycle")) == devices.Count);
            sequencesWerePreparedBeforeRuntime.Enqueue(logger.Entries.Any(entry =>
                entry.Properties.ContainsKey("DeviceCount")
                && entry.Properties.TryGetValue("ScenarioCount", out var count) && Equals(count, 0)));
            return WaitForHeartbeatCancellationAsync(deviceId, token);
        }, (_, measurement, token) =>
        {
            sequencesWerePreparedBeforeRuntime.Enqueue(logger.Entries.Any(entry =>
                entry.Properties.ContainsKey("DeviceCount")
                && entry.Properties.TryGetValue("ScenarioCount", out var count) && Equals(count, 0)));
            return WaitForTelemetryCancellationAsync(measurement, token);
        });
        using var worker = CreateWorker(reader, logger, lifetime, apiClient, "selected-plan");

        await RunUntilRuntimeLoopsAndStopAsync(worker, apiClient, devices.Count);

        Assert.Equal(1, reader.CallCount);
        Assert.Equal("selected-plan", reader.LastPlanName);
        Assert.True(reader.LastCancellationToken.CanBeCanceled);
        Assert.True(planWasPreparedBeforeSynchronization);
        Assert.Equal(1, apiClient.CallCount);
        Assert.True(apiClient.LastCancellationToken.CanBeCanceled);
        Assert.True(catalogWasSynchronizedBeforeRetrieval);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.True(apiClient.LastGetDevicesCancellationToken.CanBeCanceled);
        Assert.True(devicesWereRetrievedBeforeActivation);
        var activation = Assert.Single(apiClient.ActivationRequests);
        Assert.Equal(devices[0].Id, activation.DeviceId);
        Assert.Equal(apiClient.LastGetDevicesCancellationToken, activation.CancellationToken);
        Assert.Equal(devices.Count, preparationWasCompletedBeforeHeartbeat.Count);
        Assert.All(preparationWasCompletedBeforeHeartbeat, completed => Assert.True(completed));
        Assert.Equal(devices.Count * 2, sequencesWerePreparedBeforeRuntime.Count);
        Assert.All(sequencesWerePreparedBeforeRuntime, completed => Assert.True(completed));
        AssertHeartbeatDevices(apiClient, devices);
        AssertTelemetryDevices(apiClient, devices);
        Assert.All(apiClient.HeartbeatRequests, request =>
        {
            Assert.Equal(reader.LastCancellationToken, request.CancellationToken);
            Assert.True(request.CancellationToken.IsCancellationRequested);
        });
        Assert.All(apiClient.TelemetryRequests, request =>
        {
            Assert.Equal(reader.LastCancellationToken, request.CancellationToken);
            Assert.True(request.CancellationToken.IsCancellationRequested);
        });
        Assert.Equal(0, lifetime.StopApplicationCallCount);

        Assert.Equal(8, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
        });
        Assert.Equal("selected-plan", logger.Entries[0].Properties["PlanName"]);
        var synchronization = logger.Entries[1];
        Assert.Equal(1, synchronization.Properties["Created"]);
        Assert.Equal(2, synchronization.Properties["Updated"]);
        Assert.Equal(3, synchronization.Properties["Unchanged"]);
        Assert.Equal(4, synchronization.Properties["Retired"]);
        Assert.Equal(5, synchronization.Properties["Restored"]);
        Assert.Equal(2, logger.Entries[2].Properties["DeviceCount"]);
        AssertPreparedDevicesAreLogged(logger, devices);
        AssertResolvedPlanIsLogged(logger, CreateValidPlan());
        AssertSequencesPreparedIsLogged(logger, 0, 0);
    }

    /// <summary>
    /// Verifies that every validation error is logged before shutdown is
    /// requested and that an invalid plan never reaches the API.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithInvalidPlanLogsAllErrorsBeforeRequestingShutdown()
    {
        var plan = CreateValidPlan() with { Name = " ", Scenarios = null! };
        var reader = new StubSimulationPlanReader((_, _) => Task.FromResult(plan));
        var logger = new RecordingLogger();
        var errorCountWhenShutdownWasRequested = -1;
        var lifetime = new RecordingApplicationLifetime(() =>
            errorCountWhenShutdownWasRequested = logger.Entries.Count(
                entry => entry.Level == LogLevel.Error));
        var apiClient = new StubDeviceSimulatorApiClient();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, reader.CallCount);
        Assert.Equal(0, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(2, errorCountWhenShutdownWasRequested);
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Null(entry.Exception);
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsType<string>(entry.Properties["Message"])));
        });
        Assert.Contains(logger.Entries, entry =>
            Equals(entry.Properties["Code"], "Plan.Name.Required") &&
            Equals(entry.Properties["Path"], "name"));
        Assert.Contains(logger.Entries, entry =>
            Equals(entry.Properties["Code"], "Plan.Scenarios.Required") &&
            Equals(entry.Properties["Path"], "scenarios"));
    }

    /// <summary>
    /// Verifies that expected reader failures retain their exception details
    /// in the error log, request shutdown, and prevent API calls.
    /// </summary>
    /// <param name="failureKind">The kind of reader failure to simulate.</param>
    [Theory]
    [InlineData("MissingFile")]
    [InlineData("InputOutput")]
    [InlineData("AccessDenied")]
    [InlineData("MalformedJson")]
    public async Task ExecuteAsyncWithExpectedReaderFailureLogsExceptionAndRequestsShutdown(
        string failureKind)
    {
        Exception expected = failureKind switch
        {
            "MissingFile" => new FileNotFoundException("Plan file was not found."),
            "InputOutput" => new IOException("Plan file could not be read."),
            "AccessDenied" => new UnauthorizedAccessException("Plan access was denied."),
            "MalformedJson" => new JsonException("Plan JSON is malformed."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var reader = new StubSimulationPlanReader((_, _) =>
            Task.FromException<SimulationPlanDefinition>(expected));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var apiClient = new StubDeviceSimulatorApiClient();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient, "unreadable-plan");

        await RunWorkerAsync(worker);

        Assert.Equal(1, reader.CallCount);
        Assert.Equal(0, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(expected, entry.Exception);
        Assert.Equal("unreadable-plan", entry.Properties["PlanName"]);
    }

    /// <summary>
    /// Verifies that stopping the worker cancels an in-flight read, logs
    /// normal cancellation, and neither calls the API nor requests shutdown again.
    /// </summary>
    [Fact]
    public async Task StopAsyncDuringPlanLoadingCancelsReaderWithoutLoggingAnError()
    {
        var readStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingPlan = new TaskCompletionSource<SimulationPlanDefinition>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubSimulationPlanReader(async (_, token) =>
        {
            readStarted.TrySetResult(true);
            return await pendingPlan.Task.WaitAsync(token);
        });
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var apiClient = new StubDeviceSimulatorApiClient();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await readStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            await AwaitExecutionAsync(worker);

            Assert.Equal(1, reader.CallCount);
            Assert.True(reader.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(0, apiClient.CallCount);
            Assert.Equal(0, apiClient.GetDevicesCallCount);
            Assert.Empty(apiClient.ActivationRequests);
            AssertNoRuntimeRequests(apiClient);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Contains("canceled", entry.Message);
            Assert.Null(entry.Exception);
        }
        finally
        {
            pendingPlan.TrySetResult(CreateValidPlan());
        }
    }

    /// <summary>
    /// Verifies that reader cancellation is not treated as normal host shutdown
    /// when the worker stopping token has not been canceled.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnrequestedCancellationDoesNotSwallowException()
    {
        var reader = new StubSimulationPlanReader((_, _) =>
            Task.FromException<SimulationPlanDefinition>(
                new OperationCanceledException("Reader canceled independently.")));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var apiClient = new StubDeviceSimulatorApiClient();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunWorkerAsync(worker));

        Assert.Equal(1, reader.CallCount);
        Assert.False(reader.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(0, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Verifies that an unexpected programming failure remains observable
    /// through the worker execution task rather than being swallowed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnexpectedFailurePropagatesException()
    {
        var expected = new InvalidOperationException("Unexpected reader failure.");
        var reader = new StubSimulationPlanReader((_, _) =>
            Task.FromException<SimulationPlanDefinition>(expected));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var apiClient = new StubDeviceSimulatorApiClient();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(worker));

        Assert.Same(expected, actual);
        Assert.Equal(0, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Verifies that an applied synchronization with no changes is still
    /// reported as successful and does not request application shutdown.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnchangedCatalogLogsSuccessWithoutRequestingShutdown()
    {
        var response = CreateSuccessfulSynchronizationResponse() with
        {
            Created = 0,
            Updated = 0,
            Unchanged = 10,
            Retired = 0,
            Restored = 0,
            HasChanges = false
        };
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(_ => Task.FromResult(response));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunUntilRuntimeLoopsAndStopAsync(worker, apiClient, 2);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Equal(8, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        var synchronization = logger.Entries[1];
        Assert.Equal(0, synchronization.Properties["Created"]);
        Assert.Equal(0, synchronization.Properties["Updated"]);
        Assert.Equal(10, synchronization.Properties["Unchanged"]);
        Assert.Equal(0, synchronization.Properties["Retired"]);
        Assert.Equal(0, synchronization.Properties["Restored"]);
        Assert.Equal(2, logger.Entries[2].Properties["DeviceCount"]);
        Assert.Single(apiClient.ActivationRequests);
        AssertPreparedDevicesAreLogged(logger, CreateAvailableDevices());
        AssertHeartbeatDevices(apiClient, CreateAvailableDevices());
        AssertTelemetryDevices(apiClient, CreateAvailableDevices());
    }

    /// <summary>
    /// Verifies that catalog rejection logs every structured validation error,
    /// including nullable details, before requesting shutdown once.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithRejectedCatalogLogsAllErrorsBeforeRequestingShutdown()
    {
        var response = new DeviceCatalogSynchronizationResponse(
            new DeviceCatalogValidationResponse(false,
                new DeviceCatalogValidationErrorResponse[]
                {
                    new("Device.Name.Required", "Device name is required.", "WH-001", "Name"),
                    new("Catalog.Invalid", "Catalog configuration is invalid.", null, null)
                }),
            0, 0, 0, 0, 0, false, false);
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(_ => Task.FromResult(response));
        var logger = new RecordingLogger();
        var errorCountWhenShutdownWasRequested = -1;
        var lifetime = new RecordingApplicationLifetime(() =>
            errorCountWhenShutdownWasRequested = logger.Entries.Count(
                entry => entry.Level == LogLevel.Error));
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(2, errorCountWhenShutdownWasRequested);
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.Equal("normal-operation", logger.Entries[0].Properties["PlanName"]);

        var errors = logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.All(errors, entry => Assert.Null(entry.Exception));
        Assert.Contains(errors, entry =>
            Equals(entry.Properties["Code"], "Device.Name.Required")
            && Equals(entry.Properties["DeviceCode"], "WH-001")
            && Equals(entry.Properties["PropertyName"], "Name")
            && Equals(entry.Properties["Message"], "Device name is required."));
        Assert.Contains(errors, entry =>
            Equals(entry.Properties["Code"], "Catalog.Invalid")
            && entry.Properties["DeviceCode"] is null
            && entry.Properties["PropertyName"] is null
            && Equals(entry.Properties["Message"], "Catalog configuration is invalid."));
    }

    /// <summary>
    /// Verifies that expected API failures are attributed to synchronization,
    /// retain their original exception, and request application shutdown.
    /// Cancellation without a host stop request is treated as a failure.
    /// </summary>
    /// <param name="failureKind">The kind of API failure to simulate.</param>
    [Theory]
    [InlineData("HttpFailure")]
    [InlineData("MalformedJson")]
    [InlineData("UnrequestedCancellation")]
    [InlineData("Timeout")]
    public async Task ExecuteAsyncWithExpectedApiFailureLogsExceptionAndRequestsShutdown(
        string failureKind)
    {
        Exception expected = failureKind switch
        {
            "HttpFailure" => new HttpRequestException("API request failed."),
            "MalformedJson" => new JsonException("API response JSON is malformed."),
            "UnrequestedCancellation" => new OperationCanceledException(
                "API request canceled independently."),
            "Timeout" => new TaskCanceledException(
                "API request timed out.", new TimeoutException("Request deadline expired.")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(_ =>
            Task.FromException<DeviceCatalogSynchronizationResponse>(expected));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.False(apiClient.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.Equal("normal-operation", logger.Entries[0].Properties["PlanName"]);
        var error = logger.Entries[1];
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Same(expected, error.Exception);
        Assert.Equal("Device catalog synchronization failed.", error.Message);
    }

    /// <summary>
    /// Verifies that stopping the worker cancels an in-flight API operation
    /// and logs normal cancellation without requesting shutdown again.
    /// </summary>
    [Fact]
    public async Task StopAsyncDuringSynchronizationCancelsApiWithoutLoggingAnError()
    {
        var synchronizationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<DeviceCatalogSynchronizationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new StubDeviceSimulatorApiClient(async token =>
        {
            synchronizationStarted.TrySetResult(true);
            return await pendingResponse.Task.WaitAsync(token);
        });
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await synchronizationStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            await AwaitExecutionAsync(worker);

            Assert.Equal(1, apiClient.CallCount);
            Assert.Equal(0, apiClient.GetDevicesCallCount);
            Assert.Empty(apiClient.ActivationRequests);
            AssertNoRuntimeRequests(apiClient);
            Assert.True(apiClient.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.Equal(2, logger.Entries.Count);
            Assert.All(logger.Entries, entry =>
            {
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Null(entry.Exception);
            });
            Assert.Equal("normal-operation", logger.Entries[0].Properties["PlanName"]);
            Assert.Equal("Device catalog synchronization was canceled.", logger.Entries[1].Message);
        }
        finally
        {
            pendingResponse.TrySetResult(CreateSuccessfulSynchronizationResponse());
        }
    }

    /// <summary>
    /// Verifies that an unexpected API programming failure propagates through
    /// the execution task instead of being handled as an expected startup failure.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnexpectedApiFailurePropagatesException()
    {
        var expected = new InvalidOperationException("Unexpected API client failure.");
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(_ =>
            Task.FromException<DeviceCatalogSynchronizationResponse>(expected));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(worker));

        Assert.Same(expected, actual);
        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(0, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("normal-operation", entry.Properties["PlanName"]);
        Assert.Null(entry.Exception);
    }

    /// <summary>
    /// Verifies that an empty device list produces a warning before shutdown
    /// and does not produce a successful device-count log.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithNoDevicesLogsWarningBeforeRequestingShutdown()
    {
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(
                Array.Empty<SimulatorDeviceResponse>()));
        var logger = new RecordingLogger();
        var warningCountWhenShutdownWasRequested = -1;
        var lifetime = new RecordingApplicationLifetime(() =>
            warningCountWhenShutdownWasRequested = logger.Entries.Count(
                entry => entry.Level == LogLevel.Warning));
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(1, warningCountWhenShutdownWasRequested);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries.Take(2), entry =>
            Assert.Equal(LogLevel.Information, entry.Level));
        var warning = logger.Entries[2];
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("No devices were returned by the API. Simulation cannot start.", warning.Message);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(logger.Entries, entry => entry.Properties.ContainsKey("DeviceCount"));
    }

    /// <summary>
    /// Verifies that expected retrieval failures are attributed to device
    /// retrieval, preserve exception details, and request shutdown once.
    /// </summary>
    /// <param name="failureKind">The kind of device retrieval failure to simulate.</param>
    [Theory]
    [InlineData("HttpFailure")]
    [InlineData("MalformedJson")]
    [InlineData("UnrequestedCancellation")]
    [InlineData("Timeout")]
    public async Task ExecuteAsyncWithExpectedRetrievalFailureLogsExceptionAndRequestsShutdown(
        string failureKind)
    {
        Exception expected = failureKind switch
        {
            "HttpFailure" => new HttpRequestException("Device retrieval request failed."),
            "MalformedJson" => new JsonException("Device response JSON is malformed."),
            "UnrequestedCancellation" => new OperationCanceledException(
                "Device retrieval canceled independently."),
            "Timeout" => new TaskCanceledException(
                "Device retrieval timed out.", new TimeoutException("Request deadline expired.")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromException<IReadOnlyList<SimulatorDeviceResponse>>(expected));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.False(apiClient.LastGetDevicesCancellationToken.IsCancellationRequested);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries.Take(2), entry =>
            Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Equal("normal-operation", logger.Entries[0].Properties["PlanName"]);
        Assert.Equal(1, logger.Entries[1].Properties["Created"]);
        var error = logger.Entries[2];
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Same(expected, error.Exception);
        Assert.Equal("Device retrieval failed.", error.Message);
    }

    /// <summary>
    /// Verifies that stopping the worker cancels an in-flight device retrieval
    /// and logs normal cancellation without requesting shutdown again.
    /// </summary>
    [Fact]
    public async Task StopAsyncDuringDeviceRetrievalCancelsRequestWithoutLoggingAnError()
    {
        var retrievalStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingDevices = new TaskCompletionSource<IReadOnlyList<SimulatorDeviceResponse>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: async token =>
        {
            retrievalStarted.TrySetResult(true);
            return await pendingDevices.Task.WaitAsync(token);
        });
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await retrievalStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            await AwaitExecutionAsync(worker);

            Assert.Equal(1, apiClient.CallCount);
            Assert.Equal(1, apiClient.GetDevicesCallCount);
            Assert.Empty(apiClient.ActivationRequests);
            AssertNoRuntimeRequests(apiClient);
            Assert.True(apiClient.LastGetDevicesCancellationToken.IsCancellationRequested);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.Equal(3, logger.Entries.Count);
            Assert.All(logger.Entries, entry =>
            {
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Null(entry.Exception);
            });
            Assert.Equal("Device retrieval was canceled.", logger.Entries[2].Message);
        }
        finally
        {
            pendingDevices.TrySetResult(CreateAvailableDevices());
        }
    }

    /// <summary>
    /// Verifies that an unexpected retrieval programming failure propagates
    /// through execution without being handled as an expected startup failure.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnexpectedRetrievalFailurePropagatesException()
    {
        var expected = new InvalidOperationException("Unexpected retrieval failure.");
        var reader = new StubSimulationPlanReader();
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromException<IReadOnlyList<SimulatorDeviceResponse>>(expected));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunWorkerAsync(worker));

        Assert.Same(expected, actual);
        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
        });
        Assert.Equal("normal-operation", logger.Entries[0].Properties["PlanName"]);
        Assert.Equal(1, logger.Entries[1].Properties["Created"]);
    }

    /// <summary>
    /// Verifies that a device preparation service is required by the worker.
    /// </summary>
    [Fact]
    public void ConstructorWithNullDevicePreparationServiceThrowsArgumentNullException()
    {
        var reader = new StubSimulationPlanReader();
        var loader = new SimulationPlanLoader(reader, new SimulationPlanValidator());
        var options = Options.Create(new DeviceSimulatorOptions { PlanName = "normal-operation" });
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var apiClient = new StubDeviceSimulatorApiClient();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationWorker(logger, loader, options, lifetime, apiClient, null!,
                CreateHeartbeatCoordinator(apiClient),
                CreateTelemetryCoordinator(apiClient),
                new SimulationPlanResolver(new ScenarioTargetResolver()),
                new DeviceTemperatureScenarioSequenceFactory(), new FakeTimeProvider()));

        Assert.Equal("devicePreparationService", exception.ParamName);
    }

    /// <summary>
    /// Verifies that preparation excludes retired devices and reports every
    /// eligible device, including activations that report no lifecycle change.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithMixedLifecyclesLogsOnlyPreparedDevices()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Retired),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(3, SimulatorDeviceLifecycle.Active),
            CreatePreparationDevice(4, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(5, SimulatorDeviceLifecycle.Retired)
        };
        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            activate: (deviceId, _) => Task.FromResult(
                CreateSuccessfulActivationResponse(deviceId, changed: false)));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunUntilRuntimeLoopsAndStopAsync(worker, apiClient, 3);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Equal(new[] { devices[1].Id, devices[3].Id },
            apiClient.ActivationRequests.Select(request => request.DeviceId).ToArray());
        Assert.Equal(9, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Equal(5, logger.Entries[2].Properties["DeviceCount"]);
        AssertPreparedDevicesAreLogged(logger, new[] { devices[1], devices[2], devices[3] });
        AssertHeartbeatDevices(apiClient, new[] { devices[1], devices[2], devices[3] });
        AssertTelemetryDevices(apiClient, new[] { devices[1], devices[2], devices[3] });
        Assert.Equal(SimulatorDeviceLifecycle.Registered, devices[1].Lifecycle);
        Assert.Equal(SimulatorDeviceLifecycle.Registered, devices[3].Lifecycle);
    }

    /// <summary>
    /// Verifies that a nonempty catalog containing only retired devices produces
    /// a preparation warning before shutdown and no readiness messages.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithOnlyRetiredDevicesLogsWarningBeforeRequestingShutdown()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Retired),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Retired)
        };
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var warningCountAtShutdown = -1;
        var lifetime = new RecordingApplicationLifetime(() =>
            warningCountAtShutdown = logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(1, warningCountAtShutdown);
        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries.Take(3), entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Equal(2, logger.Entries[2].Properties["DeviceCount"]);
        var warning = logger.Entries[3];
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Equal(
            "No eligible devices are available after preparation. Simulation cannot start.",
            warning.Message);
        AssertNoReadinessLogs(logger);
    }

    /// <summary>
    /// Verifies that already active devices reach readiness without activation
    /// requests or shutdown, including preparation with no asynchronous HTTP work.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithOnlyActiveDevicesReportsReadinessWithoutActivation()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Active)
        };
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunUntilRuntimeLoopsAndStopAsync(worker, apiClient, devices.Length);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Empty(apiClient.ActivationRequests);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Equal(8, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        AssertPreparedDevicesAreLogged(logger, devices);
        AssertHeartbeatDevices(apiClient, devices);
        AssertTelemetryDevices(apiClient, devices);
    }

    /// <summary>
    /// Verifies that failure on a later activation preserves exception details,
    /// stops further requests, and logs a preparation error before shutdown.
    /// A previous successful activation does not produce a readiness report.
    /// </summary>
    /// <param name="failureKind">The activation failure to simulate.</param>
    [Theory]
    [InlineData("HttpFailure")]
    [InlineData("MalformedJson")]
    [InlineData("UnrequestedCancellation")]
    [InlineData("Timeout")]
    public async Task ExecuteAsyncWithExpectedActivationFailureLogsErrorAndStopsPreparation(
        string failureKind)
    {
        Exception expected = failureKind switch
        {
            "HttpFailure" => new HttpRequestException("Device activation failed."),
            "MalformedJson" => new JsonException("Activation response JSON is malformed."),
            "UnrequestedCancellation" => new OperationCanceledException(
                "Activation canceled independently."),
            "Timeout" => new TaskCanceledException(
                "Activation timed out.", new TimeoutException("Request deadline expired.")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(3, SimulatorDeviceLifecycle.Registered)
        };
        var completedActivations = new ConcurrentQueue<Guid>();
        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            activate: (deviceId, _) =>
            {
                if (deviceId == devices[1].Id)
                {
                    return Task.FromException<SimulatorDeviceActivationResponse>(expected);
                }

                completedActivations.Enqueue(deviceId);
                return Task.FromResult(CreateSuccessfulActivationResponse(deviceId));
            });
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var errorCountAtShutdown = -1;
        var lifetime = new RecordingApplicationLifetime(() =>
            errorCountAtShutdown = logger.Entries.Count(entry => entry.Level == LogLevel.Error));
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Equal(new[] { devices[0].Id, devices[1].Id },
            apiClient.ActivationRequests.Select(request => request.DeviceId).ToArray());
        Assert.Equal(devices[0].Id, Assert.Single(completedActivations));
        Assert.All(apiClient.ActivationRequests, request =>
            Assert.False(request.CancellationToken.IsCancellationRequested));
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(1, errorCountAtShutdown);
        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries.Take(3), entry => Assert.Equal(LogLevel.Information, entry.Level));
        var error = logger.Entries[3];
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Same(expected, error.Exception);
        Assert.Equal("Device preparation failed. Simulation startup will stop.", error.Message);
        AssertNoReadinessLogs(logger);
        AssertNoRuntimeRequests(apiClient);
    }

    /// <summary>
    /// Verifies that an unsupported device lifecycle is attributed to preparation
    /// and requests shutdown before any activation or readiness report.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnsupportedLifecycleLogsPreparationFailure()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, (SimulatorDeviceLifecycle)999),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered)
        };
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        await RunWorkerAsync(worker);

        Assert.Empty(apiClient.ActivationRequests);
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(4, logger.Entries.Count);
        var error = logger.Entries[3];
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal("Device preparation failed. Simulation startup will stop.", error.Message);
        AssertNoReadinessLogs(logger);
    }

    /// <summary>
    /// Verifies that stopping the worker cancels an in-flight activation,
    /// prevents subsequent activation, and reports normal cancellation.
    /// </summary>
    [Fact]
    public async Task StopAsyncDuringDevicePreparationCancelsActivationWithoutLoggingAnError()
    {
        var activationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingActivation = new TaskCompletionSource<SimulatorDeviceActivationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered)
        };
        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            activate: async (_, token) =>
            {
                activationStarted.TrySetResult(true);
                return await pendingActivation.Task.WaitAsync(token);
            });
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await activationStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            await AwaitExecutionAsync(worker);

            var request = Assert.Single(apiClient.ActivationRequests);
            Assert.Equal(devices[0].Id, request.DeviceId);
            Assert.True(request.CancellationToken.IsCancellationRequested);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.Equal(4, logger.Entries.Count);
            Assert.All(logger.Entries, entry =>
            {
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Null(entry.Exception);
            });
            Assert.Equal("Device preparation was canceled.", logger.Entries[3].Message);
            AssertNoReadinessLogs(logger);
            AssertNoRuntimeRequests(apiClient);
        }
        finally
        {
            pendingActivation.TrySetResult(CreateSuccessfulActivationResponse(devices[0].Id));
        }
    }

    /// <summary>
    /// Verifies that preparation failures outside the expected exception filter
    /// remain observable through the worker execution task.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithUnexpectedPreparationFailurePropagatesException()
    {
        var expected = new NotSupportedException("Unexpected activation client failure.");
        var apiClient = new StubDeviceSimulatorApiClient(activate: (_, _) =>
            Task.FromException<SimulatorDeviceActivationResponse>(expected));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        var actual = await Assert.ThrowsAsync<NotSupportedException>(() => RunWorkerAsync(worker));

        Assert.Same(expected, actual);
        Assert.Single(apiClient.ActivationRequests);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        AssertNoReadinessLogs(logger);
        AssertNoRuntimeRequests(apiClient);
    }

    /// <summary>
    /// Verifies that worker execution and readiness reporting wait for activation
    /// to complete before proceeding to another registered device.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWaitsForDevicePreparationBeforeReportingReadiness()
    {
        var activationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingActivation = new TaskCompletionSource<SimulatorDeviceActivationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered)
        };
        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            activate: (deviceId, token) =>
            {
                if (deviceId == devices[0].Id)
                {
                    activationStarted.TrySetResult(true);
                    return pendingActivation.Task.WaitAsync(token);
                }

                return Task.FromResult(CreateSuccessfulActivationResponse(deviceId));
            });
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await activationStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            var executionTask = worker.ExecuteTask;
            Assert.NotNull(executionTask);
            Assert.False(executionTask.IsCompleted);
            Assert.Equal(devices[0].Id, Assert.Single(apiClient.ActivationRequests).DeviceId);
            AssertNoReadinessLogs(logger);
            AssertNoRuntimeRequests(apiClient);

            pendingActivation.SetResult(CreateSuccessfulActivationResponse(devices[0].Id));
            await apiClient.WaitForRuntimeRequestsAsync(devices.Length);
            Assert.False(executionTask.IsCompleted);
            await StopWorkerAsync(worker);

            Assert.Equal(devices.Select(device => device.Id).ToArray(),
                apiClient.ActivationRequests.Select(request => request.DeviceId).ToArray());
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.Equal(8, logger.Entries.Count);
            AssertPreparedDevicesAreLogged(logger, devices);
            AssertHeartbeatDevices(apiClient, devices);
            AssertTelemetryDevices(apiClient, devices);
        }
        finally
        {
            pendingActivation.TrySetResult(CreateSuccessfulActivationResponse(devices[0].Id));
            await StopWorkerAsync(worker);
        }
    }

    /// <summary>Verifies that heartbeat coordination is required by the worker.</summary>
    [Fact]
    public void ConstructorWithNullHeartbeatCoordinatorThrowsArgumentNullException()
    {
        var apiClient = new StubDeviceSimulatorApiClient();
        var loader = new SimulationPlanLoader(new StubSimulationPlanReader(), new SimulationPlanValidator());
        var options = Options.Create(new DeviceSimulatorOptions { PlanName = "normal-operation" });

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationWorker(new RecordingLogger(), loader, options,
                new RecordingApplicationLifetime(), apiClient,
                new DevicePreparationService(apiClient), null!,
                CreateTelemetryCoordinator(apiClient),
                new SimulationPlanResolver(new ScenarioTargetResolver()),
                new DeviceTemperatureScenarioSequenceFactory(), new FakeTimeProvider()));

        Assert.Equal("heartbeatCoordinator", exception.ParamName);
    }

    /// <summary>Verifies that telemetry coordination is required by the worker.</summary>
    [Fact]
    public void ConstructorWithNullTelemetryCoordinatorThrowsArgumentNullException()
    {
        var apiClient = new StubDeviceSimulatorApiClient();
        var loader = new SimulationPlanLoader(new StubSimulationPlanReader(), new SimulationPlanValidator());
        var options = Options.Create(new DeviceSimulatorOptions { PlanName = "normal-operation" });

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationWorker(new RecordingLogger(), loader, options,
                new RecordingApplicationLifetime(), apiClient,
                new DevicePreparationService(apiClient), CreateHeartbeatCoordinator(apiClient),
                null!, new SimulationPlanResolver(new ScenarioTargetResolver()),
                new DeviceTemperatureScenarioSequenceFactory(), new FakeTimeProvider()));

        Assert.Equal("telemetryCoordinator", exception.ParamName);
    }

    /// <summary>Verifies that scenario resolution is required by the worker.</summary>
    [Fact]
    public void ConstructorWithNullPlanResolverThrowsArgumentNullException()
    {
        var apiClient = new StubDeviceSimulatorApiClient();
        var loader = new SimulationPlanLoader(new StubSimulationPlanReader(), new SimulationPlanValidator());
        var options = Options.Create(new DeviceSimulatorOptions { PlanName = "normal-operation" });

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationWorker(new RecordingLogger(), loader, options,
                new RecordingApplicationLifetime(), apiClient,
                new DevicePreparationService(apiClient), CreateHeartbeatCoordinator(apiClient),
                CreateTelemetryCoordinator(apiClient), null!,
                new DeviceTemperatureScenarioSequenceFactory(), new FakeTimeProvider()));

        Assert.Equal("planResolver", exception.ParamName);
    }

    /// <summary>
    /// Verifies that scenario resolution receives devices after activation and
    /// permits both runtime groups to start. Resolution logs the plan's own name
    /// and the number of scenarios, independently of the configured file name.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncResolvesScenariosUsingPreparedDevicesBeforeStartingRuntime()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Retired),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered),
            CreatePreparationDevice(3, SimulatorDeviceLifecycle.Active)
        };
        var first = CreateHighTemperatureScenario(new ScenarioTargetDefinition(
            ScenarioTargetMode.SpecificDevice, DeviceCode: devices[1].Code));
        var second = CreateHighTemperatureScenario(new ScenarioTargetDefinition(
            ScenarioTargetMode.RandomCompatible, Count: 2)) with
        { Name = "SecondTemperatureScenario" };
        var plan = CreateValidPlan() with
        {
            Name = "WarehouseScenarios",
            Scenarios = new ScenarioDefinition[] { first, second }
        };
        Assert.True(new SimulationPlanValidator().Validate(plan).IsValid);
        var reader = new StubSimulationPlanReader((_, _) => Task.FromResult(plan));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var sequencesPreparedBeforeRequests = new ConcurrentQueue<bool>();
        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            heartbeat: (id, token) =>
            {
                sequencesPreparedBeforeRequests.Enqueue(logger.Entries.Any(entry =>
                    entry.Properties.ContainsKey("DeviceCount")
                    && entry.Properties.TryGetValue("ScenarioCount", out var count) && Equals(count, 2)));
                return WaitForHeartbeatCancellationAsync(id, token);
            },
            telemetry: (_, measurement, token) =>
            {
                sequencesPreparedBeforeRequests.Enqueue(logger.Entries.Any(entry =>
                    entry.Properties.ContainsKey("DeviceCount")
                    && entry.Properties.TryGetValue("ScenarioCount", out var count) && Equals(count, 2)));
                return WaitForTelemetryCancellationAsync(measurement, token);
            });
        using var worker = CreateWorker(reader, logger, lifetime, apiClient, "selected-file");

        await RunUntilRuntimeLoopsAndStopAsync(worker, apiClient, 2);

        Assert.Equal(devices[1].Id, Assert.Single(apiClient.ActivationRequests).DeviceId);
        Assert.Equal("selected-file", reader.LastPlanName);
        AssertResolvedPlanIsLogged(logger, plan);
        AssertSequencesPreparedIsLogged(logger, 2, 2);
        Assert.Equal(4, sequencesPreparedBeforeRequests.Count);
        Assert.All(sequencesPreparedBeforeRequests, resolved => Assert.True(resolved));
        var prepared = new[] { devices[1], devices[2] };
        AssertPreparedDevicesAreLogged(logger, prepared);
        AssertHeartbeatDevices(apiClient, prepared);
        AssertTelemetryDevices(apiClient, prepared);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
    }

    /// <summary>
    /// Verifies that a structurally valid plan can fail against the actual
    /// prepared catalog. The worker logs the resolution failure before shutdown
    /// and dispatches neither heartbeats nor telemetry.
    /// </summary>
    /// <param name="failureKind">The unavailable or incompatible target configuration.</param>
    [Theory]
    [InlineData("NoCompatibleDevices")]
    [InlineData("MissingSpecificDevice")]
    [InlineData("RetiredSpecificDevice")]
    [InlineData("IncompatibleSpecificDevice")]
    [InlineData("InsufficientRandomDevices")]
    public async Task ExecuteAsyncWithUnresolvableScenariosLogsErrorAndPreventsRuntime(
        string failureKind)
    {
        var devices = CreateAvailableDevices().ToList();
        var target = failureKind switch
        {
            "NoCompatibleDevices" => new ScenarioTargetDefinition(ScenarioTargetMode.All),
            "MissingSpecificDevice" => new ScenarioTargetDefinition(
                ScenarioTargetMode.SpecificDevice, DeviceCode: "WH-404"),
            "RetiredSpecificDevice" => new ScenarioTargetDefinition(
                ScenarioTargetMode.SpecificDevice, DeviceCode: "WH-003"),
            "IncompatibleSpecificDevice" => new ScenarioTargetDefinition(
                ScenarioTargetMode.SpecificDevice, DeviceCode: "WH-002"),
            "InsufficientRandomDevices" => new ScenarioTargetDefinition(
                ScenarioTargetMode.RandomCompatible, Count: 2),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        if (failureKind == "NoCompatibleDevices")
        {
            devices[0] = devices[0] with
            {
                Capabilities = new[] { SimulatorDeviceCapability.Humidity }
            };
        }
        if (failureKind == "RetiredSpecificDevice")
        {
            devices.Add(CreatePreparationDevice(3, SimulatorDeviceLifecycle.Retired));
        }
        var scenario = CreateHighTemperatureScenario(target);
        var plan = CreateValidPlan() with
        {
            Name = "UnresolvableWarehousePlan",
            Scenarios = new ScenarioDefinition[] { scenario }
        };
        // The failure must reach resolution, rather than the plan validator.
        Assert.True(new SimulationPlanValidator().Validate(plan).IsValid);
        var reader = new StubSimulationPlanReader((_, _) => Task.FromResult(plan));
        var logger = new RecordingLogger();
        var resolutionErrorCountAtShutdown = -1;
        var lifetime = new RecordingApplicationLifetime(() =>
            resolutionErrorCountAtShutdown = logger.Entries.Count(entry =>
                entry.Level == LogLevel.Error
                && entry.Message == $"Simulation plan {plan.Name} could not be resolved."));
        var apiClient = new StubDeviceSimulatorApiClient(getDevices: _ =>
            Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        using var worker = CreateWorker(reader, logger, lifetime, apiClient, "selected-file");

        await RunWorkerAsync(worker);

        Assert.Equal(1, reader.CallCount);
        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, apiClient.GetDevicesCallCount);
        Assert.Equal(devices[0].Id, Assert.Single(apiClient.ActivationRequests).DeviceId);
        AssertPreparedDevicesAreLogged(logger, devices.Where(device =>
            device.Lifecycle != SimulatorDeviceLifecycle.Retired).ToArray());
        AssertNoRuntimeRequests(apiClient);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(1, resolutionErrorCountAtShutdown);
        var error = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
        var exception = Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains(scenario.Name, exception.Message);
        Assert.Equal(plan.Name, error.Properties["PlanName"]);
        Assert.Equal($"Simulation plan {plan.Name} could not be resolved.", error.Message);
        Assert.Equal(error, logger.Entries[^1]);
        Assert.DoesNotContain(logger.Entries, entry => entry.Properties.ContainsKey("ScenarioCount"));
    }

    /// <summary>
    /// Verifies that a failed runtime group does not stop the other group.
    /// Caller cancellation subsequently completes the worker without an
    /// additional application shutdown request.
    /// </summary>
    /// <param name="failedGroup">The runtime group whose requests fail immediately.</param>
    [Theory]
    [InlineData("Heartbeat")]
    [InlineData("Telemetry")]
    public async Task ExecuteAsyncWithOneRuntimeGroupFailedKeepsOtherGroupRunning(string failedGroup)
    {
        var failure = new HttpRequestException("Device request rejected.", null, HttpStatusCode.Conflict);
        var apiClient = new StubDeviceSimulatorApiClient(
            heartbeat: (id, token) => failedGroup == "Heartbeat"
                ? Task.FromException<SimulatorDeviceHeartbeatResponse>(failure)
                : WaitForHeartbeatCancellationAsync(id, token),
            telemetry: (_, measurement, token) => failedGroup == "Telemetry"
                ? Task.FromException<SimulatorTelemetryRecordingResponse>(failure)
                : WaitForTelemetryCancellationAsync(measurement, token));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await apiClient.WaitForRuntimeRequestsAsync(2);

            var executionTask = worker.ExecuteTask;
            Assert.NotNull(executionTask);
            Assert.False(executionTask.IsCompleted);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            AssertHeartbeatDevices(apiClient, CreateAvailableDevices());
            AssertTelemetryDevices(apiClient, CreateAvailableDevices());

            await StopWorkerAsync(worker);

            Assert.True(executionTask.IsCompletedSuccessfully);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    /// <summary>
    /// Verifies that exhaustion of both runtime groups logs one warning before
    /// requesting application shutdown, including independent request timeouts.
    /// </summary>
    /// <param name="failureKind">The failure that terminates each device runner.</param>
    [Theory]
    [InlineData("HttpFailure")]
    [InlineData("Timeout")]
    public async Task ExecuteAsyncWithAllRuntimeLoopsFailedLogsWarningAndRequestsShutdown(
        string failureKind)
    {
        Exception failure = failureKind switch
        {
            "HttpFailure" => new HttpRequestException(
                "API is unavailable.", null, HttpStatusCode.ServiceUnavailable),
            "Timeout" => new TaskCanceledException("Request timed out.", new TimeoutException()),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var apiClient = new StubDeviceSimulatorApiClient(
            heartbeat: (_, _) => Task.FromException<SimulatorDeviceHeartbeatResponse>(failure),
            telemetry: (_, _, _) => Task.FromException<SimulatorTelemetryRecordingResponse>(failure));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var warningWasLoggedBeforeShutdown = false;
        var lifetime = new RecordingApplicationLifetime(() =>
            warningWasLoggedBeforeShutdown = logger.Entries.Any(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message == "All device heartbeat and telemetry loops have ended. Simulation will stop."));
        using var worker = CreateWorker(reader, logger, lifetime, apiClient,
            heartbeatCoordinator: CreateHeartbeatCoordinator(apiClient, maxFailures: 1),
            telemetryCoordinator: CreateTelemetryCoordinator(apiClient, maxFailures: 1));

        await RunWorkerAsync(worker);

        AssertHeartbeatDevices(apiClient, CreateAvailableDevices());
        AssertTelemetryDevices(apiClient, CreateAvailableDevices());
        AssertPreparedDevicesAreLogged(logger, CreateAvailableDevices());
        Assert.True(warningWasLoggedBeforeShutdown);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.False(reader.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(9, logger.Entries.Count);
        var warning = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Warning));
        Assert.Null(warning.Exception);
        Assert.Equal("All device heartbeat and telemetry loops have ended. Simulation will stop.", warning.Message);
        Assert.Equal(warning, logger.Entries[^1]);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// Verifies that one failed loop does not stop the worker while another
    /// device is still running, and normal shutdown does not request host stop again.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithOneHeartbeatLoopFailedKeepsOtherDeviceRunning()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Active)
        };
        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            heartbeat: (id, token) => id == devices[0].Id
                ? Task.FromException<SimulatorDeviceHeartbeatResponse>(
                    new HttpRequestException("Device is retired.", null, HttpStatusCode.Conflict))
                : WaitForHeartbeatCancellationAsync(id, token));
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await apiClient.WaitForRuntimeRequestsAsync(devices.Length);

            var executionTask = worker.ExecuteTask;
            Assert.NotNull(executionTask);
            Assert.False(executionTask.IsCompleted);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            AssertHeartbeatDevices(apiClient, devices);
            AssertTelemetryDevices(apiClient, devices);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);

            await StopWorkerAsync(worker);

            Assert.True(executionTask.IsCompletedSuccessfully);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.All(apiClient.HeartbeatRequests, request =>
                Assert.True(request.CancellationToken.IsCancellationRequested));
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    /// <summary>
    /// Verifies that stopping the worker cancels both runtime groups and waits
    /// for every in-flight request, including a request that acknowledges
    /// cancellation later than the others. Expected cancellation does not
    /// produce an exhausted-runtime warning or request host shutdown again.
    /// </summary>
    /// <param name="delayedGroup">The group containing the last request to acknowledge cancellation.</param>
    [Theory]
    [InlineData("Heartbeat")]
    [InlineData("Telemetry")]
    public async Task StopAsyncDuringRuntimeWaitsForEveryRequestWithoutRequestingShutdown(string delayedGroup)
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Active)
        };
        var delayedRequestCanceled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherRequestsCanceled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelayedRequest = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgedRequests = 0;

        async Task WaitForRequestCancellationAsync(string group, Guid deviceId, CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (group == delayedGroup && deviceId == devices[1].Id)
                {
                    delayedRequestCanceled.TrySetResult(true);
                    await releaseDelayedRequest.Task.WaitAsync(TestTimeout, CancellationToken.None);
                }
                else if (Interlocked.Increment(ref acknowledgedRequests) == 3)
                {
                    otherRequestsCanceled.TrySetResult(true);
                }

                throw;
            }
        }

        var apiClient = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices),
            heartbeat: async (id, token) =>
            {
                await WaitForRequestCancellationAsync("Heartbeat", id, token);
                return new SimulatorDeviceHeartbeatResponse(
                    id, new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc), true);
            },
            telemetry: async (id, measurement, token) =>
            {
                await WaitForRequestCancellationAsync("Telemetry", id, token);
                return new SimulatorTelemetryRecordingResponse(measurement.MeasurementId, true);
            });
        var reader = new StubSimulationPlanReader();
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        using var worker = CreateWorker(reader, logger, lifetime, apiClient);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await apiClient.WaitForRuntimeRequestsAsync(devices.Length);

            var stopTask = worker.StopAsync(CancellationToken.None);
            await Task.WhenAll(delayedRequestCanceled.Task, otherRequestsCanceled.Task)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(stopTask.IsCompleted);
            var executionTask = worker.ExecuteTask;
            Assert.NotNull(executionTask);
            Assert.False(executionTask.IsCompleted);
            Assert.Equal(0, lifetime.StopApplicationCallCount);

            releaseDelayedRequest.SetResult(true);
            await stopTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await AwaitExecutionAsync(worker);

            Assert.True(executionTask.IsCompletedSuccessfully);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            AssertHeartbeatDevices(apiClient, devices);
            AssertTelemetryDevices(apiClient, devices);
            Assert.All(apiClient.HeartbeatRequests, request =>
                Assert.True(request.CancellationToken.IsCancellationRequested));
            Assert.All(apiClient.TelemetryRequests, request =>
                Assert.True(request.CancellationToken.IsCancellationRequested));
            Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        }
        finally
        {
            releaseDelayedRequest.TrySetResult(true);
            await StopWorkerAsync(worker);
        }
    }

    /// <summary>Verifies that sequence preparation and shared time dependencies are required.</summary>
    /// <param name="dependency">The new dependency omitted from construction.</param>
    [Theory]
    [InlineData("deviceTemperatureScenarioSequence")]
    [InlineData("time")]
    public void ConstructorWithNullScenarioPreparationDependencyThrowsArgumentNullException(string dependency)
    {
        var client = new StubDeviceSimulatorApiClient();
        var loader = new SimulationPlanLoader(new StubSimulationPlanReader(), new SimulationPlanValidator());

        var exception = Assert.Throws<ArgumentNullException>(() => new SimulationWorker(
            new RecordingLogger(), loader,
            Options.Create(new DeviceSimulatorOptions { PlanName = "normal-operation" }),
            new RecordingApplicationLifetime(), client, new DevicePreparationService(client),
            CreateHeartbeatCoordinator(client), CreateTelemetryCoordinator(client),
            new SimulationPlanResolver(new ScenarioTargetResolver()),
            dependency == "deviceTemperatureScenarioSequence" ? null! : new DeviceTemperatureScenarioSequenceFactory(),
            dependency == "time" ? null! : new FakeTimeProvider()));

        Assert.Equal(dependency, exception.ParamName);
    }

    /// <summary>
    /// Verifies that resolved targets receive abnormal temperatures after activation,
    /// while untargeted devices and non-temperature metrics retain normal telemetry.
    /// </summary>
    /// <param name="allCompatible">Whether all compatible devices or one specific device is selected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsyncAppliesPreparedSequencesOnlyToResolvedTargets(bool allCompatible)
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active) with
            { Capabilities = new[] { SimulatorDeviceCapability.Temperature } },
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Registered) with
            { Capabilities = new[] { SimulatorDeviceCapability.Temperature } },
            CreatePreparationDevice(3, SimulatorDeviceLifecycle.Active) with
            { Capabilities = new[] { SimulatorDeviceCapability.Humidity } }
        };
        var target = allCompatible
            ? new ScenarioTargetDefinition(ScenarioTargetMode.All)
            : new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: devices[1].Code);
        var scenario = CreateHighTemperatureScenario(target) with
        {
            StartsAfter = TimeSpan.Zero,
            AbnormalMinimum = 40,
            AbnormalMaximum = 50,
            MaximumRisePerMeasurement = 100
        };
        var plan = CreateValidPlan() with { Scenarios = new ScenarioDefinition[] { scenario } };
        var reader = new StubSimulationPlanReader((_, _) => Task.FromResult(plan));
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var client = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        using var worker = CreateWorker(reader, logger, lifetime, client);

        await RunUntilRuntimeLoopsAndStopAsync(worker, client, devices.Length);

        var measurements = client.TelemetryRequests.ToDictionary(request => request.DeviceId,
            request => request.Measurement);
        Assert.InRange(measurements[devices[0].Id].NumericValue!.Value,
            allCompatible ? 40d : 20d, allCompatible ? 50d : 24d);
        Assert.InRange(measurements[devices[1].Id].NumericValue!.Value, 40d, 50d);
        Assert.Equal(SimulatorTelemetryMetric.Humidity, measurements[devices[2].Id].Metric);
        Assert.InRange(measurements[devices[2].Id].NumericValue!.Value, 78d, 82d);
        Assert.Equal(devices[1].Id, Assert.Single(client.ActivationRequests).DeviceId);
        AssertHeartbeatDevices(client, devices);
        AssertSequencesPreparedIsLogged(logger, 1, allCompatible ? 2 : 1);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
    }

    /// <summary>
    /// Verifies that the plan seed initializes temperature target selection in
    /// resolved-device order, and omitting the seed still permits scenario startup.
    /// </summary>
    /// <param name="seed">The optional seed used to prepare abnormal temperatures.</param>
    [Theory]
    [InlineData(42)]
    [InlineData(-17)]
    [InlineData(null)]
    public async Task ExecuteAsyncCreatesTemperatureTargetsUsingOptionalPlanSeed(int? seed)
    {
        var devices = new[]
        {
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Active) with
            { Capabilities = new[] { SimulatorDeviceCapability.Temperature } },
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active) with
            { Capabilities = new[] { SimulatorDeviceCapability.Temperature } }
        };
        var scenario = CreateHighTemperatureScenario(new ScenarioTargetDefinition(ScenarioTargetMode.All)) with
        {
            StartsAfter = TimeSpan.Zero,
            AbnormalMinimum = 40,
            AbnormalMaximum = 50,
            MaximumRisePerMeasurement = 100
        };
        var plan = CreateValidPlan() with { Seed = seed, Scenarios = new ScenarioDefinition[] { scenario } };
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var client = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        using var worker = CreateWorker(new StubSimulationPlanReader((_, _) => Task.FromResult(plan)),
            logger, lifetime, client);

        await RunUntilRuntimeLoopsAndStopAsync(worker, client, devices.Length);

        var measurements = client.TelemetryRequests.ToDictionary(request => request.DeviceId,
            request => request.Measurement.NumericValue!.Value);
        var expectedRandom = seed.HasValue ? new Random(seed.Value) : null;
        foreach (var device in devices.OrderBy(device => device.Code, StringComparer.Ordinal))
        {
            var temperature = measurements[device.Id];
            Assert.InRange(temperature, 40d, 50d);
            if (expectedRandom is not null)
            {
                Assert.Equal(40 + expectedRandom.NextDouble() * 10, temperature);
            }
        }
        AssertSequencesPreparedIsLogged(logger, 1, devices.Length);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
    }

    /// <summary>
    /// Verifies that preparation time is excluded from scenario elapsed time,
    /// while time spent starting heartbeats is included in the shared timeline.
    /// The controlled timestamp uses a different origin from UTC date-time ticks.
    /// </summary>
    /// <param name="advanceDuringHeartbeat">Whether heartbeat startup advances the clock past the scenario start.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsyncCapturesScenarioStartAfterPreparationAndBeforeRuntime(bool advanceDuringHeartbeat)
    {
        var clock = new ObservedTimeProvider();
        var device = CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active) with
        { Capabilities = new[] { SimulatorDeviceCapability.Temperature } };
        var scenario = CreateHighTemperatureScenario(new ScenarioTargetDefinition(ScenarioTargetMode.All)) with
        { AbnormalMinimum = 40, AbnormalMaximum = 50, MaximumRisePerMeasurement = 100 };
        var plan = CreateValidPlan() with { Scenarios = new ScenarioDefinition[] { scenario } };
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var client = new StubDeviceSimulatorApiClient(
            getDevices: _ =>
            {
                clock.Advance(TimeSpan.FromHours(1));
                return Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(new[] { device });
            },
            heartbeat: (id, token) =>
            {
                if (advanceDuringHeartbeat)
                {
                    clock.Advance(TimeSpan.FromMinutes(2));
                }
                return WaitForHeartbeatCancellationAsync(id, token);
            });
        using var worker = CreateWorker(new StubSimulationPlanReader((_, _) => Task.FromResult(plan)),
            logger, lifetime, client, time: clock);

        await RunUntilRuntimeLoopsAndStopAsync(worker, client, 1);

        var temperature = Assert.Single(client.TelemetryRequests).Measurement.NumericValue!.Value;
        Assert.InRange(temperature, advanceDuringHeartbeat ? 40d : 20d,
            advanceDuringHeartbeat ? 50d : 24d);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// Verifies that a creation failure after one valid scenario-device pair
    /// is logged before shutdown and prevents both runtime groups from starting.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncWithSequenceCreationFailureLogsErrorAndPreventsAllRuntimeRequests()
    {
        var devices = new[]
        {
            CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active),
            CreatePreparationDevice(2, SimulatorDeviceLifecycle.Active) with { Id = Guid.Empty }
        };
        var scenario = CreateHighTemperatureScenario(new ScenarioTargetDefinition(ScenarioTargetMode.All));
        var plan = CreateValidPlan() with { Scenarios = new ScenarioDefinition[] { scenario } };
        Assert.True(new SimulationPlanValidator().Validate(plan).IsValid);
        var logger = new RecordingLogger();
        var creationErrorLoggedBeforeShutdown = false;
        var lifetime = new RecordingApplicationLifetime(() => creationErrorLoggedBeforeShutdown =
            logger.Entries.Any(entry => entry.Level == LogLevel.Error && entry.Exception is ArgumentException));
        var client = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(devices));
        using var worker = CreateWorker(new StubSimulationPlanReader((_, _) => Task.FromResult(plan)),
            logger, lifetime, client);

        await RunWorkerAsync(worker);

        AssertResolvedPlanIsLogged(logger, plan);
        AssertNoRuntimeRequests(client);
        Assert.True(creationErrorLoggedBeforeShutdown);
        Assert.Equal(1, lifetime.StopApplicationCallCount);
        var error = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
        Assert.Equal("deviceId", Assert.IsType<ArgumentException>(error.Exception).ParamName);
        Assert.Equal("Temperature scenario sequences could not be created. Simulation startup will stop.",
            error.Message);
        Assert.Equal(error, logger.Entries[^1]);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Properties.ContainsKey("ScenarioCount") && entry.Properties.ContainsKey("DeviceCount"));
    }

    /// <summary>
    /// Verifies that multiple resolved scenarios for one device reach the runner
    /// as one persistent queue, preserve plan order, and wait for recovery.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncRunsQueuedScenariosForTheSameDeviceAcrossTelemetryCycles()
    {
        var clock = new ObservedTimeProvider();
        var device = CreatePreparationDevice(1, SimulatorDeviceLifecycle.Active) with
        { Capabilities = new[] { SimulatorDeviceCapability.Temperature } };
        var first = CreateHighTemperatureScenario(new ScenarioTargetDefinition(ScenarioTargetMode.All)) with
        {
            Name = "FirstTemperatureScenario",
            StartsAfter = TimeSpan.Zero,
            Duration = TimeSpan.FromMinutes(1),
            RecoveryDuration = TimeSpan.FromMinutes(1),
            AbnormalMinimum = 40,
            AbnormalMaximum = 41,
            MaximumRisePerMeasurement = 100,
            MaximumRecoveryPerMeasurement = 100
        };
        var second = first with
        { Name = "SecondTemperatureScenario", AbnormalMinimum = 60, AbnormalMaximum = 61 };
        var plan = CreateValidPlan() with { Scenarios = new ScenarioDefinition[] { first, second } };
        var logger = new RecordingLogger();
        var lifetime = new RecordingApplicationLifetime();
        var client = new StubDeviceSimulatorApiClient(
            getDevices: _ => Task.FromResult<IReadOnlyList<SimulatorDeviceResponse>>(new[] { device }),
            telemetry: (_, measurement, _) =>
                Task.FromResult(new SimulatorTelemetryRecordingResponse(measurement.MeasurementId, true)));
        using var worker = CreateWorker(new StubSimulationPlanReader((_, _) => Task.FromResult(plan)),
            logger, lifetime, client, time: clock);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await clock.WaitForDelayAsync();
            for (var index = 0; index < 3; index++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                await clock.WaitForDelayAsync();
            }

            var requests = client.TelemetryRequests;
            Assert.Equal(4, requests.Count);
            Assert.InRange(requests[0].Measurement.NumericValue!.Value, 40d, 41d);
            Assert.Equal(requests[0].Measurement.NumericValue, requests[1].Measurement.NumericValue);
            Assert.InRange(requests[2].Measurement.NumericValue!.Value, 20d, 24d);
            Assert.InRange(requests[3].Measurement.NumericValue!.Value, 60d, 61d);
            Assert.Equal(4, requests.Select(request => request.Measurement.MeasurementId).Distinct().Count());
            AssertSequencesPreparedIsLogged(logger, 2, 1);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    /// <summary>Checks the separate successful sequence-preparation log and its counts.</summary>
    /// <param name="logger">The worker's captured logs.</param>
    /// <param name="scenarioCount">The number of resolved scenario definitions.</param>
    /// <param name="deviceCount">The number of devices receiving a sequence.</param>
    private static void AssertSequencesPreparedIsLogged(RecordingLogger logger, int scenarioCount, int deviceCount)
    {
        var entry = Assert.Single(logger.Entries.Where(entry =>
            entry.Properties.ContainsKey("ScenarioCount") && entry.Properties.ContainsKey("DeviceCount")));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(scenarioCount, entry.Properties["ScenarioCount"]);
        Assert.Equal(deviceCount, entry.Properties["DeviceCount"]);
        Assert.Equal("Temperature scenario preparation completed. " +
            $"Resolved scenarios: {scenarioCount}, devices with sequences: {deviceCount}.", entry.Message);
    }

    /// <summary>
    /// Shares a controlled clock between the worker and runners and exposes delay
    /// registration so telemetry can be advanced without real sleeps or polling.
    /// </summary>
    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(
            new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero));
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        private readonly long _initialTimestamp;

        /// <summary>Captures the underlying timestamp origin before simulated time advances.</summary>
        public ObservedTimeProvider()
        {
            _initialTimestamp = _clock.GetTimestamp();
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        /// <inheritdoc />
        public override long GetTimestamp() => _clock.GetTimestamp() - _initialTimestamp + 123456789;

        /// <inheritdoc />
        public override long TimestampFrequency => _clock.TimestampFrequency;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }

        /// <summary>Advances the shared time and triggers due callbacks.</summary>
        /// <param name="elapsed">The simulated duration to advance.</param>
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        /// <summary>Waits until a runner has registered its next interval delay.</summary>
        /// <returns>A task representing observation of one telemetry delay.</returns>
        public async Task WaitForDelayAsync()
        {
            var delay = await _delays.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(1), delay);
        }
    }

    /// <summary>
    /// Verifies one preparation summary and an ordered structured information
    /// message for every prepared device, using the active lifecycle.
    /// </summary>
    /// <param name="logger">The logger containing completed startup messages.</param>
    /// <param name="expectedDevices">The eligible input devices in expected order.</param>
    private static void AssertPreparedDevicesAreLogged(
        RecordingLogger logger, IReadOnlyList<SimulatorDeviceResponse> expectedDevices)
    {
        var summary = Assert.Single(logger.Entries.Where(entry =>
            entry.Message.StartsWith("Device preparation completed.", StringComparison.Ordinal)));
        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Null(summary.Exception);
        Assert.Equal(expectedDevices.Count, summary.Properties["DeviceCount"]);

        var details = logger.Entries.Where(entry => entry.Properties.ContainsKey("DeviceId")).ToArray();
        Assert.Equal(expectedDevices.Count, details.Length);
        for (var index = 0; index < expectedDevices.Count; index++)
        {
            var expected = expectedDevices[index];
            var entry = details[index];
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal(expected.Id, entry.Properties["DeviceId"]);
            Assert.Equal(expected.Code, entry.Properties["DeviceCode"]);
            Assert.Equal(expected.Name, entry.Properties["DeviceName"]);
            Assert.Equal(SimulatorDeviceLifecycle.Active, entry.Properties["Lifecycle"]);
        }
    }

    /// <summary>
    /// Verifies that unsuccessful preparation has not reported successful
    /// completion or listed individual devices as ready.
    /// </summary>
    /// <param name="logger">The logger containing startup messages.</param>
    private static void AssertNoReadinessLogs(RecordingLogger logger)
    {
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.StartsWith("Device preparation completed.", StringComparison.Ordinal)
            || entry.Properties.ContainsKey("DeviceId"));
    }

    /// <summary>
    /// Creates a device with deterministic identity for preparation scenarios.
    /// </summary>
    /// <param name="number">The suffix used to distinguish the device.</param>
    /// <param name="lifecycle">The lifecycle returned by device retrieval.</param>
    /// <returns>A device with temperature and humidity capabilities.</returns>
    private static SimulatorDeviceResponse CreatePreparationDevice(
        int number, SimulatorDeviceLifecycle lifecycle)
    {
        return new SimulatorDeviceResponse(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
            $"WH-{number:D3}",
            $"Warehouse Sensor {number}",
            new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity },
            lifecycle);
    }

    /// <summary>
    /// Creates a successful activation result using the requested device ID.
    /// </summary>
    /// <param name="deviceId">The device requested for activation.</param>
    /// <param name="changed">Whether the API reports a lifecycle change.</param>
    /// <returns>An activation response with the active lifecycle.</returns>
    private static SimulatorDeviceActivationResponse CreateSuccessfulActivationResponse(
        Guid deviceId, bool changed = true)
    {
        return new SimulatorDeviceActivationResponse(deviceId, SimulatorDeviceLifecycle.Active, changed);
    }

    /// <summary>Verifies that a stopped startup has dispatched no runtime requests.</summary>
    /// <param name="apiClient">The API substitute recording runtime requests.</param>
    private static void AssertNoRuntimeRequests(StubDeviceSimulatorApiClient apiClient)
    {
        Assert.Empty(apiClient.HeartbeatRequests);
        Assert.Empty(apiClient.TelemetryRequests);
    }

    /// <summary>Verifies the successful resolution message and its structured fields.</summary>
    /// <param name="logger">The logger recording startup messages.</param>
    /// <param name="plan">The plan whose scenarios were resolved.</param>
    private static void AssertResolvedPlanIsLogged(RecordingLogger logger, SimulationPlanDefinition plan)
    {
        var entry = Assert.Single(logger.Entries.Where(log =>
            log.Properties.ContainsKey("ScenarioCount") && log.Properties.ContainsKey("PlanName")));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(plan.Name, entry.Properties["PlanName"]);
        Assert.Equal(plan.Scenarios.Count, entry.Properties["ScenarioCount"]);
        Assert.Equal($"Simulation plan {plan.Name} was resolved. Scenarios: {plan.Scenarios.Count}.",
            entry.Message);
    }

    /// <summary>Creates a valid scenario whose target is resolved against the prepared catalog.</summary>
    /// <param name="target">The target selection requested by the test.</param>
    /// <returns>A high-temperature scenario accepted by the production plan validator.</returns>
    private static HighTemperatureScenarioDefinition CreateHighTemperatureScenario(ScenarioTargetDefinition target)
    {
        return new HighTemperatureScenarioDefinition(
            "GradualHighTemperature", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), target,
            AutoRecover: true, RecoveryDuration: TimeSpan.FromMinutes(2),
            AbnormalMinimum: 30, AbnormalMaximum: 35,
            MaximumRisePerMeasurement: 2, MaximumRecoveryPerMeasurement: 2);
    }

    /// <summary>
    /// Creates a worker using the production loader, validator, preparation
    /// service, plan resolver, and both runtime coordinators with one API substitute.
    /// </summary>
    /// <param name="reader">The reader supplying the test outcome.</param>
    /// <param name="logger">The logger recording worker messages.</param>
    /// <param name="lifetime">The lifetime recording shutdown requests.</param>
    /// <param name="apiClient">The API client supplying startup operation outcomes.</param>
    /// <param name="planName">The plan name exposed through options.</param>
    /// <param name="heartbeatCoordinator">
    /// A configured coordinator, or null to create one with controlled time.
    /// </param>
    /// <param name="telemetryCoordinator">
    /// A configured coordinator, or null to create one with controlled time.
    /// </param>
    /// <param name="time">The time provider shared by the worker and default coordinators.</param>
    /// <returns>A worker that must be disposed by its caller.</returns>
    private static SimulationWorker CreateWorker(
        ISimulationPlanReader reader,
        RecordingLogger logger,
        RecordingApplicationLifetime lifetime,
        IDeviceSimulatorApiClient apiClient,
        string planName = "normal-operation",
        DeviceHeartbeatCoordinator? heartbeatCoordinator = null,
        DeviceTelemetryCoordinator? telemetryCoordinator = null,
        TimeProvider? time = null)
    {
        time ??= new FakeTimeProvider();
        var loader = new SimulationPlanLoader(reader, new SimulationPlanValidator());
        var options = Options.Create(new DeviceSimulatorOptions { PlanName = planName });

        var preparationService = new DevicePreparationService(apiClient);

        return new SimulationWorker(
            logger, loader, options, lifetime, apiClient, preparationService,
            heartbeatCoordinator ?? CreateHeartbeatCoordinator(apiClient, time: time),
            telemetryCoordinator ?? CreateTelemetryCoordinator(apiClient, time: time),
            new SimulationPlanResolver(new ScenarioTargetResolver()),
            new DeviceTemperatureScenarioSequenceFactory(), time);
    }

    /// <summary>
    /// Creates the production coordinator and runner with controlled time.
    /// Their logs are separate from the worker logs under test.
    /// </summary>
    /// <param name="apiClient">The shared API substitute.</param>
    /// <param name="maxFailures">The runner's consecutive failure limit.</param>
    /// <param name="time">The runner clock, or null to create a controlled clock.</param>
    /// <returns>A coordinator for the supplied API behavior.</returns>
    private static DeviceHeartbeatCoordinator CreateHeartbeatCoordinator(
        IDeviceSimulatorApiClient apiClient, int maxFailures = 5, TimeProvider? time = null)
    {
        var options = Options.Create(new DeviceSimulatorOptions
        {
            HeartbeatInterval = TimeSpan.FromMinutes(1),
            MaxConsecutiveHeartbeatFailures = maxFailures
        });
        var runner = new DeviceHeartbeatRunner(
            NullLogger<DeviceHeartbeatRunner>.Instance, apiClient, options,
            time ?? new FakeTimeProvider());

        return new DeviceHeartbeatCoordinator(
            NullLogger<DeviceHeartbeatCoordinator>.Instance, runner);
    }

    /// <summary>
    /// Creates a telemetry coordinator using the supplied API client
    /// and a controlled clock.
    /// </summary>
    /// <param name="apiClient">
    /// The API client used by the telemetry runner.
    /// </param>
    /// <param name="maxFailures">The runner's consecutive failure limit.</param>
    /// <param name="time">The runner clock, or null to create a controlled clock.</param>
    /// <returns>
    /// A coordinator configured for telemetry execution in tests.
    /// </returns>
    private static DeviceTelemetryCoordinator CreateTelemetryCoordinator(
        IDeviceSimulatorApiClient apiClient, int maxFailures = 5, TimeProvider? time = null)
    {
        var clock = time ?? new FakeTimeProvider();

        var options = Options.Create(new DeviceSimulatorOptions
        {
            TelemetryInterval = TimeSpan.FromMinutes(1),
            MaxConsecutiveTelemetryFailures = maxFailures,
            WarehouseTimeZoneId = "UTC"
        });

        var runner = new DeviceTelemetryRunner(
            NullLogger<DeviceTelemetryRunner>.Instance,
            apiClient,
            new NormalTelemetryGenerator(clock),
            options,
            clock);

        return new DeviceTelemetryCoordinator(
            NullLogger<DeviceTelemetryCoordinator>.Instance,
            runner);
    }

    /// <summary>
    /// Checks that exactly the prepared devices dispatched one heartbeat each.
    /// </summary>
    /// <param name="apiClient">The API substitute recording heartbeats.</param>
    /// <param name="devices">The devices expected to run heartbeat loops.</param>
    private static void AssertHeartbeatDevices(
        StubDeviceSimulatorApiClient apiClient, IReadOnlyList<SimulatorDeviceResponse> devices)
    {
        Assert.Equal(devices.Select(device => device.Id).OrderBy(id => id).ToArray(),
            apiClient.HeartbeatRequests.Select(request => request.DeviceId).OrderBy(id => id).ToArray());
    }

    /// <summary>
    /// Checks that each prepared device dispatched its first telemetry request.
    /// Each request remains pending or fails before a second metric is sent.
    /// </summary>
    /// <param name="apiClient">The API substitute recording telemetry requests.</param>
    /// <param name="devices">The devices expected to run telemetry loops.</param>
    private static void AssertTelemetryDevices(
        StubDeviceSimulatorApiClient apiClient, IReadOnlyList<SimulatorDeviceResponse> devices)
    {
        Assert.Equal(devices.Select(device => device.Id).OrderBy(id => id).ToArray(),
            apiClient.TelemetryRequests.Select(request => request.DeviceId).OrderBy(id => id).ToArray());
    }

    /// <summary>
    /// Starts the worker, waits for heartbeat and telemetry requests to enter the API,
    /// then stops and observes execution instead of waiting for infinite loops.
    /// </summary>
    /// <param name="worker">The worker to execute and stop.</param>
    /// <param name="apiClient">The API substitute reporting requests.</param>
    /// <param name="expectedDeviceCount">The number of devices expected in each runtime group.</param>
    /// <returns>A task representing startup and cooperative shutdown.</returns>
    private static async Task RunUntilRuntimeLoopsAndStopAsync(
        SimulationWorker worker, StubDeviceSimulatorApiClient apiClient, int expectedDeviceCount)
    {
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await apiClient.WaitForRuntimeRequestsAsync(expectedDeviceCount);
            var executionTask = worker.ExecuteTask;
            Assert.NotNull(executionTask);
            Assert.False(executionTask.IsCompleted);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    /// <summary>
    /// Cancels background execution and waits for its completion with a timeout.
    /// </summary>
    /// <param name="worker">The worker that has already been started.</param>
    /// <returns>A task representing completion of worker shutdown.</returns>
    private static async Task StopWorkerAsync(SimulationWorker worker)
    {
        // Cleanup must also work when the test token has already been canceled.
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, CancellationToken.None);
        var executionTask = worker.ExecuteTask;
        Assert.NotNull(executionTask);
        await executionTask.WaitAsync(TestTimeout, CancellationToken.None);
    }

    /// <summary>
    /// Starts the worker and waits for a finite failure or shutdown scenario.
    /// </summary>
    /// <param name="worker">The worker to start.</param>
    /// <returns>A task representing completion of worker execution.</returns>
    private static async Task RunWorkerAsync(SimulationWorker worker)
    {
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await AwaitExecutionAsync(worker);
    }

    /// <summary>
    /// Waits for the background execution task with a bounded timeout.
    /// Starting the service alone does not await ExecuteAsync in .NET 10.
    /// </summary>
    /// <param name="worker">The worker that has already been started.</param>
    /// <returns>A task representing completion of background execution.</returns>
    private static async Task AwaitExecutionAsync(SimulationWorker worker)
    {
        var executionTask = worker.ExecuteTask;
        Assert.NotNull(executionTask);

        await executionTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Creates a valid plan with no additional scenarios.
    /// </summary>
    /// <returns>A normal-operation simulation plan.</returns>
    private static SimulationPlanDefinition CreateValidPlan()
    {
        return new SimulationPlanDefinition(
            "NormalOperation", 42, Array.Empty<ScenarioDefinition>());
    }

    /// <summary>
    /// Creates an applied synchronization response with distinct counts
    /// so incorrect structured log arguments can be detected.
    /// </summary>
    /// <returns>A successful catalog synchronization response.</returns>
    private static DeviceCatalogSynchronizationResponse CreateSuccessfulSynchronizationResponse()
    {
        return new DeviceCatalogSynchronizationResponse(
            new DeviceCatalogValidationResponse(
                true, Array.Empty<DeviceCatalogValidationErrorResponse>()),
            1, 2, 3, 4, 5, true, true);
    }

    /// <summary>
    /// Creates two devices for successful startup preparation.
    /// </summary>
    /// <returns>A nonempty device list supplied by the test API client.</returns>
    private static IReadOnlyList<SimulatorDeviceResponse> CreateAvailableDevices()
    {
        return new SimulatorDeviceResponse[]
        {
            new(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "WH-001",
                "North Temperature Sensor",
                new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity },
                SimulatorDeviceLifecycle.Registered),
            new(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "WH-002",
                "Entrance Controller",
                new[] { SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.LightState },
                SimulatorDeviceLifecycle.Active)
        };
    }

    /// <summary>
    /// Supplies startup, heartbeat, and telemetry outcomes without HTTP.
    /// Records requests and signals their arrival without polling.
    /// </summary>
    private sealed class StubDeviceSimulatorApiClient : IDeviceSimulatorApiClient
    {
        private readonly Func<CancellationToken, Task<DeviceCatalogSynchronizationResponse>> _synchronize;
        private readonly Func<CancellationToken, Task<IReadOnlyList<SimulatorDeviceResponse>>> _getDevices;
        /// <summary>
        /// Stores the activation behavior supplied by the test.
        /// </summary>
        private readonly Func<Guid, CancellationToken, Task<SimulatorDeviceActivationResponse>> _activate;
        private readonly Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>> _heartbeat;
        private readonly ConcurrentQueue<HeartbeatRequest> _heartbeatRequests = new();
        private readonly Channel<HeartbeatRequest> _heartbeatNotifications = Channel.CreateUnbounded<HeartbeatRequest>();
        private readonly Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken,
            Task<SimulatorTelemetryRecordingResponse>> _telemetry;
        private readonly ConcurrentQueue<TelemetryRequest> _telemetryRequests = new();
        private readonly Channel<TelemetryRequest> _telemetryNotifications = Channel.CreateUnbounded<TelemetryRequest>();

        /// <summary>
        /// Captures activation requests dispatched by background execution.
        /// </summary>
        private readonly ConcurrentQueue<ActivationRequest> _activationRequests = new();

        /// <summary>
        /// Initializes an API substitute with optional startup operations.
        /// </summary>
        /// <param name="synchronize">
        /// The operation, or null to return a successful response immediately.
        /// </param>
        /// <param name="getDevices">
        /// The retrieval operation, or null to return two devices immediately.
        /// </param>
        /// <param name="activate">
        /// The activation operation, or null to return an active lifecycle immediately.
        /// </param>
        /// <param name="heartbeat">
        /// The heartbeat operation, or null to keep requests pending until cancellation.
        /// </param>
        /// <param name="telemetry">
        /// The telemetry operation, or null to keep requests pending until cancellation.
        /// </param>
        public StubDeviceSimulatorApiClient(
            Func<CancellationToken, Task<DeviceCatalogSynchronizationResponse>>? synchronize = null,
            Func<CancellationToken, Task<IReadOnlyList<SimulatorDeviceResponse>>>? getDevices = null,
            Func<Guid, CancellationToken, Task<SimulatorDeviceActivationResponse>>? activate = null,
            Func<Guid, CancellationToken, Task<SimulatorDeviceHeartbeatResponse>>? heartbeat = null,
            Func<Guid, SimulatorTelemetryMeasurementRequest, CancellationToken,
                Task<SimulatorTelemetryRecordingResponse>>? telemetry = null)
        {
            _synchronize = synchronize
                ?? (_ => Task.FromResult(CreateSuccessfulSynchronizationResponse()));
            _getDevices = getDevices ?? (_ => Task.FromResult(CreateAvailableDevices()));
            _activate = activate ?? ((deviceId, _) =>
                Task.FromResult(CreateSuccessfulActivationResponse(deviceId)));
            _heartbeat = heartbeat ?? WaitForHeartbeatCancellationAsync;
            _telemetry = telemetry ?? ((_, measurement, token) =>
                WaitForTelemetryCancellationAsync(measurement, token));
        }

        /// <summary>
        /// Gets the number of synchronization requests.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Gets the cancellation token supplied to the latest synchronization request.
        /// </summary>
        public CancellationToken LastCancellationToken { get; private set; }

        /// <summary>
        /// Gets the number of device retrieval requests.
        /// </summary>
        public int GetDevicesCallCount { get; private set; }

        /// <summary>
        /// Gets the cancellation token supplied to the latest retrieval request.
        /// </summary>
        public CancellationToken LastGetDevicesCancellationToken { get; private set; }

        /// <summary>
        /// Gets a snapshot of activation requests in dispatch order.
        /// </summary>
        public IReadOnlyList<ActivationRequest> ActivationRequests => _activationRequests.ToArray();

        /// <summary>Gets a snapshot of dispatched heartbeat requests.</summary>
        public IReadOnlyList<HeartbeatRequest> HeartbeatRequests => _heartbeatRequests.ToArray();

        /// <summary>Gets a snapshot of dispatched telemetry requests.</summary>
        public IReadOnlyList<TelemetryRequest> TelemetryRequests => _telemetryRequests.ToArray();

        /// <summary>Waits for one request per device from each runtime group.</summary>
        /// <param name="count">The number of devices expected in each group.</param>
        /// <returns>A task completed after both groups have dispatched their requests.</returns>
        public async Task WaitForRuntimeRequestsAsync(int count)
        {
            await Task.WhenAll(
                WaitForHeartbeatRequestsAsync(count),
                WaitForTelemetryRequestsAsync(count));
        }

        /// <summary>Waits for new telemetry request notifications without polling.</summary>
        /// <param name="count">The number of request notifications to observe.</param>
        /// <returns>A task representing observation of all requested notifications.</returns>
        public async Task WaitForTelemetryRequestsAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                await _telemetryNotifications.Reader.ReadAsync(TestContext.Current.CancellationToken)
                    .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            }
        }

        /// <summary>Waits for a specified number of new heartbeat requests without polling.</summary>
        /// <param name="count">The number of request notifications to observe.</param>
        /// <returns>A task representing observation of all requested notifications.</returns>
        public async Task WaitForHeartbeatRequestsAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                await _heartbeatNotifications.Reader.ReadAsync(TestContext.Current.CancellationToken)
                    .AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            }
        }

        /// <inheritdoc />
        public Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCancellationToken = cancellationToken;
            return _synchronize(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            GetDevicesCallCount++;
            LastGetDevicesCancellationToken = cancellationToken;
            return _getDevices(cancellationToken);
        }

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(
            Guid deviceId,
            CancellationToken cancellationToken = default)
        {
            _activationRequests.Enqueue(new ActivationRequest(deviceId, cancellationToken));
            return _activate(deviceId, cancellationToken);
        }

        /// <inheritdoc />
        public Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(
            Guid deviceId,
            SimulatorTelemetryMeasurementRequest request,
            CancellationToken cancellationToken = default)
        {
            var recorded = new TelemetryRequest(deviceId, request, cancellationToken);
            _telemetryRequests.Enqueue(recorded);
            _telemetryNotifications.Writer.TryWrite(recorded);
            return _telemetry(deviceId, request, cancellationToken);
        }

        /// <inheritdoc />
        public Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(
            Guid deviceId,
            CancellationToken cancellationToken = default)
        {
            var request = new HeartbeatRequest(deviceId, cancellationToken);
            _heartbeatRequests.Enqueue(request);
            _heartbeatNotifications.Writer.TryWrite(request);
            return _heartbeat(deviceId, cancellationToken);
        }
    }

    /// <summary>
    /// Keeps a heartbeat pending until the worker stops, making normal startup
    /// tests explicitly exercise cooperative cancellation of runtime work.
    /// </summary>
    /// <param name="deviceId">The device associated with the pending request.</param>
    /// <param name="cancellationToken">The worker's stopping token.</param>
    /// <returns>A task canceled when the worker stops.</returns>
    private static async Task<SimulatorDeviceHeartbeatResponse> WaitForHeartbeatCancellationAsync(
        Guid deviceId, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new SimulatorDeviceHeartbeatResponse(
            deviceId, new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc), true);
    }

    /// <summary>Keeps a telemetry request pending until the worker stops.</summary>
    /// <param name="measurement">The measurement supplied to the pending request.</param>
    /// <param name="cancellationToken">The worker's stopping token.</param>
    /// <returns>A task canceled when the worker stops.</returns>
    private static async Task<SimulatorTelemetryRecordingResponse> WaitForTelemetryCancellationAsync(
        SimulatorTelemetryMeasurementRequest measurement, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new SimulatorTelemetryRecordingResponse(measurement.MeasurementId, true);
    }

    /// <summary>Captures one dispatched telemetry request.</summary>
    /// <param name="DeviceId">The device that produced the measurement.</param>
    /// <param name="Measurement">The submitted measurement.</param>
    /// <param name="CancellationToken">The token passed to the request.</param>
    private sealed record TelemetryRequest(
        Guid DeviceId, SimulatorTelemetryMeasurementRequest Measurement,
        CancellationToken CancellationToken);

    /// <summary>Captures one dispatched heartbeat request.</summary>
    /// <param name="DeviceId">The device requested for heartbeat recording.</param>
    /// <param name="CancellationToken">The token passed to the request.</param>
    private sealed record HeartbeatRequest(Guid DeviceId, CancellationToken CancellationToken);

    /// <summary>
    /// Captures one activation dispatched during device preparation.
    /// </summary>
    /// <param name="DeviceId">The device requested for activation.</param>
    /// <param name="CancellationToken">The token passed to the request.</param>
    private sealed record ActivationRequest(Guid DeviceId, CancellationToken CancellationToken);

    /// <summary>
    /// Supplies deterministic read outcomes without using the file system.
    /// </summary>
    private sealed class StubSimulationPlanReader : ISimulationPlanReader
    {
        private readonly Func<string, CancellationToken, Task<SimulationPlanDefinition>> _read;

        /// <summary>
        /// Initializes a reader with an optional custom read operation.
        /// </summary>
        /// <param name="read">
        /// The read operation, or null to return a valid plan immediately.
        /// </param>
        public StubSimulationPlanReader(
            Func<string, CancellationToken, Task<SimulationPlanDefinition>>? read = null)
        {
            _read = read ?? ((_, _) => Task.FromResult(CreateValidPlan()));
        }

        /// <summary>
        /// Gets the number of read requests.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Gets the name supplied to the latest read request.
        /// </summary>
        public string? LastPlanName { get; private set; }

        /// <summary>
        /// Gets the cancellation token supplied to the latest read request.
        /// </summary>
        public CancellationToken LastCancellationToken { get; private set; }

        /// <inheritdoc />
        public Task<SimulationPlanDefinition> ReadAsync(
            string planName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPlanName = planName;
            LastCancellationToken = cancellationToken;
            return _read(planName, cancellationToken);
        }
    }

    /// <summary>
    /// Records application shutdown requests without starting a real host.
    /// </summary>
    private sealed class RecordingApplicationLifetime : IHostApplicationLifetime
    {
        private readonly Action? _onStop;

        /// <summary>
        /// Initializes a lifetime recorder with an optional shutdown observer.
        /// </summary>
        /// <param name="onStop">
        /// The action used to capture state when shutdown is requested.
        /// </param>
        public RecordingApplicationLifetime(Action? onStop = null)
        {
            _onStop = onStop;
        }

        /// <summary>
        /// Gets the number of application shutdown requests.
        /// </summary>
        public int StopApplicationCallCount { get; private set; }

        /// <inheritdoc />
        public CancellationToken ApplicationStarted => CancellationToken.None;

        /// <inheritdoc />
        public CancellationToken ApplicationStopping => CancellationToken.None;

        /// <inheritdoc />
        public CancellationToken ApplicationStopped => CancellationToken.None;

        /// <inheritdoc />
        public void StopApplication()
        {
            StopApplicationCallCount++;
            _onStop?.Invoke();
        }
    }

    /// <summary>
    /// Captures worker logs and structured properties across background tasks.
    /// </summary>
    private sealed class RecordingLogger : ILogger<SimulationWorker>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        /// <summary>
        /// Gets a snapshot of all recorded log entries.
        /// </summary>
        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();

            _entries.Enqueue(new LogEntry(
                logLevel, formatter(state, exception), exception, properties));
        }
    }

    /// <summary>
    /// Represents one captured worker log entry.
    /// </summary>
    /// <param name="Level">The severity of the entry.</param>
    /// <param name="Message">The formatted log message.</param>
    /// <param name="Exception">The original exception, when supplied.</param>
    /// <param name="Properties">The structured log properties.</param>
    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);
}
