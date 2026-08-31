using AssetMonitoring.Modules.Telemetry.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.Telemetry.Tests.Database;

internal sealed class SqliteTelemetryDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    internal SqliteTelemetryDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    internal TelemetryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new TelemetryDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}
