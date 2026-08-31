using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.Telemetry.Database;

/// <summary>
/// Represents the Entity Framework Core database context for the
/// Telemetry module.
/// </summary>
public sealed class TelemetryDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TelemetryDbContext"/> class.
    /// </summary>
    /// <param name="options">
    /// The options used to configure the database context.
    /// </param>
    public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : base(options)
    { }

    /// <summary>
    /// Gets the telemetry measurements stored by the module.
    /// </summary>
    public DbSet<TelemetryMeasurement> Measurements => Set<TelemetryMeasurement>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TelemetryDbContext).Assembly);
    }
}
