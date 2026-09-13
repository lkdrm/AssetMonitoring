using AssetMonitoring.DeviceSimulator.Api.Contracts;
using System.Text.Json;

namespace AssetMonitoring.DeviceSimulator.Interfaces;

/// <summary>
/// Defines operations used by the simulator to communicate
/// with the Asset Monitoring API.
/// </summary>
public interface IDeviceSimulatorApiClient
{
    /// <summary>
    /// Requests device catalog synchronization and reads its result,
    /// including catalog validation errors returned with HTTP 400.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token used to cancel the HTTP operation and response reading.
    /// </param>
    /// <returns>
    /// The synchronization result, including validation details
    /// and device counts.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the HTTP request fails or the API returns an
    /// unsuccessful status other than HTTP 400.
    /// </exception>
    /// <exception cref="JsonException">
    /// Thrown when the response cannot be deserialized
    /// or required response data is missing.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is canceled or the request times out.
    /// </exception>
    Task<DeviceCatalogSynchronizationResponse> SynchronizeDeviceCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves devices from the Asset Monitoring API.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token used to cancel the HTTP operation.
    /// </param>
    /// <returns>
    /// A list of devices returned by the API.
    /// The list may be empty.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the HTTP request fails or the response status is unsuccessful.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when the response cannot be deserialized
    /// or contains a null device collection.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is canceled or the request times out.
    /// </exception>
    Task<IReadOnlyList<SimulatorDeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates monitoring for the specified device through the API.
    /// Repeated activation is idempotent.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device to activate.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the HTTP operation.
    /// </param>
    /// <returns>
    /// The successful activation result, including whether
    /// the device lifecycle was changed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when the HTTP request fails or its status is unsuccessful.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when the activation response is malformed or invalid.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is canceled or the request times out.
    /// </exception>
    Task<SimulatorDeviceActivationResponse> ActivateDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a heartbeat for an active device and reads the server response.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device sending the heartbeat.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the HTTP operation.
    /// </param>
    /// <returns>
    /// The stored heartbeat timestamp and whether it was updated.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when the request fails or the response status is unsuccessful.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when the response contains malformed JSON or invalid heartbeat data.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is canceled or the request times out.
    /// </exception>
    Task<SimulatorDeviceHeartbeatResponse> SendHeartbeatAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a telemetry measurement to the API.
    /// Repeating the same measurement identifier supports idempotent recording.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device that produced the measurement.
    /// </param>
    /// <param name="request">
    /// The measurement to send.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the HTTP operation.
    /// </param>
    /// <returns>
    /// The measurement identifier and whether a new measurement was recorded.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when the HTTP request fails or the response status is unsuccessful.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when the response cannot be deserialized,
    /// is null, or contains a different measurement identifier.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is canceled or the request times out.
    /// </exception>
    Task<SimulatorTelemetryRecordingResponse> SendTelemetryAsync(Guid deviceId, SimulatorTelemetryMeasurementRequest request, CancellationToken cancellationToken = default);
}
