using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Support;

internal sealed class FakeDeviceRepository : IDeviceRepository
{
    private readonly List<Device> _devices;

    internal FakeDeviceRepository(IEnumerable<Device>? devices = null)
    {
        _devices = devices?.ToList() ?? [];
    }

    internal IReadOnlyList<Device> Devices => _devices;

    internal IReadOnlyList<Device> AddedDevices => _addedDevices;

    private readonly List<Device> _addedDevices = [];

    internal int GetAllCallCount { get; private set; }

    internal int SaveChangesCallCount { get; private set; }

    internal CancellationToken LastGetAllCancellationToken { get; private set; }

    internal CancellationToken LastSaveCancellationToken { get; private set; }

    public Task<IReadOnlyList<Device>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        GetAllCallCount++;
        LastGetAllCancellationToken = cancellationToken;

        return Task.FromResult<IReadOnlyList<Device>>(_devices.ToList());
    }

    public void Add(Device device)
    {
        _devices.Add(device);
        _addedDevices.Add(device);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        LastSaveCancellationToken = cancellationToken;

        return Task.CompletedTask;
    }
}
