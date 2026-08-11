namespace AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

/// <summary>
/// Represents a physical device registered in the asset monitoring platform.
/// Controls the device metadata, supported capabilities, and lifecycle.
/// </summary>
public class Device
{
    /// <summary>
    /// Gets the unique internal identifier of the device.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the unique catalog code used to identify the device,
    /// for example, ENTRANCE-01.
    /// </summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public string HardwareModel { get; private set; }

    public string HardwareRevision { get; private set; }

    public string FirmwareVersion { get; private set; }

    public string Location { get; private set; }

    /// <summary>
    /// Gets the telemetry types supported by the device.
    /// Capabilities describe what the device can measure,
    /// but do not contain current telemetry values.
    /// </summary>
    private readonly HashSet<DeviceCapability> _capabilities;

    public IReadOnlySet<DeviceCapability> Capabilities => _capabilities;

    /// <summary>
    /// Gets the UTC timestamp when the device was registered.
    /// </summary>
    public DateTime RegisteredAtUtc { get; private set; }

    /// <summary>
    /// Gets the current business lifecycle of the device.
    /// Lifecycle is independent of its online or offline connection status.
    /// </summary>
    public DeviceLifecycle Lifecycle { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp when the device was retired,
    /// or null when it is not retired.
    /// </summary>
    public DateTime? RetiredAtUtc { get; private set; }

    /// <summary>
    /// Creates a registered device with its catalog metadata and capabilities.
    /// </summary>
    /// <param name="code">Unique catalog code.</param>
    /// <param name="name">Human-readable device name.</param>
    /// <param name="hardwareModel">Hardware model.</param>
    /// <param name="hardwareRevision">Hardware revision.</param>
    /// <param name="firmwareVersion">Installed firmware version.</param>
    /// <param name="location">Physical device location.</param>
    /// <param name="capabilities">Telemetry types supported by the device.</param>
    /// <param name="registeredAtUtc">Device registration time in UTC.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when required data is invalid, capabilities are empty,
    /// or the registration time is not UTC.
    /// </exception>
    public Device(string code, string name, string hardwareModel, string hardwareRevision, string firmwareVersion, string location, IEnumerable<DeviceCapability> capabilities, DateTime registeredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hardwareModel) || string.IsNullOrWhiteSpace(hardwareRevision) || string.IsNullOrWhiteSpace(firmwareVersion) || string.IsNullOrWhiteSpace(location) || capabilities == null)
        {
            throw new ArgumentException("Required device data is missing or invalid.");
        }

        if (registeredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Registration time must be UTC.", nameof(registeredAtUtc));
        }

        Id = Guid.NewGuid();
        Code = code;
        Name = name;
        HardwareModel = hardwareModel;
        HardwareRevision = hardwareRevision;
        FirmwareVersion = firmwareVersion;
        Location = location;
        _capabilities = capabilities.ToHashSet();
        RegisteredAtUtc = registeredAtUtc;
        Lifecycle = DeviceLifecycle.Registered;
        RetiredAtUtc = null;
    }

    /// <summary>
    /// Retires the device while preserving its historical data.
    /// Repeated retirement does not change the original retirement time.
    /// </summary>
    /// <param name="retiredAtUtc">Retirement time in UTC.</param>
    /// <returns>
    /// True when the lifecycle was changed; otherwise, false.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the retirement time is not UTC.
    /// </exception>
    public bool Retire(DateTime retiredAtUtc)
    {
        if (retiredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Retirement time must be UTC.", nameof(retiredAtUtc));
        }

        if (Lifecycle == DeviceLifecycle.Retired)
        {
            return false;
        }

        Lifecycle = DeviceLifecycle.Retired;
        RetiredAtUtc = retiredAtUtc;

        return true;
    }

    /// <summary>
    /// Restores a retired device to the registered lifecycle.
    /// Monitoring is not activated automatically.
    /// </summary>
    /// <returns>
    /// True when the lifecycle was changed; otherwise, false.
    /// </returns>
    public bool Restore()
    {
        if (Lifecycle != DeviceLifecycle.Retired)
        {
            return false;
        }

        Lifecycle = DeviceLifecycle.Registered;
        RetiredAtUtc = null;

        return true;
    }

    /// <summary>
    /// Updates the catalog-managed metadata and capabilities of the device.
    /// </summary>
    /// <param name="name">Human-readable device name.</param>
    /// <param name="hardwareModel">Hardware model.</param>
    /// <param name="hardwareRevision">Hardware revision.</param>
    /// <param name="firmwareVersion">Installed firmware version.</param>
    /// <param name="location">Physical device location.</param>
    /// <param name="capabilities">Telemetry types supported by the device.</param>
    /// <returns>
    /// True when at least one metadata value or capability was changed;
    /// otherwise, false.
    /// </returns>
    /// <remarks>
    /// This operation does not change the device identifier, catalog code,
    /// registration time, lifecycle, or retirement time.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when required metadata is missing.
    /// </exception>
    public bool UpdateMetadata(string name, string hardwareModel, string hardwareRevision, string firmwareVersion, string location, IEnumerable<DeviceCapability> capabilities)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hardwareModel) || string.IsNullOrWhiteSpace(hardwareRevision) || string.IsNullOrWhiteSpace(firmwareVersion) || string.IsNullOrWhiteSpace(location) || capabilities == null)
        {
            throw new ArgumentException("Required device data is missing or invalid.");
        }

        var currentMetadata = (Name, HardwareModel, HardwareRevision, FirmwareVersion, Location);
        var incomingMetadata = (name, hardwareModel, hardwareRevision, firmwareVersion, location);
        var newCapabilities = capabilities.ToHashSet();

        var metadataIsEqual = currentMetadata == incomingMetadata;
        var capabilitiesAreEqual = _capabilities.SetEquals(newCapabilities);

        if (metadataIsEqual && capabilitiesAreEqual)
        {
            return false;
        }

        ApplyMetadata(name, hardwareModel, hardwareRevision, firmwareVersion, location, newCapabilities);

        return true;
    }

    private void ApplyMetadata(string name, string hardwareModel, string hardwareRevision, string firmwareVersion, string location, HashSet<DeviceCapability> capabilities)
    {
        Name = name;
        HardwareModel = hardwareModel;
        HardwareRevision = hardwareRevision;
        FirmwareVersion = firmwareVersion;
        Location = location;

        _capabilities.Clear();
        _capabilities.UnionWith(capabilities);
    }
}
