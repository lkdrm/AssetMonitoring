using AssetMonitoring.Modules.DeviceManagement.Application.Connectivity;
using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Application.Devices;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AssetMonitoring.Modules.DeviceManagement.Infrastructure.Persistence.Queries;

internal sealed class DeviceQueries : IDeviceQueries
{
    private readonly DeviceManagementDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _offlineThreshold;

    public DeviceQueries(DeviceManagementDbContext dbContext, TimeProvider timeProvider, IOptions<DeviceConnectivityOptions> connectivityOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(connectivityOptions);

        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _offlineThreshold = connectivityOptions.Value.OfflineThreshold;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceResponse>> GetAllAsync(DeviceLifecycle? lifecycle = null, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Devices.AsNoTracking();

        if (lifecycle.HasValue)
        {
            query = query.Where(device => device.Lifecycle == lifecycle.Value);
        }

        var devices = await query.OrderBy(device => device.Code).ToListAsync(cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        return devices.Select(device => Map(device, utcNow)).ToList();
    }

    /// <inheritdoc />
    public async Task<DeviceResponse?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var device = await _dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Code == code, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        return device is null ? null : Map(device, utcNow);
    }

    private DeviceResponse Map(Device device, DateTime utcNow) =>
        new()
        {
            Id = device.Id,
            Code = device.Code,
            Name = device.Name,
            HardwareModel = device.HardwareModel,
            HardwareRevision = device.HardwareRevision,
            FirmwareVersion = device.FirmwareVersion,
            Location = device.Location,
            Capabilities = device.Capabilities.ToArray(),
            Lifecycle = device.Lifecycle,
            RegisteredAtUtc = device.RegisteredAtUtc,
            RetiredAtUtc = device.RetiredAtUtc,
            LastHeartbeatAtUtc = device.LastHeartbeatAtUtc,
            ConnectivityStatus = device.GetConnectivityStatus(utcNow, _offlineThreshold)
        };
}
