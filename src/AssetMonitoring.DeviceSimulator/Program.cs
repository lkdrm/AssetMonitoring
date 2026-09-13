using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Infrastructure;
using AssetMonitoring.DeviceSimulator.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Resolve configuration and plan files relative to the application directory.
var builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

builder.Services.AddDeviceSimulator();

builder.Services.AddOptions<DeviceSimulatorOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceSimulatorOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.PlanName), "Simulation plan name must not be null, empty, or whitespace.")
    .ValidateOnStart();
builder.Services.AddOptions<DeviceSimulatorOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceSimulatorOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiBaseAddress), "Simulation plan name is required.")
    .Validate(options => Uri
    .TryCreate(options.ApiBaseAddress, UriKind.Absolute, out var address)
    && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps), "API base address must be an absolute HTTP or HTTPS URI.")
    .ValidateOnStart();
builder.Services.AddOptions<DeviceSimulatorOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceSimulatorOptions.SectionName))
    .Validate(options => options.HeartbeatInterval > TimeSpan.Zero, "Device simulator heartbeat interval must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<DeviceSimulatorOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceSimulatorOptions.SectionName))
    .Validate(options => options.MaxConsecutiveHeartbeatFailures > 0, "Device simulator maximum consecutive heartbeat failures must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<DeviceSimulatorOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceSimulatorOptions.SectionName))
    .Validate(options => options.MaxConsecutiveTelemetryFailures > 0, "Device simulator maximum consecutive telemetry failures must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<DeviceSimulatorOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceSimulatorOptions.SectionName))
    .Validate(options => options.TelemetryInterval > TimeSpan.Zero, "Device simulator telemetry interval must be greater than zero.")
    .Validate(options =>
    {
        if (string.IsNullOrWhiteSpace(options.WarehouseTimeZoneId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(options.WarehouseTimeZoneId);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }, "Device simulator warehouse time zone ID must identify an available, valid time zone.")
    .ValidateOnStart();
builder.Services.AddHostedService<SimulationWorker>();

using var host = builder.Build();
await host.RunAsync();