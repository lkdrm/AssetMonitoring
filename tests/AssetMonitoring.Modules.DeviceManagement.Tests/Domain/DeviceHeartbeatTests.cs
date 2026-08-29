using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Domain.Devices;

public sealed class DeviceHeartbeatTests
{
    private static readonly DateTime RegisteredAtUtc =
        new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RecordFirstHeartbeatForActiveDeviceStoresTimestampAndReturnsTrue()
    {
        var device = CreateActiveDevice();
        var heartbeatAtUtc = RegisteredAtUtc.AddMinutes(1);

        var changed = device.RecordHeartbeat(heartbeatAtUtc);

        Assert.True(changed);
        Assert.Equal(heartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void RecordNewerHeartbeatForActiveDeviceUpdatesTimestampAndReturnsTrue()
    {
        var device = CreateActiveDevice();
        var firstHeartbeatAtUtc = RegisteredAtUtc.AddMinutes(1);
        var secondHeartbeatAtUtc = RegisteredAtUtc.AddMinutes(2);
        device.RecordHeartbeat(firstHeartbeatAtUtc);

        var changed = device.RecordHeartbeat(secondHeartbeatAtUtc);

        Assert.True(changed);
        Assert.Equal(secondHeartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void RecordSameHeartbeatReturnsFalseAndPreservesTimestamp()
    {
        var device = CreateActiveDevice();
        var heartbeatAtUtc = RegisteredAtUtc.AddMinutes(1);
        device.RecordHeartbeat(heartbeatAtUtc);

        var changed = device.RecordHeartbeat(heartbeatAtUtc);

        Assert.False(changed);
        Assert.Equal(heartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void RecordOlderHeartbeatReturnsFalseAndPreservesTimestamp()
    {
        var device = CreateActiveDevice();
        var latestHeartbeatAtUtc = RegisteredAtUtc.AddMinutes(2);
        var olderHeartbeatAtUtc = RegisteredAtUtc.AddMinutes(1);
        device.RecordHeartbeat(latestHeartbeatAtUtc);

        var changed = device.RecordHeartbeat(olderHeartbeatAtUtc);

        Assert.False(changed);
        Assert.Equal(latestHeartbeatAtUtc, device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void RecordHeartbeatWithNonUtcTimestampThrowsAndPreservesState()
    {
        var device = CreateActiveDevice();
        var localTime = new DateTime(
            2026,
            8,
            12,
            10,
            1,
            0,
            DateTimeKind.Local);

        var exception = Assert.Throws<ArgumentException>(() =>
            device.RecordHeartbeat(localTime));

        Assert.Equal("heartbeatAtUtc", exception.ParamName);
        Assert.Null(device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void RecordHeartbeatForRegisteredDeviceThrowsAndPreservesState()
    {
        var device = CreateDevice();
        var heartbeatAtUtc = RegisteredAtUtc.AddMinutes(1);

        Assert.Throws<InvalidOperationException>(() =>
            device.RecordHeartbeat(heartbeatAtUtc));

        Assert.Equal(DeviceLifecycle.Registered, device.Lifecycle);
        Assert.Null(device.LastHeartbeatAtUtc);
    }

    [Fact]
    public void RecordHeartbeatForRetiredDeviceThrowsAndPreservesState()
    {
        var device = CreateDevice();
        var retiredAtUtc = RegisteredAtUtc.AddDays(1);
        device.Retire(retiredAtUtc);

        Assert.Throws<InvalidOperationException>(() =>
            device.RecordHeartbeat(retiredAtUtc.AddMinutes(1)));

        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
        Assert.Null(device.LastHeartbeatAtUtc);
    }

    private static Device CreateActiveDevice()
    {
        var device = CreateDevice();
        device.Activate();

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
            new[] { DeviceCapability.Temperature },
            RegisteredAtUtc);
    }
}
