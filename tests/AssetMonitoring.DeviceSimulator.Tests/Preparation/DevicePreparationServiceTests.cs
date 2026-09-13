using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Preparation;
using System.Net;
using System.Text.Json;

namespace AssetMonitoring.DeviceSimulator.Tests.Preparation;

/// <summary>
/// Verifies device selection, activation, input preservation, cancellation,
/// and interruption of preparation after a failure without HTTP requests.
/// </summary>
public sealed class DevicePreparationServiceTests
{
    /// <summary>
    /// Bounds asynchronous coordination so a cancellation regression fails
    /// instead of leaving a test waiting indefinitely.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that an API client is required when constructing the service.
    /// </summary>
    [Fact]
    public void ConstructorWithNullApiClientThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DevicePreparationService(null!));

        Assert.Equal("devices", exception.ParamName);
    }

    /// <summary>
    /// Verifies that a missing input collection is rejected before activation.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWithNullDevicesThrowsArgumentNullException()
    {
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.PrepareAsync(null!, TestContext.Current.CancellationToken));

        Assert.Equal("devices", exception.ParamName);
        Assert.Empty(client.ActivationRequests);
    }

    /// <summary>
    /// Verifies that an empty input produces an empty result without activation.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWithEmptyDevicesReturnsEmptyCollection()
    {
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);

        var result = await service.PrepareAsync(
            Array.Empty<SimulatorDeviceResponse>(),
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Empty(client.ActivationRequests);
    }

    /// <summary>
    /// Verifies that retired devices are excluded without activation requests.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWithRetiredDevicesReturnsEmptyCollection()
    {
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Retired),
            CreateDevice(2, SimulatorDeviceLifecycle.Retired)
        };

        var result = await service.PrepareAsync(
            devices, TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Empty(client.ActivationRequests);
        Assert.All(devices, device =>
            Assert.Equal(SimulatorDeviceLifecycle.Retired, device.Lifecycle));
    }

    /// <summary>
    /// Verifies that active devices retain their data and order without
    /// requesting activation again.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWithActiveDevicesPreservesDevicesWithoutActivation()
    {
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Active),
            CreateDevice(2, SimulatorDeviceLifecycle.Active)
        };

        var result = await service.PrepareAsync(
            devices, TestContext.Current.CancellationToken);

        Assert.Equal(devices, result.ToArray());
        Assert.Empty(client.ActivationRequests);
    }

    /// <summary>
    /// Verifies that successful activation produces an active copy while
    /// preserving input data, including when activation was already applied.
    /// </summary>
    /// <param name="changed">
    /// Whether the API reports a lifecycle change during this request.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareAsyncWithRegisteredDeviceReturnsActiveCopy(bool changed)
    {
        var client = new StubDeviceSimulatorApiClient((deviceId, _) =>
            Task.FromResult(CreateActivationResponse(deviceId, changed)));
        var service = new DevicePreparationService(client);
        var device = CreateDevice(1, SimulatorDeviceLifecycle.Registered);
        var devices = new[] { device };
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await service.PrepareAsync(devices, cancellationToken);

        var preparedDevice = Assert.Single(result);
        var request = Assert.Single(client.ActivationRequests);
        Assert.Equal(device.Id, request.DeviceId);
        Assert.Equal(cancellationToken, request.CancellationToken);
        Assert.Equal(SimulatorDeviceLifecycle.Active, preparedDevice.Lifecycle);
        Assert.Equal(device.Id, preparedDevice.Id);
        Assert.Equal(device.Code, preparedDevice.Code);
        Assert.Equal(device.Name, preparedDevice.Name);
        Assert.Equal(device.Capabilities.ToArray(), preparedDevice.Capabilities.ToArray());
        Assert.NotSame(device, preparedDevice);
        Assert.Same(device, devices[0]);
        Assert.Equal(SimulatorDeviceLifecycle.Registered, device.Lifecycle);
    }

    /// <summary>
    /// Verifies that mixed input retains eligible devices in input order
    /// and activates only registered devices without replacing input entries.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWithMixedLifecyclesReturnsEligibleDevicesInOrder()
    {
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Retired),
            CreateDevice(2, SimulatorDeviceLifecycle.Registered),
            CreateDevice(3, SimulatorDeviceLifecycle.Active),
            CreateDevice(4, SimulatorDeviceLifecycle.Retired),
            CreateDevice(5, SimulatorDeviceLifecycle.Registered)
        };
        var originalEntries = devices.ToArray();
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await service.PrepareAsync(devices, cancellationToken);

        Assert.Equal(
            new[] { devices[1].Id, devices[2].Id, devices[4].Id },
            result.Select(device => device.Id).ToArray());
        Assert.All(result, device =>
            Assert.Equal(SimulatorDeviceLifecycle.Active, device.Lifecycle));
        Assert.Equal(
            new[] { devices[1].Id, devices[4].Id },
            client.ActivationRequests.Select(request => request.DeviceId).ToArray());
        Assert.All(client.ActivationRequests, request =>
            Assert.Equal(cancellationToken, request.CancellationToken));
        Assert.Equal(SimulatorDeviceLifecycle.Registered, devices[1].Lifecycle);
        Assert.Equal(SimulatorDeviceLifecycle.Registered, devices[4].Lifecycle);
        for (var index = 0; index < devices.Length; index++)
        {
            Assert.Same(originalEntries[index], devices[index]);
        }
    }

    /// <summary>
    /// Verifies that an unsupported lifecycle fails preparation instead of
    /// activating the invalid device or processing a later registered device.
    /// </summary>
    /// <param name="lifecycleValue">An undefined lifecycle enum value.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public async Task PrepareAsyncWithUnsupportedLifecycleThrowsInvalidOperationException(
        int lifecycleValue)
    {
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);
        var devices = new[]
        {
            CreateDevice(1, (SimulatorDeviceLifecycle)lifecycleValue),
            CreateDevice(2, SimulatorDeviceLifecycle.Registered)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(devices, TestContext.Current.CancellationToken));

        Assert.Empty(client.ActivationRequests);
    }

    /// <summary>
    /// Verifies that failure on the second activation is propagated unchanged
    /// and prevents a third request after the first activation succeeded.
    /// </summary>
    /// <param name="failureKind">The activation failure to simulate.</param>
    [Theory]
    [InlineData("Http")]
    [InlineData("Json")]
    [InlineData("Cancellation")]
    [InlineData("Unexpected")]
    public async Task PrepareAsyncWhenActivationFailsStopsFurtherRequests(
        string failureKind)
    {
        var failure = CreateFailure(failureKind);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Registered),
            CreateDevice(2, SimulatorDeviceLifecycle.Registered),
            CreateDevice(3, SimulatorDeviceLifecycle.Registered)
        };
        var completedActivations = new List<Guid>();
        var client = new StubDeviceSimulatorApiClient((deviceId, _) =>
        {
            if (deviceId == devices[1].Id)
            {
                return Task.FromException<SimulatorDeviceActivationResponse>(failure);
            }

            completedActivations.Add(deviceId);
            return Task.FromResult(CreateActivationResponse(deviceId));
        });
        var service = new DevicePreparationService(client);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.PrepareAsync(devices, TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        Assert.Equal(devices[0].Id, Assert.Single(completedActivations));
        Assert.Equal(
            new[] { devices[0].Id, devices[1].Id },
            client.ActivationRequests.Select(request => request.DeviceId).ToArray());
        Assert.All(devices, device =>
            Assert.Equal(SimulatorDeviceLifecycle.Registered, device.Lifecycle));
    }

    /// <summary>
    /// Verifies that the next activation waits for the current request to
    /// complete and that preparation does not return while activation is pending.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWaitsForActivationBeforeStartingNextDevice()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var firstStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource<SimulatorDeviceActivationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Registered),
            CreateDevice(2, SimulatorDeviceLifecycle.Registered)
        };
        var client = new StubDeviceSimulatorApiClient((deviceId, token) =>
        {
            if (deviceId == devices[0].Id)
            {
                firstStarted.TrySetResult(true);
                return firstResponse.Task.WaitAsync(token);
            }

            return Task.FromResult(CreateActivationResponse(deviceId));
        });
        var service = new DevicePreparationService(client);
        var preparation = service.PrepareAsync(devices, cancellation.Token);

        try
        {
            await firstStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(preparation.IsCompleted);
            Assert.Equal(devices[0].Id, Assert.Single(client.ActivationRequests).DeviceId);

            firstResponse.SetResult(CreateActivationResponse(devices[0].Id));
            var result = await preparation.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(devices.Select(device => device.Id).ToArray(),
                result.Select(device => device.Id).ToArray());
            Assert.All(result, device =>
                Assert.Equal(SimulatorDeviceLifecycle.Active, device.Lifecycle));
            Assert.Equal(devices.Select(device => device.Id).ToArray(),
                client.ActivationRequests.Select(request => request.DeviceId).ToArray());
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(preparation);
        }
    }

    /// <summary>
    /// Verifies cancellation before processing a nonempty collection,
    /// including paths that would not otherwise make an HTTP request.
    /// </summary>
    /// <param name="lifecycle">The lifecycle of the unprocessed device.</param>
    [Theory]
    [InlineData(SimulatorDeviceLifecycle.Retired)]
    [InlineData(SimulatorDeviceLifecycle.Active)]
    [InlineData(SimulatorDeviceLifecycle.Registered)]
    public async Task PrepareAsyncWithCanceledTokenDoesNotProcessDevices(
        SimulatorDeviceLifecycle lifecycle)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var client = new StubDeviceSimulatorApiClient();
        var service = new DevicePreparationService(client);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareAsync(new[] { CreateDevice(1, lifecycle) }, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(client.ActivationRequests);
    }

    /// <summary>
    /// Verifies that cancellation reaches an in-flight activation request
    /// and prevents the next device from being activated.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWhenActivationIsCanceledStopsPreparingDevices()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var activationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Registered),
            CreateDevice(2, SimulatorDeviceLifecycle.Registered)
        };
        var client = new StubDeviceSimulatorApiClient(async (deviceId, token) =>
        {
            activationStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateActivationResponse(deviceId);
        });
        var service = new DevicePreparationService(client);
        var preparation = service.PrepareAsync(devices, cancellation.Token);

        try
        {
            await activationStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                preparation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            var request = Assert.Single(client.ActivationRequests);
            Assert.Equal(devices[0].Id, request.DeviceId);
            Assert.Equal(cancellation.Token, request.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(preparation);
        }
    }

    /// <summary>
    /// Verifies that cancellation observed after one successful activation
    /// prevents a new activation from being dispatched.
    /// </summary>
    [Fact]
    public async Task PrepareAsyncWhenCanceledBetweenDevicesDoesNotActivateNextDevice()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var devices = new[]
        {
            CreateDevice(1, SimulatorDeviceLifecycle.Registered),
            CreateDevice(2, SimulatorDeviceLifecycle.Registered)
        };
        var client = new StubDeviceSimulatorApiClient((deviceId, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(CreateActivationResponse(deviceId));
        });
        var service = new DevicePreparationService(client);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareAsync(devices, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(devices[0].Id, Assert.Single(client.ActivationRequests).DeviceId);
        Assert.All(devices, device =>
            Assert.Equal(SimulatorDeviceLifecycle.Registered, device.Lifecycle));
    }

    /// <summary>
    /// Creates a device with deterministic identity and descriptive metadata.
    /// </summary>
    /// <param name="number">The numeric suffix used to distinguish devices.</param>
    /// <param name="lifecycle">The initial lifecycle of the device.</param>
    /// <returns>A device fixture with temperature and humidity capabilities.</returns>
    private static SimulatorDeviceResponse CreateDevice(
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
    /// Creates the successful response guaranteed by the activation client.
    /// </summary>
    /// <param name="deviceId">The identifier of the activated device.</param>
    /// <param name="changed">Whether activation changed the lifecycle.</param>
    /// <returns>An activation response with an active lifecycle.</returns>
    private static SimulatorDeviceActivationResponse CreateActivationResponse(
        Guid deviceId, bool changed = true)
    {
        return new SimulatorDeviceActivationResponse(
            deviceId, SimulatorDeviceLifecycle.Active, changed);
    }

    /// <summary>
    /// Creates expected client failures and an unexpected failure for
    /// verifying unchanged exception propagation.
    /// </summary>
    /// <param name="failureKind">The kind of failure to create.</param>
    /// <returns>The exception instance supplied to the API substitute.</returns>
    private static Exception CreateFailure(string failureKind)
    {
        return failureKind switch
        {
            "Http" => new HttpRequestException(
                "Device cannot be activated.", null, HttpStatusCode.Conflict),
            "Json" => new JsonException("The API returned an invalid activation response."),
            "Cancellation" => new OperationCanceledException("Activation request timed out."),
            "Unexpected" => new InvalidOperationException("Unexpected client failure."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null)
        };
    }

    /// <summary>
    /// Observes a preparation task during cleanup and accepts cancellation.
    /// Other failures remain visible and waiting is bounded.
    /// </summary>
    /// <param name="preparation">The task started by an asynchronous test.</param>
    /// <returns>A task representing bounded cleanup.</returns>
    private static async Task ObserveCancellationAsync(Task preparation)
    {
        try
        {
            await preparation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the intended cleanup result for pending work.
        }
    }

    /// <summary>
    /// Supplies controlled activation outcomes and records dispatched requests.
    /// Catalog synchronization and retrieval are deliberately unsupported.
    /// </summary>
    private sealed class StubDeviceSimulatorApiClient : IDeviceSimulatorApiClient
    {
        /// <summary>
        /// Stores the activation behavior configured by the test.
        /// </summary>
        private readonly Func<Guid, CancellationToken, Task<SimulatorDeviceActivationResponse>> _activate;

        /// <summary>
        /// Stores activation requests in dispatch order.
        /// </summary>
        private readonly List<ActivationRequest> _activationRequests = new();

        /// <summary>
        /// Initializes an API substitute with optional activation behavior.
        /// </summary>
        /// <param name="activate">
        /// The operation, or null to return a successful activation immediately.
        /// </param>
        public StubDeviceSimulatorApiClient(
            Func<Guid, CancellationToken, Task<SimulatorDeviceActivationResponse>>? activate = null)
        {
            _activate = activate ?? ((deviceId, _) =>
                Task.FromResult(CreateActivationResponse(deviceId)));
        }

        /// <summary>
        /// Gets activation requests in the order they were dispatched.
        /// </summary>
        public IReadOnlyList<ActivationRequest> ActivationRequests => _activationRequests;

        /// <inheritdoc />
        public Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(
            Guid deviceId, CancellationToken cancellationToken = default)
        {
            _activationRequests.Add(new ActivationRequest(deviceId, cancellationToken));
            return _activate(deviceId, cancellationToken);
        }

        /// <inheritdoc />
        public Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(
            Guid deviceId,
            SimulatorTelemetryMeasurementRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Device preparation must not send telemetry.");

        /// <inheritdoc />
        public Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(
            Guid deviceId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "Device preparation must not send heartbeats.");
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "Preparation must use the supplied device collection.");
        }

        /// <inheritdoc />
        public Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "Catalog synchronization belongs to an earlier startup stage.");
        }
    }

    /// <summary>
    /// Captures one activation request supplied to the API substitute.
    /// </summary>
    /// <param name="DeviceId">The identifier requested for activation.</param>
    /// <param name="CancellationToken">The token passed to the request.</param>
    private sealed record ActivationRequest(
        Guid DeviceId, CancellationToken CancellationToken);
}
