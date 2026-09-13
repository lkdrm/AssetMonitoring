using AssetMonitoring.DeviceSimulator.Api;
using AssetMonitoring.DeviceSimulator.Api.Contracts;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AssetMonitoring.DeviceSimulator.Tests.Api;

/// <summary>
/// Verifies catalog synchronization, device retrieval, activation, heartbeats, telemetry,
/// JSON mapping, HTTP failures, cancellation, and response disposal without networking.
/// </summary>
public sealed class DeviceSimulatorApiClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Identifies the device used in activation requests and response fixtures.
    /// </summary>
    private const string ActivationDeviceIdText = "d5328b8a-101b-4f99-a1fd-b0bd387a9e59";

    /// <summary>
    /// Identifies a different device used for heartbeat request fixtures.
    /// </summary>
    private const string HeartbeatDeviceIdText = "2a48790f-190d-4e6b-b370-71b0ba734f3f";

    /// <summary>
    /// Supplies a server UTC timestamp with the full fractional precision
    /// supported by DateTime, independently of the test machine clock.
    /// </summary>
    private const string HeartbeatUtcTimestampText = "2026-09-10T12:34:56.1234567Z";

    /// <summary>
    /// Identifies a telemetry measurement independently of the device identifier.
    /// </summary>
    private const string TelemetryMeasurementIdText = "44444444-4444-4444-4444-444444444444";

    /// <summary>
    /// Supplies an API-shaped response for a newly persisted measurement.
    /// </summary>
    private const string RecordedTelemetryJson =
        "{\"measurementId\":\"" + TelemetryMeasurementIdText + "\",\"recorded\":true}";

    /// <summary>
    /// Provides an independently written wire response with distinct counts
    /// so incorrect JSON property mappings are observable.
    /// </summary>
    private const string ChangedCatalogJson = """
        {
          "validationResult": {
            "isValid": true,
            "errors": []
          },
          "created": 1,
          "updated": 2,
          "unchanged": 3,
          "retired": 4,
          "restored": 5,
          "applied": true,
          "hasChanges": true
        }
        """;

    /// <summary>
    /// Provides a successful synchronization response with no changes.
    /// </summary>
    private const string UnchangedCatalogJson = """
        {
          "validationResult": {
            "isValid": true,
            "errors": []
          },
          "created": 0,
          "updated": 0,
          "unchanged": 10,
          "retired": 0,
          "restored": 0,
          "applied": true,
          "hasChanges": false
        }
        """;

    /// <summary>
    /// Provides catalog validation failures, including an error with no
    /// associated device or property.
    /// </summary>
    private const string InvalidCatalogJson = """
        {
          "validationResult": {
            "isValid": false,
            "errors": [
              {
                "errorCode": "Device.Name.Required",
                "message": "Device name is required.",
                "deviceCode": "WH-001",
                "propertyName": "Name"
              },
              {
                "errorCode": "Catalog.Invalid",
                "message": "Catalog configuration is invalid.",
                "deviceCode": null,
                "propertyName": null
              }
            ]
          },
          "created": 0,
          "updated": 0,
          "unchanged": 0,
          "retired": 0,
          "restored": 0,
          "applied": false,
          "hasChanges": false
        }
        """;

    /// <summary>
    /// Provides API-shaped device JSON with every supported lifecycle and
    /// capability, plus additional fields that the simulator does not consume.
    /// </summary>
    private const string DevicesJson = """
        [
          {
            "id": "d5328b8a-101b-4f99-a1fd-b0bd387a9e59",
            "code": "WH-001",
            "name": "North Temperature Sensor",
            "hardwareModel": "ESP32",
            "hardwareRevision": "1.0",
            "firmwareVersion": "1.0.0",
            "location": "Warehouse North",
            "capabilities": ["Temperature", "Humidity"],
            "registeredAtUtc": "2026-08-12T10:37:46.6195641Z",
            "lifecycle": "Registered",
            "retiredAtUtc": null,
            "lastHeartbeatAtUtc": null,
            "connectivityStatus": "NeverConnected"
          },
          {
            "id": "22222222-2222-2222-2222-222222222222",
            "code": "WH-002",
            "name": "Entrance Controller",
            "capabilities": ["DoorState", "LightState"],
            "lifecycle": "Active"
          },
          {
            "id": "33333333-3333-3333-3333-333333333333",
            "code": "WH-003",
            "name": "Retired Humidity Sensor",
            "capabilities": ["Humidity"],
            "lifecycle": "Retired"
          }
        ]
        """;

    /// <summary>
    /// Verifies the HTTP method, relative route, absence of a request body,
    /// mapping of all successful response fields, and response disposal.
    /// </summary>
    [Fact]
    public async Task SynchronizeDeviceCatalogAsyncWithSuccessPostsToCatalogEndpointAndMapsResponse()
    {
        using var content = new TrackingJsonContent(ChangedCatalogJson);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.SynchronizeDeviceCatalogAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            new Uri("https://simulator-api.test/warehouse/api/device-catalog/synchronize"),
            handler.LastRequestUri);
        Assert.False(handler.LastRequestHadContent);
        Assert.True(result.Applied);
        Assert.True(result.HasChanges);
        Assert.True(result.ValidationResult.IsValid);
        Assert.Empty(result.ValidationResult.Errors);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, result.Updated);
        Assert.Equal(3, result.Unchanged);
        Assert.Equal(4, result.Retired);
        Assert.Equal(5, result.Restored);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that successful synchronization with no changes remains an
    /// applied result rather than being interpreted as failure.
    /// </summary>
    [Fact]
    public async Task SynchronizeDeviceCatalogAsyncWithUnchangedCatalogReturnsAppliedResult()
    {
        using var content = new TrackingJsonContent(UnchangedCatalogJson);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.SynchronizeDeviceCatalogAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        Assert.False(result.HasChanges);
        Assert.True(result.ValidationResult.IsValid);
        Assert.Empty(result.ValidationResult.Errors);
        Assert.Equal(10, result.Unchanged);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Retired);
        Assert.Equal(0, result.Restored);
    }

    /// <summary>
    /// Verifies that HTTP 400 preserves every catalog validation error,
    /// including nullable details, and releases the response content.
    /// </summary>
    [Fact]
    public async Task SynchronizeDeviceCatalogAsyncWithBadRequestReturnsValidationErrors()
    {
        using var content = new TrackingJsonContent(InvalidCatalogJson);
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.SynchronizeDeviceCatalogAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.False(result.Applied);
        Assert.False(result.HasChanges);
        Assert.False(result.ValidationResult.IsValid);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Retired);
        Assert.Equal(0, result.Restored);
        Assert.Collection(
            result.ValidationResult.Errors,
            error =>
            {
                Assert.Equal("Device.Name.Required", error.ErrorCode);
                Assert.Equal("Device name is required.", error.Message);
                Assert.Equal("WH-001", error.DeviceCode);
                Assert.Equal("Name", error.PropertyName);
            },
            error =>
            {
                Assert.Equal("Catalog.Invalid", error.ErrorCode);
                Assert.Equal("Catalog configuration is invalid.", error.Message);
                Assert.Null(error.DeviceCode);
                Assert.Null(error.PropertyName);
            });
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that other HTTP errors are reported before attempting to
    /// deserialize their non-JSON response bodies and still dispose content.
    /// </summary>
    /// <param name="statusCode">The unsuccessful status returned by the API.</param>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SynchronizeDeviceCatalogAsyncWithOtherHttpErrorThrowsBeforeJsonReading(
        HttpStatusCode statusCode)
    {
        using var content = new TrackingJsonContent("<html>Request failed.</html>");
        using var response = new HttpResponseMessage(statusCode) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SynchronizeDeviceCatalogAsync(TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that empty or malformed JSON is reported as a JSON failure
    /// and that response content is released on this exception path.
    /// </summary>
    /// <param name="json">The invalid response body.</param>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    public async Task SynchronizeDeviceCatalogAsyncWithInvalidJsonThrowsJsonException(
        string json)
    {
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.SynchronizeDeviceCatalogAsync(TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that a null result, a missing validation result, or a missing
    /// errors collection is rejected rather than returned to the worker.
    /// </summary>
    /// <param name="json">The incomplete JSON response.</param>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"validationResult\":{\"isValid\":true}}")]
    public async Task SynchronizeDeviceCatalogAsyncWithIncompleteResponseThrowsJsonException(
        string json)
    {
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.SynchronizeDeviceCatalogAsync(TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that HTTP 400 with a different response contract is rejected
    /// rather than treated as a catalog validation result.
    /// </summary>
    [Fact]
    public async Task SynchronizeDeviceCatalogAsyncWithBadRequestProblemDetailsRejectsResponse()
    {
        const string json = """
            {
              "title": "One or more validation errors occurred.",
              "status": 400,
              "errors": { "request": ["The request was invalid."] }
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.SynchronizeDeviceCatalogAsync(TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that a transport failure reaches the caller without being
    /// replaced by a successful result or an unrelated JSON failure.
    /// </summary>
    [Fact]
    public async Task SynchronizeDeviceCatalogAsyncWithTransportFailurePropagatesException()
    {
        var expected = new HttpRequestException("The API could not be reached.");
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(expected));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SynchronizeDeviceCatalogAsync(TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// Verifies that caller cancellation reaches an in-flight HTTP operation
    /// and remains cancellation when observed by the caller.
    /// </summary>
    [Fact]
    public async Task SynchronizeDeviceCatalogAsyncWithCancellationCancelsInFlightRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            requestStarted.TrySetResult(true);
            return await pendingResponse.Task.WaitAsync(token);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        try
        {
            var operation = client.SynchronizeDeviceCatalogAsync(cancellation.Token);
            await requestStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.True(operation.IsCanceled);
            Assert.True(handler.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            pendingResponse.TrySetCanceled(cancellation.Token);
        }
    }

    /// <summary>
    /// Verifies the GET route, mapping of each device and string enum,
    /// retention of retired devices, and response disposal.
    /// </summary>
    [Fact]
    public async Task GetDevicesAsyncWithSuccessReturnsAllDevicesAndMapsStringEnums()
    {
        using var content = new TrackingJsonContent(DevicesJson);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.GetDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(new Uri("https://simulator-api.test/warehouse/api/devices"), handler.LastRequestUri);
        Assert.False(handler.LastRequestHadContent);
        Assert.Collection(result,
            device =>
            {
                Assert.Equal(Guid.Parse("d5328b8a-101b-4f99-a1fd-b0bd387a9e59"), device.Id);
                Assert.Equal("WH-001", device.Code);
                Assert.Equal("North Temperature Sensor", device.Name);
                Assert.Equal(SimulatorDeviceLifecycle.Registered, device.Lifecycle);
                Assert.Collection(device.Capabilities,
                    capability => Assert.Equal(SimulatorDeviceCapability.Temperature, capability),
                    capability => Assert.Equal(SimulatorDeviceCapability.Humidity, capability));
            },
            device =>
            {
                Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), device.Id);
                Assert.Equal("WH-002", device.Code);
                Assert.Equal("Entrance Controller", device.Name);
                Assert.Equal(SimulatorDeviceLifecycle.Active, device.Lifecycle);
                Assert.Collection(device.Capabilities,
                    capability => Assert.Equal(SimulatorDeviceCapability.DoorState, capability),
                    capability => Assert.Equal(SimulatorDeviceCapability.LightState, capability));
            },
            device =>
            {
                Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), device.Id);
                Assert.Equal("WH-003", device.Code);
                Assert.Equal("Retired Humidity Sensor", device.Name);
                Assert.Equal(SimulatorDeviceLifecycle.Retired, device.Lifecycle);
                Assert.Equal(SimulatorDeviceCapability.Humidity, Assert.Single(device.Capabilities));
            });
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that an empty JSON array remains a valid empty device list.
    /// </summary>
    [Fact]
    public async Task GetDevicesAsyncWithEmptyArrayReturnsEmptyCollection()
    {
        using var content = new TrackingJsonContent("[]");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.GetDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that device retrieval rejects every tested HTTP error before
    /// reading JSON, including HTTP 400 and server failures.
    /// </summary>
    /// <param name="statusCode">The unsuccessful status returned by the API.</param>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetDevicesAsyncWithHttpErrorThrowsBeforeJsonReading(HttpStatusCode statusCode)
    {
        using var content = new TrackingJsonContent("<html>Request failed.</html>");
        using var response = new HttpResponseMessage(statusCode) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetDevicesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that empty, malformed, or object-shaped JSON cannot be
    /// returned as a device list, and that the response is still disposed.
    /// </summary>
    /// <param name="json">The invalid response body.</param>
    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("{}")]
    public async Task GetDevicesAsyncWithInvalidJsonThrowsJsonException(string json)
    {
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.GetDevicesAsync(TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that JSON null is rejected rather than silently converted
    /// into the valid empty-list response.
    /// </summary>
    [Fact]
    public async Task GetDevicesAsyncWithNullCollectionThrowsJsonException()
    {
        using var content = new TrackingJsonContent("null");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<JsonException>(() =>
            client.GetDevicesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("The API returned a null device collection.", exception.Message);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that unknown names and numeric JSON enum values are rejected
    /// for both capabilities and lifecycle.
    /// </summary>
    /// <param name="capabilityJson">The JSON value inserted into capabilities.</param>
    /// <param name="lifecycleJson">The JSON value inserted into lifecycle.</param>
    [Theory]
    [InlineData("\"UnknownCapability\"", "\"Registered\"")]
    [InlineData("\"Temperature\"", "\"UnknownLifecycle\"")]
    [InlineData("0", "\"Registered\"")]
    [InlineData("\"Temperature\"", "0")]
    public async Task GetDevicesAsyncWithInvalidEnumValueThrowsJsonException(
        string capabilityJson,
        string lifecycleJson)
    {
        var json = $$"""
            [
              {
                "id": "d5328b8a-101b-4f99-a1fd-b0bd387a9e59",
                "code": "WH-001",
                "name": "North Temperature Sensor",
                "capabilities": [{{capabilityJson}}],
                "lifecycle": {{lifecycleJson}}
              }
            ]
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.GetDevicesAsync(TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that a device retrieval transport failure reaches the caller
    /// without being replaced by an empty device list.
    /// </summary>
    [Fact]
    public async Task GetDevicesAsyncWithTransportFailurePropagatesException()
    {
        var expected = new HttpRequestException("The API could not be reached.");
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(expected));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetDevicesAsync(TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// Verifies that caller cancellation reaches an in-flight GET operation
    /// and is propagated instead of being converted into an empty list.
    /// </summary>
    [Fact]
    public async Task GetDevicesAsyncWithCancellationCancelsInFlightRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            requestStarted.TrySetResult(true);
            return await pendingResponse.Task.WaitAsync(token);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        try
        {
            var operation = client.GetDevicesAsync(cancellation.Token);
            await requestStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.True(operation.IsCanceled);
            Assert.True(handler.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            pendingResponse.TrySetCanceled(cancellation.Token);
        }
    }

    /// <summary>
    /// Verifies that an empty device identifier is rejected before an HTTP
    /// request is sent and that the invalid parameter is identified.
    /// </summary>
    [Fact]
    public async Task ActivateDeviceAsyncWithEmptyDeviceIdThrowsBeforeSendingRequest()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected."));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ActivateDeviceAsync(Guid.Empty, TestContext.Current.CancellationToken));

        Assert.Equal("deviceId", exception.ParamName);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// Verifies that activation uses the device-specific POST route without
    /// a body, maps the response, and accepts an unchanged active device.
    /// </summary>
    /// <param name="changed">Whether the API reports a lifecycle change.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActivateDeviceAsyncWithSuccessPostsToDeviceRouteAndReturnsResult(bool changed)
    {
        var deviceId = Guid.Parse(ActivationDeviceIdText);
        var changedJson = changed ? "true" : "false";
        var json = $$"""
            {
              "deviceId": "{{ActivationDeviceIdText}}",
              "lifecycle": "Active",
              "changed": {{changedJson}}
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.ActivateDeviceAsync(
            deviceId, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            new Uri($"https://simulator-api.test/warehouse/api/devices/{ActivationDeviceIdText}/activate"),
            handler.LastRequestUri);
        Assert.False(handler.LastRequestHadContent);
        Assert.Equal(deviceId, result.DeviceId);
        Assert.Equal(SimulatorDeviceLifecycle.Active, result.Lifecycle);
        Assert.Equal(changed, result.Changed);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that activation rejects unsuccessful HTTP statuses before
    /// reading JSON and preserves the response status in the exception.
    /// </summary>
    /// <param name="statusCode">The unsuccessful status returned by the API.</param>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ActivateDeviceAsyncWithHttpErrorThrowsBeforeJsonReading(HttpStatusCode statusCode)
    {
        using var content = new TrackingJsonContent("<html>Activation failed.</html>");
        using var response = new HttpResponseMessage(statusCode) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ActivateDeviceAsync(
                Guid.Parse(ActivationDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that empty, malformed, incorrectly shaped, null, or incomplete
    /// activation responses are rejected and their content is disposed.
    /// </summary>
    /// <param name="json">The invalid response body.</param>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task ActivateDeviceAsyncWithInvalidResponseThrowsJsonException(string json)
    {
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.ActivateDeviceAsync(
                Guid.Parse(ActivationDeviceIdText), TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that success is rejected when it names another device,
    /// reports a nonactive lifecycle, or uses an unsupported enum value.
    /// </summary>
    /// <param name="responseDeviceId">The device identifier in the response.</param>
    /// <param name="lifecycleJson">The JSON value supplied for the lifecycle.</param>
    [Theory]
    [InlineData("33333333-3333-3333-3333-333333333333", "\"Active\"")]
    [InlineData(ActivationDeviceIdText, "\"Registered\"")]
    [InlineData(ActivationDeviceIdText, "\"Retired\"")]
    [InlineData(ActivationDeviceIdText, "\"UnknownLifecycle\"")]
    [InlineData(ActivationDeviceIdText, "1")]
    public async Task ActivateDeviceAsyncWithInconsistentResponseThrowsJsonException(
        string responseDeviceId,
        string lifecycleJson)
    {
        var json = $$"""
            {
              "deviceId": "{{responseDeviceId}}",
              "lifecycle": {{lifecycleJson}},
              "changed": true
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.ActivateDeviceAsync(
                Guid.Parse(ActivationDeviceIdText), TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that an activation transport failure reaches the caller
    /// with the original exception preserved.
    /// </summary>
    [Fact]
    public async Task ActivateDeviceAsyncWithTransportFailurePropagatesException()
    {
        var expected = new HttpRequestException("The API could not be reached.");
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(expected));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ActivateDeviceAsync(
                Guid.Parse(ActivationDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// Verifies that caller cancellation reaches an in-flight activation
    /// request and remains cancellation when observed by the caller.
    /// </summary>
    [Fact]
    public async Task ActivateDeviceAsyncWithCancellationCancelsInFlightRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            requestStarted.TrySetResult(true);
            return await pendingResponse.Task.WaitAsync(token);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        try
        {
            var operation = client.ActivateDeviceAsync(
                Guid.Parse(ActivationDeviceIdText), cancellation.Token);
            await requestStarted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.True(operation.IsCanceled);
            Assert.True(handler.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            pendingResponse.TrySetCanceled(cancellation.Token);
        }
    }

    /// <summary>
    /// Verifies that an empty heartbeat device identifier is rejected before
    /// sending an HTTP request and that the invalid parameter is identified.
    /// </summary>
    [Fact]
    public async Task SendHeartbeatAsyncWithEmptyDeviceIdThrowsBeforeSendingRequest()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected."));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendHeartbeatAsync(Guid.Empty, TestContext.Current.CancellationToken));

        Assert.Equal("deviceId", exception.ParamName);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// Verifies the heartbeat POST route without a body, exact server timestamp
    /// mapping, UTC kind, response disposal, and acceptance of unchanged results.
    /// </summary>
    /// <param name="changed">Whether the API reports an updated heartbeat timestamp.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendHeartbeatAsyncWithSuccessPostsToDeviceRouteAndReturnsServerTimestamp(
        bool changed)
    {
        var deviceId = Guid.Parse(HeartbeatDeviceIdText);
        var changedJson = changed ? "true" : "false";
        var json = $$"""
            {
              "deviceId": "{{HeartbeatDeviceIdText}}",
              "lastHeartbeatAtUtc": "{{HeartbeatUtcTimestampText}}",
              "changed": {{changedJson}}
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.SendHeartbeatAsync(
            deviceId, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            new Uri($"https://simulator-api.test/warehouse/api/devices/{HeartbeatDeviceIdText}/heartbeat"),
            handler.LastRequestUri);
        Assert.False(handler.LastRequestHadContent);
        Assert.Equal(deviceId, result.DeviceId);
        var expectedTimestamp = new DateTime(
            2026, 9, 10, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
        Assert.Equal(expectedTimestamp, result.LastHeartbeatAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.LastHeartbeatAtUtc.Kind);
        Assert.Equal(changed, result.Changed);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies rejection of unsuccessful heartbeat statuses before attempting
    /// JSON deserialization, preserving the status code and disposing the response.
    /// </summary>
    /// <param name="statusCode">The unsuccessful HTTP status returned by the API.</param>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendHeartbeatAsyncWithHttpErrorThrowsBeforeJsonReading(
        HttpStatusCode statusCode)
    {
        using var content = new TrackingJsonContent("<html>Heartbeat rejected.</html>");
        using var response = new HttpResponseMessage(statusCode) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies rejection of empty, malformed, incorrectly shaped, null, and
    /// incomplete heartbeat bodies, including missing identity or timestamp.
    /// </summary>
    /// <param name="json">The invalid heartbeat response body.</param>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"lastHeartbeatAtUtc\":\"" + HeartbeatUtcTimestampText + "\",\"changed\":true}")]
    [InlineData("{\"deviceId\":\"" + HeartbeatDeviceIdText + "\",\"changed\":true}")]
    public async Task SendHeartbeatAsyncWithInvalidResponseThrowsJsonException(string json)
    {
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that a heartbeat response identifying another device or an
    /// empty identifier is rejected even when its timestamp is valid UTC.
    /// </summary>
    /// <param name="responseDeviceId">The inconsistent response device identifier.</param>
    [Theory]
    [InlineData("33333333-3333-3333-3333-333333333333")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task SendHeartbeatAsyncWithMismatchedDeviceIdThrowsJsonException(
        string responseDeviceId)
    {
        var json = $$"""
            {
              "deviceId": "{{responseDeviceId}}",
              "lastHeartbeatAtUtc": "{{HeartbeatUtcTimestampText}}",
              "changed": true
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<JsonException>(() =>
            client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Equal("The API returned an invalid heartbeat response.", exception.Message);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that valid date strings which deserialize with Unspecified
    /// or Local kind do not satisfy the UTC heartbeat response contract.
    /// </summary>
    /// <remarks>
    /// System.Text.Json uses Local kind for explicit numeric offsets, including
    /// a zero offset. See the
    /// <see href="https://source.dot.net/System.Text.Json/System/Text/Json/JsonHelpers.Date.cs.html">
    /// runtime date parser</see>.
    /// </remarks>
    /// <param name="timestamp">A timestamp that does not deserialize with UTC kind.</param>
    [Theory]
    [InlineData("2026-09-10T12:34:56.1234567")]
    [InlineData("2026-09-10T14:34:56.1234567+02:00")]
    [InlineData("2026-09-10T12:34:56.1234567+00:00")]
    public async Task SendHeartbeatAsyncWithNonUtcTimestampThrowsJsonException(string timestamp)
    {
        var json = $$"""
            {
              "deviceId": "{{HeartbeatDeviceIdText}}",
              "lastHeartbeatAtUtc": "{{timestamp}}",
              "changed": true
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<JsonException>(() =>
            client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Equal("The API returned an invalid heartbeat response.", exception.Message);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that malformed dates, impossible calendar dates, null values,
    /// and numeric timestamps cannot be read as heartbeat DateTime values.
    /// </summary>
    /// <param name="timestampJson">The raw JSON value supplied for the timestamp.</param>
    [Theory]
    [InlineData("\"not-a-date\"")]
    [InlineData("\"2026-02-30T12:34:56Z\"")]
    [InlineData("null")]
    [InlineData("123")]
    public async Task SendHeartbeatAsyncWithInvalidTimestampThrowsJsonException(
        string timestampJson)
    {
        var json = $$"""
            {
              "deviceId": "{{HeartbeatDeviceIdText}}",
              "lastHeartbeatAtUtc": {{timestampJson}},
              "changed": true
            }
            """;
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that heartbeat transport failures preserve the original
    /// exception and reach the caller after one request attempt.
    /// </summary>
    [Fact]
    public async Task SendHeartbeatAsyncWithTransportFailurePropagatesException()
    {
        var expected = new HttpRequestException("The API could not be reached.");
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(expected));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// Verifies that caller cancellation reaches an in-flight heartbeat
    /// request and is propagated instead of being swallowed.
    /// </summary>
    [Fact]
    public async Task SendHeartbeatAsyncWithCancellationCancelsInFlightRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            requestStarted.TrySetResult(true);
            return await pendingResponse.Task.WaitAsync(token);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        try
        {
            var operation = client.SendHeartbeatAsync(
                Guid.Parse(HeartbeatDeviceIdText), cancellation.Token);
            await requestStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.True(operation.IsCanceled);
            Assert.True(handler.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            pendingResponse.TrySetCanceled(cancellation.Token);
        }
    }

    /// <summary>
    /// Verifies that a null measurement is rejected before HTTP dispatch.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithNullRequestThrowsBeforeSendingRequest()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected."));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), null!, TestContext.Current.CancellationToken));

        Assert.Equal("request", exception.ParamName);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// Verifies that an empty device identifier is rejected before HTTP dispatch.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithEmptyDeviceIdThrowsBeforeSendingRequest()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected."));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendTelemetryAsync(
                Guid.Empty, CreateTelemetryRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("deviceId", exception.ParamName);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// Verifies POST routing, camel-case JSON fields, string metric names,
    /// numeric and boolean values, UTC timestamps, response mapping, and disposal.
    /// </summary>
    /// <param name="metric">The metric being sent.</param>
    /// <param name="expectedMetricName">The independently specified wire name.</param>
    /// <param name="numericValue">The numeric value, or null for a state metric.</param>
    /// <param name="stateValue">The boolean value, or null for a numeric metric.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature, "Temperature", 22.5, null)]
    [InlineData(SimulatorTelemetryMetric.Humidity, "Humidity", 80.5, null)]
    [InlineData(SimulatorTelemetryMetric.DoorState, "DoorState", null, true)]
    [InlineData(SimulatorTelemetryMetric.LightState, "LightState", null, false)]
    public async Task SendTelemetryAsyncWithValidMeasurementPostsExpectedJsonAndMapsCreatedResponse(
        SimulatorTelemetryMetric metric,
        string expectedMetricName,
        double? numericValue,
        bool? stateValue)
    {
        var measurement = CreateTelemetryRequest() with
        {
            Metric = metric,
            NumericValue = numericValue,
            StateValue = stateValue
        };
        string? requestJson = null;
        string? mediaType = null;
        using var content = new TrackingJsonContent(RecordedTelemetryJson);
        using var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = content };
        using var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            var body = request.Content ??
                throw new InvalidOperationException("The request must contain JSON.");
            mediaType = body.Headers.ContentType?.MediaType;
            requestJson = await body.ReadAsStringAsync(token);
            return response;
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var result = await client.SendTelemetryAsync(
            Guid.Parse(HeartbeatDeviceIdText), measurement, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            new Uri($"https://simulator-api.test/warehouse/api/devices/{HeartbeatDeviceIdText}/telemetry"),
            handler.LastRequestUri);
        Assert.True(handler.LastRequestHadContent);
        Assert.Equal("application/json", mediaType);
        Assert.NotNull(requestJson);
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        Assert.Equal(measurement.MeasurementId, root.GetProperty("measurementId").GetGuid());
        Assert.Equal(JsonValueKind.String, root.GetProperty("metric").ValueKind);
        Assert.Equal(expectedMetricName, root.GetProperty("metric").GetString());

        var numeric = root.GetProperty("numericValue");
        if (numericValue is { } expectedNumeric)
        {
            Assert.Equal(expectedNumeric, numeric.GetDouble());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, numeric.ValueKind);
        }

        var state = root.GetProperty("stateValue");
        if (stateValue is { } expectedState)
        {
            Assert.Equal(expectedState, state.GetBoolean());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, state.ValueKind);
        }

        var measuredAtUtc = root.GetProperty("measuredAtUtc").GetDateTime();
        Assert.Equal(measurement.MeasuredAtUtc, measuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, measuredAtUtc.Kind);
        Assert.Equal(measurement.MeasurementId, result.MeasurementId);
        Assert.True(result.Recorded);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that explicitly resending one measurement preserves the body
    /// and accepts HTTP 200 with Recorded false after an initial HTTP 201.
    /// This exercises the client contract, not server-side deduplication.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithRepeatedMeasurementPreservesPayloadAndAcceptsExistingResult()
    {
        var measurement = CreateTelemetryRequest();
        using var createdContent = new TrackingJsonContent(RecordedTelemetryJson);
        using var existingContent = new TrackingJsonContent(
            "{\"measurementId\":\"" + TelemetryMeasurementIdText + "\",\"recorded\":false}");
        using var createdResponse = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = createdContent
        };
        using var existingResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = existingContent
        };
        var bodies = new System.Collections.Generic.List<string>();
        using var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            var body = request.Content ??
                throw new InvalidOperationException("The request must contain JSON.");
            bodies.Add(await body.ReadAsStringAsync(token));
            return bodies.Count == 1 ? createdResponse : existingResponse;
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var first = await client.SendTelemetryAsync(
            Guid.Parse(HeartbeatDeviceIdText), measurement, TestContext.Current.CancellationToken);
        var second = await client.SendTelemetryAsync(
            Guid.Parse(HeartbeatDeviceIdText), measurement, TestContext.Current.CancellationToken);

        Assert.True(first.Recorded);
        Assert.False(second.Recorded);
        Assert.Equal(measurement.MeasurementId, first.MeasurementId);
        Assert.Equal(measurement.MeasurementId, second.MeasurementId);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.True(createdContent.IsDisposed);
        Assert.True(existingContent.IsDisposed);
    }

    /// <summary>
    /// Verifies that unsuccessful HTTP statuses are reported before attempting
    /// to deserialize the error body as a telemetry response.
    /// </summary>
    /// <param name="statusCode">The unsuccessful HTTP status.</param>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendTelemetryAsyncWithHttpErrorThrowsBeforeJsonReading(HttpStatusCode statusCode)
    {
        using var content = new TrackingJsonContent("<html>Telemetry rejected.</html>");
        using var response = new HttpResponseMessage(statusCode) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(),
                TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies rejection and disposal of malformed, null, incomplete, or
    /// incorrectly typed telemetry responses.
    /// </summary>
    /// <param name="json">The invalid response body.</param>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"recorded\":true}")]
    [InlineData("{\"measurementId\":\"not-a-guid\",\"recorded\":true}")]
    [InlineData("{\"measurementId\":\"" + TelemetryMeasurementIdText + "\",\"recorded\":\"true\"}")]
    public async Task SendTelemetryAsyncWithInvalidResponseThrowsJsonException(string json)
    {
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAsync<JsonException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.CallCount);
        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies that a response cannot acknowledge a different or empty
    /// measurement identifier, even when its HTTP status is successful.
    /// </summary>
    /// <param name="measurementId">The incorrect response identifier.</param>
    [Theory]
    [InlineData(HeartbeatDeviceIdText)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task SendTelemetryAsyncWithMismatchedMeasurementIdThrowsJsonException(string measurementId)
    {
        var json = "{\"measurementId\":\"" + measurementId + "\",\"recorded\":true}";
        using var content = new TrackingJsonContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = content };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAsync<JsonException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(),
                TestContext.Current.CancellationToken));

        Assert.True(content.IsDisposed);
    }

    /// <summary>
    /// Verifies propagation of the original transport exception after one
    /// attempt; retries belong to the later telemetry runner.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithTransportFailurePropagatesException()
    {
        var expected = new HttpRequestException("The API could not be reached.");
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(expected));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(),
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// Simulates a request timeout and verifies that it propagates even when
    /// the caller's token has not been canceled.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithRequestTimeoutPropagatesCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var timeout = new TaskCanceledException(
            "The request timed out.", new TimeoutException("The API response was too slow."));
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(timeout));
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(), cancellation.Token));

        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// Verifies that an already-canceled caller token produces cancellation.
    /// The handler also honors cancellation if HttpClient dispatches to it.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithAlreadyCanceledTokenPropagatesCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        using var handler = new StubHttpMessageHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The request token must be canceled.");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendTelemetryAsync(
                Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(), cancellation.Token));
    }

    /// <summary>
    /// Verifies that caller cancellation reaches a pending telemetry request
    /// and is propagated without waiting for an HTTP timeout.
    /// </summary>
    [Fact]
    public async Task SendTelemetryAsyncWithCancellationCancelsInFlightRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            requestStarted.TrySetResult(true);
            return await pendingResponse.Task.WaitAsync(token);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new DeviceSimulatorApiClient(httpClient);
        var operation = client.SendTelemetryAsync(
            Guid.Parse(HeartbeatDeviceIdText), CreateTelemetryRequest(), cancellation.Token);

        try
        {
            await requestStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.True(operation.IsCanceled);
            Assert.True(handler.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            pendingResponse.TrySetCanceled(cancellation.Token);
            try
            {
                await operation.WaitAsync(TestTimeout, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Observe cancellation before disposing resources used by the operation.
            }
        }
    }

    /// <summary>
    /// Creates a fixed telemetry measurement independently of the local clock.
    /// </summary>
    /// <returns>A temperature measurement with a stable ID and UTC timestamp.</returns>
    private static SimulatorTelemetryMeasurementRequest CreateTelemetryRequest()
    {
        return new SimulatorTelemetryMeasurementRequest(
            Guid.Parse(TelemetryMeasurementIdText),
            SimulatorTelemetryMetric.Temperature,
            22.5,
            null,
            new DateTime(2026, 9, 11, 10, 20, 30, DateTimeKind.Utc).AddTicks(1234567));
    }

    /// <summary>
    /// Creates an HTTP client whose requests are handled entirely in memory.
    /// The caller owns the handler and disposes it separately.
    /// </summary>
    /// <param name="handler">The handler providing controlled responses.</param>
    /// <returns>An HTTP client that must be disposed by its caller.</returns>
    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://simulator-api.test/warehouse/"),
            // Test waits are bounded separately so an HTTP timeout cannot
            // accidentally satisfy the caller-cancellation test.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Records outgoing request details and supplies configured responses
    /// without opening a network connection.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        /// <summary>
        /// Initializes the handler with the operation used to produce responses.
        /// </summary>
        /// <param name="send">The configured request handling operation.</param>
        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        /// <summary>
        /// Gets the number of outgoing requests.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Gets the HTTP method of the latest request.
        /// </summary>
        public HttpMethod? LastMethod { get; private set; }

        /// <summary>
        /// Gets the complete URI of the latest request.
        /// </summary>
        public Uri? LastRequestUri { get; private set; }

        /// <summary>
        /// Gets whether the latest request contained an HTTP content object.
        /// </summary>
        public bool LastRequestHadContent { get; private set; }

        /// <summary>
        /// Gets the cancellation token supplied by HttpClient to the handler.
        /// This may be a linked token rather than the original caller token.
        /// </summary>
        public CancellationToken LastCancellationToken { get; private set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastRequestHadContent = request.Content is not null;
            LastCancellationToken = cancellationToken;

            return _send(request, cancellationToken);
        }
    }

    /// <summary>
    /// Supplies UTF-8 JSON content and records whether the response owner
    /// disposed it before control returned to the test.
    /// </summary>
    private sealed class TrackingJsonContent : StringContent
    {
        /// <summary>
        /// Initializes tracked content with the supplied response body.
        /// </summary>
        /// <param name="json">The response body, which may be invalid JSON.</param>
        public TrackingJsonContent(string json)
            : base(json, Encoding.UTF8, "application/json")
        {
        }

        /// <summary>
        /// Gets whether this content has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
