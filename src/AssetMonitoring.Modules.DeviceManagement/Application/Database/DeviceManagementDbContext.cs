using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Database;

/// <summary>
/// Represents the Entity Framework Core database context
/// for the Device Management module.
/// </summary>
public sealed class DeviceManagementDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceManagementDbContext"/> class.
    /// </summary>
    /// <param name="options">The options used to configure the database context.</param>
    public DeviceManagementDbContext(DbContextOptions<DeviceManagementDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Gets the collection of devices tracked by the database context.
    /// </summary>
    public DbSet<Device> Devices => Set<Device>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeviceManagementDbContext).Assembly);
    }
}
