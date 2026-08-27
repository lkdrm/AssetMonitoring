using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Support;

internal sealed class StubDeviceCatalogReader : IDeviceCatalogReader
{
    private readonly Func<string, CancellationToken, Task<DeviceCatalogDocument>> _read;

    internal StubDeviceCatalogReader(DeviceCatalogDocument document)
        : this((_, _) => Task.FromResult(document))
    {
    }

    internal StubDeviceCatalogReader(
        Func<string, CancellationToken, Task<DeviceCatalogDocument>> read)
    {
        _read = read;
    }

    internal int CallCount { get; private set; }

    internal string? LastPath { get; private set; }

    internal CancellationToken LastCancellationToken { get; private set; }

    public Task<DeviceCatalogDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastPath = path;
        LastCancellationToken = cancellationToken;

        return _read(path, cancellationToken);
    }
}
