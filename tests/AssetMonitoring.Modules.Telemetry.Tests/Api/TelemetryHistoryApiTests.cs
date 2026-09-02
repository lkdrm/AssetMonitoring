using System.Net;
using System.Text.Json;
using AssetMonitoring.Modules.Telemetry.Database;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.Telemetry.Tests.Api;

public sealed class TelemetryHistoryApiTests
{
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid OtherDeviceId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetHistoryWithoutMeasurementsReturnsOkWithDefaultEmptyPage()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(
            $"/api/devices/{DeviceId}/telemetry");
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, result.GetProperty("items").GetArrayLength());
        Assert.Equal(1, result.GetProperty("page").GetInt32());
        Assert.Equal(50, result.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task GetHistoryMapsQueryFiltersOrdersAndPaginatesMeasurements()
    {
        using var factory = new TelemetryApiFactory();
        var oldest = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc);
        var middle = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc.AddMinutes(1));
        var newest = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Temperature,
            22,
            MeasuredAtUtc.AddMinutes(2));
        var otherMetric = CreateNumericMeasurement(
            MeasurementId(4),
            DeviceId,
            TelemetryMetric.Humidity,
            55,
            MeasuredAtUtc.AddMinutes(2));
        var otherDevice = CreateNumericMeasurement(
            MeasurementId(5),
            OtherDeviceId,
            TelemetryMetric.Temperature,
            99,
            MeasuredAtUtc.AddMinutes(2));
        await SaveAsync(
            factory,
            oldest,
            middle,
            newest,
            otherMetric,
            otherDevice);
        using var client = CreateClient(factory);
        var fromUtc = Uri.EscapeDataString(MeasuredAtUtc.ToString("O"));
        var toUtc = Uri.EscapeDataString(
            MeasuredAtUtc.AddMinutes(2).ToString("O"));

        using var response = await client.GetAsync(
            $"/api/devices/{DeviceId}/telemetry" +
            $"?metric=Temperature&fromUtc={fromUtc}&toUtc={toUtc}" +
            "&page=1&pageSize=2");
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement;
        var items = result.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, items.Length);
        Assert.Equal(
            newest.Id,
            items[0].GetProperty("measurementId").GetGuid());
        Assert.Equal(
            middle.Id,
            items[1].GetProperty("measurementId").GetGuid());
        Assert.Equal(1, result.GetProperty("page").GetInt32());
        Assert.Equal(2, result.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, result.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task GetHistoryWithInvalidPageReturnsBadRequestProblemDetails()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(
            $"/api/devices/{DeviceId}/telemetry?page=0");
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Telemetry history query is invalid.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            $"/api/devices/{DeviceId}/telemetry",
            problem.GetProperty("instance").GetString());
    }

    private static HttpClient CreateClient(TelemetryApiFactory factory)
    {
        return factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
    }

    private static TelemetryMeasurement CreateNumericMeasurement(
        Guid measurementId,
        Guid deviceId,
        TelemetryMetric metric,
        double value,
        DateTime measuredAtUtc)
    {
        return TelemetryMeasurement.CreateNumeric(
            measurementId,
            deviceId,
            metric,
            value,
            measuredAtUtc);
    }

    private static Guid MeasurementId(int value)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    }

    private static async Task SaveAsync(
        TelemetryApiFactory factory,
        params TelemetryMeasurement[] measurements)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<TelemetryDbContext>();
        dbContext.Measurements.AddRange(measurements);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
