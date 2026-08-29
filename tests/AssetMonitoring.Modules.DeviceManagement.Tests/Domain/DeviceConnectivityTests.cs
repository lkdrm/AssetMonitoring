using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Domain.Devices;

public sealed class DeviceConnectivityTests
{
    private static readonly DateTime RegisteredAtUtc =
        new(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime UtcNow =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan OfflineThreshold =
        TimeSpan.FromMinutes(5);

    [Fact]
    public void GetConnectivityStatusWithoutHeartbeatReturnsNeverConnected()
    {
        var device = CreateDevice();

        var status = device.GetConnectivityStatus(
            UtcNow,
            OfflineThreshold);

        Assert.Equal(DeviceConnectivityStatus.NeverConnected, status);
        Assert.Null(device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void GetConnectivityStatusWithRecentHeartbeatReturnsOnline()
    {
        var heartbeatAtUtc = UtcNow.AddMinutes(-2);
        var device = CreateDeviceWithHeartbeat(heartbeatAtUtc);

        var status = device.GetConnectivityStatus(
            UtcNow,
            OfflineThreshold);

        Assert.Equal(DeviceConnectivityStatus.Online, status);
        Assert.Equal(heartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void GetConnectivityStatusAtThresholdBoundaryReturnsOnline()
    {
        var heartbeatAtUtc = UtcNow - OfflineThreshold;
        var device = CreateDeviceWithHeartbeat(heartbeatAtUtc);

        var status = device.GetConnectivityStatus(
            UtcNow,
            OfflineThreshold);

        Assert.Equal(DeviceConnectivityStatus.Online, status);
        Assert.Equal(heartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void GetConnectivityStatusWithExpiredHeartbeatReturnsOffline()
    {
        var heartbeatAtUtc =
            UtcNow - OfflineThreshold - TimeSpan.FromTicks(1);
        var device = CreateDeviceWithHeartbeat(heartbeatAtUtc);

        var status = device.GetConnectivityStatus(
            UtcNow,
            OfflineThreshold);

        Assert.Equal(DeviceConnectivityStatus.Offline, status);
        Assert.Equal(heartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void GetConnectivityStatusWithNonUtcCurrentTimeThrowsArgumentException()
    {
        var device = CreateDevice();
        var localTime = new DateTime(
            2026,
            8,
            29,
            12,
            0,
            0,
            DateTimeKind.Local);

        var exception = Assert.Throws<ArgumentException>(() =>
            device.GetConnectivityStatus(localTime, OfflineThreshold));

        Assert.Equal("utcNow", exception.ParamName);
        Assert.Null(device.LastHeartbeatAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetConnectivityStatusWithNonPositiveThresholdThrowsArgumentOutOfRangeException(
        int thresholdMinutes)
    {
        var device = CreateDevice();
        var offlineThreshold = TimeSpan.FromMinutes(thresholdMinutes);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.GetConnectivityStatus(UtcNow, offlineThreshold));

        Assert.Equal("offlineThreshold", exception.ParamName);
        Assert.Null(device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void GetConnectivityStatusForRetiredDeviceUsesHeartbeatWithoutChangingLifecycle()
    {
        var heartbeatAtUtc = UtcNow.AddMinutes(-2);
        var device = CreateDeviceWithHeartbeat(heartbeatAtUtc);
        var retiredAtUtc = UtcNow.AddMinutes(-1);
        device.Retire(retiredAtUtc);

        var status = device.GetConnectivityStatus(
            UtcNow,
            OfflineThreshold);

        Assert.Equal(DeviceConnectivityStatus.Online, status);
        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
        Assert.Equal(heartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    private static Device CreateDeviceWithHeartbeat(DateTime heartbeatAtUtc)
    {
        var device = CreateDevice();
        device.Activate();
        device.RecordHeartbeat(heartbeatAtUtc);

        return device;
    }

    private static Device CreateDevice()
    {
        return new Device(
            "WH-001",
            "Warehouse sensor",
            "Sensor-X",
            "R1",
            "1.0.0",
            "Warehouse A",
            [DeviceCapability.Temperature],
            RegisteredAtUtc);
    }
}
