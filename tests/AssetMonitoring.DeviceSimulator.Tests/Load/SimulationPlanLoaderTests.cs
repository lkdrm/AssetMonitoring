using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Load;
using AssetMonitoring.DeviceSimulator.Scenarios;
using System.Text.Json;

namespace AssetMonitoring.DeviceSimulator.Tests.Load;

/// <summary>
/// Verifies simulation plan loading, validation results, and reader failures.
/// </summary>
public sealed class SimulationPlanLoaderTests
{
    /// <summary>
    /// Verifies that the reader dependency is required.
    /// </summary>
    [Fact]
    public void ConstructorWithNullReaderThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationPlanLoader(null!, new SimulationPlanValidator()));

        Assert.Equal("reader", exception.ParamName);
    }

    /// <summary>
    /// Verifies that a missing validator is identified correctly.
    /// </summary>
    [Fact]
    public void ConstructorWithNullValidatorThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationPlanLoader(new StubSimulationPlanReader(), null!));

        Assert.Equal("validator", exception.ParamName);
    }

    /// <summary>
    /// Verifies that a null plan name is rejected before reading.
    /// </summary>
    [Fact]
    public async Task LoadAsyncWithNullPlanNameThrowsBeforeReading()
    {
        var reader = new StubSimulationPlanReader();
        var loader = CreateLoader(reader);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            loader.LoadAsync(null!, TestContext.Current.CancellationToken));

        Assert.Equal("planName", exception.ParamName);
        Assert.Equal(0, reader.CallCount);
    }

    /// <summary>
    /// Verifies that empty or whitespace plan names are rejected before reading.
    /// </summary>
    /// <param name="planName">The invalid plan name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task LoadAsyncWithBlankPlanNameThrowsBeforeReading(string planName)
    {
        var reader = new StubSimulationPlanReader();
        var loader = CreateLoader(reader);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            loader.LoadAsync(planName, TestContext.Current.CancellationToken));

        Assert.Equal("planName", exception.ParamName);
        Assert.Equal(0, reader.CallCount);
    }

    /// <summary>
    /// Verifies successful loading and forwarding of the name and cancellation token.
    /// </summary>
    [Fact]
    public async Task LoadAsyncWithValidPlanReturnsPlanAndForwardsReaderArguments()
    {
        var plan = CreateValidPlan();
        var reader = new StubSimulationPlanReader((_, _) => Task.FromResult(plan));
        var loader = CreateLoader(reader);
        using var cancellation = new CancellationTokenSource();

        var result = await loader.LoadAsync("normal-operation", cancellation.Token);

        Assert.Same(plan, result.Plan);
        Assert.True(result.IsValid);
        Assert.True(result.Validation.IsValid);
        Assert.Empty(result.Validation.Errors);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal("normal-operation", reader.LastPlanName);
        Assert.Equal(cancellation.Token, reader.LastCancellationToken);
    }

    /// <summary>
    /// Verifies that invalid settings are returned as errors without throwing.
    /// </summary>
    [Fact]
    public async Task LoadAsyncWithInvalidPlanReturnsPlanAndAllValidationErrors()
    {
        var plan = CreateValidPlan() with { Name = " ", Scenarios = null! };
        var reader = new StubSimulationPlanReader((_, _) => Task.FromResult(plan));
        var loader = CreateLoader(reader);

        var result = await loader.LoadAsync("invalid-plan", TestContext.Current.CancellationToken);

        Assert.Same(plan, result.Plan);
        Assert.False(result.IsValid);
        Assert.False(result.Validation.IsValid);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal(2, result.Validation.Errors.Count);
        Assert.Contains(result.Validation.Errors, error =>
            error.Code == "Plan.Name.Required" && error.Path == "name");
        Assert.Contains(result.Validation.Errors, error =>
            error.Code == "Plan.Scenarios.Required" && error.Path == "scenarios");
        Assert.All(result.Validation.Errors, error =>
            Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }

    /// <summary>
    /// Verifies that a missing plan file remains a read failure.
    /// </summary>
    [Fact]
    public async Task LoadAsyncWhenFileIsMissingPropagatesReaderException()
    {
        var expected = new FileNotFoundException("Simulation plan was not found.");
        var reader = new StubSimulationPlanReader((_, _) =>
            Task.FromException<SimulationPlanDefinition>(expected));
        var loader = CreateLoader(reader);

        var actual = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            loader.LoadAsync("missing-plan", TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
    }

    /// <summary>
    /// Verifies that malformed JSON is not converted into a validation result.
    /// </summary>
    [Fact]
    public async Task LoadAsyncWhenJsonIsMalformedPropagatesReaderException()
    {
        var expected = new JsonException("Simulation plan JSON is malformed.");
        var reader = new StubSimulationPlanReader((_, _) =>
            Task.FromException<SimulationPlanDefinition>(expected));
        var loader = CreateLoader(reader);

        var actual = await Assert.ThrowsAsync<JsonException>(() =>
            loader.LoadAsync("malformed-plan", TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
    }

    /// <summary>
    /// Verifies that cancellation from the reader cancels the load operation.
    /// </summary>
    [Fact]
    public async Task LoadAsyncWhenReaderIsCanceledPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new StubSimulationPlanReader((_, token) =>
            Task.FromCanceled<SimulationPlanDefinition>(token));
        var loader = CreateLoader(reader);

        var loadTask = loader.LoadAsync("normal-operation", cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loadTask);

        Assert.True(loadTask.IsCanceled);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, reader.LastCancellationToken);
    }

    /// <summary>
    /// Creates a loader using the real validator and the supplied reader.
    /// </summary>
    /// <param name="reader">The reader stub used by the test.</param>
    /// <returns>A loader ready for testing.</returns>
    private static SimulationPlanLoader CreateLoader(ISimulationPlanReader reader)
    {
        return new SimulationPlanLoader(reader, new SimulationPlanValidator());
    }

    /// <summary>
    /// Creates a valid normal-operation plan without additional scenarios.
    /// </summary>
    /// <returns>A valid simulation plan.</returns>
    private static SimulationPlanDefinition CreateValidPlan()
    {
        return new SimulationPlanDefinition(
            "NormalOperation",
            42,
            Array.Empty<ScenarioDefinition>());
    }

    /// <summary>
    /// Supplies configured read outcomes without accessing the file system.
    /// </summary>
    private sealed class StubSimulationPlanReader : ISimulationPlanReader
    {
        private readonly Func<string, CancellationToken, Task<SimulationPlanDefinition>> _read;

        /// <summary>
        /// Initializes a reader with an optional read operation.
        /// </summary>
        /// <param name="read">
        /// The configured read operation, or null to return a valid plan.
        /// </param>
        public StubSimulationPlanReader(
            Func<string, CancellationToken, Task<SimulationPlanDefinition>>? read = null)
        {
            _read = read ?? ((_, _) => Task.FromResult(CreateValidPlan()));
        }

        /// <summary>
        /// Gets the number of read requests.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Gets the name supplied to the most recent read request.
        /// </summary>
        public string? LastPlanName { get; private set; }

        /// <summary>
        /// Gets the token supplied to the most recent read request.
        /// </summary>
        public CancellationToken LastCancellationToken { get; private set; }

        /// <inheritdoc />
        public Task<SimulationPlanDefinition> ReadAsync(
            string planName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPlanName = planName;
            LastCancellationToken = cancellationToken;

            return _read(planName, cancellationToken);
        }
    }
}
