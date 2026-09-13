using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetMonitoring.DeviceSimulator.Api;

/// <summary>
/// Provides HTTP communication with the Asset Monitoring API
/// for device simulation operations.
/// </summary>
public sealed class DeviceSimulatorApiClient : IDeviceSimulatorApiClient
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceSimulatorApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used to communicate with the Asset Monitoring API.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="httpClient"/> is null.
    /// </exception>
    public DeviceSimulatorApiClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync("api/device-catalog/synchronize", null, cancellationToken);

        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<DeviceCatalogSynchronizationResponse>(cancellationToken);

        if (result is null || result.ValidationResult is null || result.ValidationResult.Errors is null)
        {
            throw new JsonException("The API returned an incomplete catalog synchronization response.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/devices", cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<List<SimulatorDeviceResponse>>(SerializerOptions, cancellationToken);

        if (result is null)
        {
            throw new JsonException("The API returned a null device collection.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        using var response = await _httpClient.PostAsync($"api/devices/{deviceId}/activate", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SimulatorDeviceActivationResponse>(SerializerOptions, cancellationToken);

        if (result == null || result.DeviceId != deviceId || result.Lifecycle != SimulatorDeviceLifecycle.Active)
        {
            throw new JsonException("The API returned an invalid device activation response.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        using var response = await _httpClient.PostAsync($"api/devices/{deviceId}/heartbeat", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SimulatorDeviceHeartbeatResponse>(SerializerOptions, cancellationToken);

        if (result == null || result.DeviceId != deviceId || result.LastHeartbeatAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new JsonException("The API returned an invalid heartbeat response.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(Guid deviceId, SimulatorTelemetryMeasurementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        using var response = await _httpClient.PostAsJsonAsync($"api/devices/{deviceId}/telemetry", request, SerializerOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SimulatorTelemetryRecordingResponse>(SerializerOptions, cancellationToken);

        if (result == null || result.MeasurementId != request.MeasurementId)
        {
            throw new JsonException("The API returned an invalid telemetry recording response.");
        }

        return result;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(allowIntegerValues: false)
        }
    };
}
