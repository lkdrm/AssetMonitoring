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
