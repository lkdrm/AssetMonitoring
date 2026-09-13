using System.Net;
using System.Text.Json;
using AssetMonitoring.Modules.Telemetry.Database;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.Telemetry.Tests.Api;

public sealed class TelemetryLatestApiTests
{
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid OtherDeviceId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetLatestReturnsNewestRequestedDeviceMetricMeasurement()
    {
        using var factory = new TelemetryApiFactory();
        var older = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc);
        var expected = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            22,
            MeasuredAtUtc.AddMinutes(1));
        var otherMetric = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Humidity,
            55,
            MeasuredAtUtc.AddMinutes(2));
        var otherDevice = CreateNumericMeasurement(
            MeasurementId(4),
            OtherDeviceId,
            TelemetryMetric.Temperature,
            99,
            MeasuredAtUtc.AddMinutes(2));
        await SaveAsync(factory, older, expected, otherMetric, otherDevice);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync($"/api/devices/{DeviceId}/telemetry/latest" +
            "?metric=Temperature", TestContext.Current.CancellationToken);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            expected.Id,
            result.GetProperty("measurementId").GetGuid());
        Assert.Equal(DeviceId, result.GetProperty("deviceId").GetGuid());
        Assert.Equal(22, result.GetProperty("numericValue").GetDouble());
        Assert.Equal(
            expected.MeasuredAtUtc,
            result.GetProperty("measuredAtUtc").GetDateTime());
    }

    [Fact]
    public async Task GetLatestWithoutMatchingMeasurementReturnsNotFoundProblemDetails()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync($"/api/devices/{DeviceId}/telemetry/latest" +
            "?metric=DoorState", TestContext.Current.CancellationToken);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Telemetry measurement not found.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            $"/api/devices/{DeviceId}/telemetry/latest",
            problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task GetLatestWithoutMetricReturnsBadRequestProblemDetails()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync($"/api/devices/{DeviceId}/telemetry/latest", TestContext.Current.CancellationToken);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Telemetry latest query is invalid.",
            problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetLatestWithUnsupportedMetricReturnsBadRequestProblemDetails()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync($"/api/devices/{DeviceId}/telemetry/latest?metric=999", TestContext.Current.CancellationToken);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "One or more validation errors occurred.",
            problem.GetProperty("title").GetString());
        Assert.True(problem.TryGetProperty("errors", out var errors));
        Assert.Contains("999", errors.ToString());
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
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
