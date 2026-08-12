using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Application.Devices;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.DeviceManagement.Infrastructure.Persistence.Queries;

internal sealed class DeviceQueries : IDeviceQueries
{
    private readonly DeviceManagementDbContext _dbContext;

    public DeviceQueries(DeviceManagementDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
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

        return devices.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<DeviceResponse?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var device = await _dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Code == code, cancellationToken);

        return device is null ? null : Map(device);
    }

    private static DeviceResponse Map(Device device) =>
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
            RetiredAtUtc = device.RetiredAtUtc
        };
}
