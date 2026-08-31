using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetMonitoring.Modules.Telemetry.Database;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.Telemetry.Tests.Api;

public sealed class TelemetryApiTests
{
    private static readonly Guid MeasurementId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RecordNumericMeasurementReturnsCreatedAndPersistsMeasurement()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var response = await RecordNumericAsync(client);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(MeasurementId, result.GetProperty("measurementId").GetGuid());
        Assert.True(result.GetProperty("recorded").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<TelemetryDbContext>();
        var measurement = await dbContext.Measurements
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(MeasurementId, measurement.Id);
        Assert.Equal(DeviceId, measurement.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, measurement.Metric);
        Assert.Equal(21.5, measurement.NumericValue);
        Assert.Null(measurement.StateValue);
        Assert.Equal(MeasuredAtUtc, measurement.MeasuredAtUtc);
    }

    [Fact]
    public async Task RecordDuplicateMeasurementReturnsOkAndPersistsOneMeasurement()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);

        using var firstResponse = await RecordNumericAsync(client);
        using var secondResponse = await RecordNumericAsync(client);
        using var document = await ReadJsonAsync(secondResponse);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(MeasurementId, result.GetProperty("measurementId").GetGuid());
        Assert.False(result.GetProperty("recorded").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<TelemetryDbContext>();
        Assert.Equal(1, await dbContext.Measurements.CountAsync());
    }

    [Fact]
    public async Task RecordFalseStateMeasurementReturnsCreatedAndPersistsFalseValue()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);
        var body = new
        {
            measurementId = MeasurementId,
            metric = "LightState",
            numericValue = (double?)null,
            stateValue = false,
            measuredAtUtc = MeasuredAtUtc
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/devices/{DeviceId}/telemetry",
            body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<TelemetryDbContext>();
        var measurement = await dbContext.Measurements
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(TelemetryMetric.LightState, measurement.Metric);
        Assert.Null(measurement.NumericValue);
        Assert.False(measurement.StateValue);
    }

    [Fact]
    public async Task RecordMeasurementWithConflictingValuesReturnsBadRequestProblemDetails()
    {
        using var factory = new TelemetryApiFactory();
        using var client = CreateClient(factory);
        var body = new
        {
            measurementId = MeasurementId,
            metric = "Temperature",
            numericValue = 21.5,
            stateValue = true,
            measuredAtUtc = MeasuredAtUtc
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/devices/{DeviceId}/telemetry",
            body);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Telemetry measurement cannot be recorded.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            "Numeric telemetry metrics cannot contain a state value. " +
            "(Parameter 'request')",
            problem.GetProperty("detail").GetString());
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

    private static Task<HttpResponseMessage> RecordNumericAsync(
        HttpClient client)
    {
        var body = new
        {
            measurementId = MeasurementId,
            metric = "Temperature",
            numericValue = 21.5,
            stateValue = (bool?)null,
            measuredAtUtc = MeasuredAtUtc
        };

        return client.PostAsJsonAsync(
            $"/api/devices/{DeviceId}/telemetry",
            body);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
