using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Repository;

internal sealed class DeviceRepository : IDeviceRepository
{
    private readonly DeviceManagementDbContext _dbContext;

    public DeviceRepository(DeviceManagementDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public void Add(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _dbContext.Devices.Add(device);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken = default) => await _dbContext.Devices.ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Device?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => await _dbContext.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _dbContext.SaveChangesAsync(cancellationToken);
}
