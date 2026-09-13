using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;

namespace AssetMonitoring.DeviceSimulator.Preparation;

/// <summary>
/// Prepares devices for simulation by excluding retired devices,
/// activating registered devices, and preserving active devices.
/// </summary>
public sealed class DevicePreparationService
{
    private readonly IDeviceSimulatorApiClient _devices;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DevicePreparationService"/> class.
    /// </summary>
    /// <param name="devices">
    /// The API client used to activate registered devices.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="devices"/> is null.
    /// </exception>
    public DevicePreparationService(IDeviceSimulatorApiClient devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        _devices = devices;
    }

    /// <summary>
    /// Prepares devices for simulation by skipping retired devices
    /// and activating registered devices.
    /// </summary>
    /// <param name="devices">
    /// The devices retrieved from the API.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel device preparation.
    /// </param>
    /// <returns>
    /// Devices ready for simulation, with updated lifecycle values
    /// for devices activated during preparation.
    /// The collection is empty when no eligible devices are available.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="devices"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a device has an unsupported lifecycle.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when an activation request fails or returns an unsuccessful status.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when an activation response cannot be deserialized
    /// or does not satisfy the expected response contract.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when preparation is canceled or an activation request times out.
    /// </exception>
    public async Task<IReadOnlyList<SimulatorDeviceResponse>> PrepareAsync(IReadOnlyList<SimulatorDeviceResponse> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var preparedDevices = new List<SimulatorDeviceResponse>();

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (device.Lifecycle)
            {
                case SimulatorDeviceLifecycle.Retired:
                    continue;

                case SimulatorDeviceLifecycle.Active:
                    preparedDevices.Add(device);
                    break;

                case SimulatorDeviceLifecycle.Registered:
                    var activationResult = await _devices.ActivateDeviceAsync(device.Id, cancellationToken);
                    preparedDevices.Add(device with
                    {
                        Lifecycle = activationResult.Lifecycle
                    });
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported device lifecycle '{device.Lifecycle}'.");
            }
        }

        return preparedDevices;
    }
}
