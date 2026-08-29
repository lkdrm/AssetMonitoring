using AssetMonitoring.Modules.DeviceManagement.Application.Connectivity;
using AssetMonitoring.Modules.DeviceManagement.Application.Infrastructure;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(allowIntegerValues: false));
});

var deviceManagementConnectionString = builder.Configuration.GetConnectionString("DeviceManagement") ?? throw new InvalidOperationException(
        "The DeviceManagement database connection string is missing.");

builder.Services.AddDeviceManagement(deviceManagementConnectionString);
builder.Services.AddOpenApi();
builder.Services.AddOptions<DeviceConnectivityOptions>()
    .Bind(builder.Configuration
    .GetSection(DeviceConnectivityOptions.SectionName))
    .Validate(options => options.OfflineThreshold > TimeSpan.Zero, "Device connectivity offline threshold must be greater than zero.")
    .ValidateOnStart();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
