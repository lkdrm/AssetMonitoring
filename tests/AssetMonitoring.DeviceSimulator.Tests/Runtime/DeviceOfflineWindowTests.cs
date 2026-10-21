using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Xunit;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies construction, exact time boundaries, and stateless queries
/// for a scheduled device outage.
/// </summary>
public sealed class DeviceOfflineWindowTests
{
    /// <summary>Identifies the device used by valid window fixtures.</summary>
    private static readonly Guid DeviceId = Guid.Parse("d47039b9-6348-4cca-a74e-afc0871e6afb");

    /// <summary>Verifies that a definition is required.</summary>
    [Fact]
    public void ConstructorWithNullDefinitionThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DeviceOfflineWindow(null!, DeviceId));

        Assert.Equal("definition", exception.ParamName);
    }

    /// <summary>Verifies that a window cannot belong to an empty device identifier.</summary>
    [Fact]
    public void ConstructorWithEmptyDeviceIdThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new DeviceOfflineWindow(CreateDefinition(), Guid.Empty));

        Assert.Equal("deviceId", exception.ParamName);
    }

    /// <summary>Verifies that the window exposes its owning device and supplied definition.</summary>
    [Fact]
    public void ConstructorPreservesDeviceIdAndDefinition()
    {
        var definition = CreateDefinition();

        var window = new DeviceOfflineWindow(definition, DeviceId);

        Assert.Equal(DeviceId, window.DeviceId);
        Assert.Same(definition, window.Definition);
    }

    /// <summary>Verifies that elapsed simulation time cannot be negative.</summary>
    /// <param name="elapsedTicks">The invalid elapsed time, expressed in ticks.</param>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-TimeSpan.TicksPerSecond)]
    public void IsActiveAtWithNegativeElapsedThrowsArgumentOutOfRangeException(long elapsedTicks)
    {
        var window = CreateWindow();
        var elapsed = TimeSpan.FromTicks(elapsedTicks);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => window.IsActiveAt(elapsed));

        Assert.Equal("elapsed", exception.ParamName);
        Assert.Equal(elapsed, Assert.IsType<TimeSpan>(exception.ActualValue));
    }

    /// <summary>
    /// Verifies that the start is inclusive and the end is exclusive,
    /// including measurements one tick before and after each boundary.
    /// </summary>
    /// <param name="elapsedTicks">The queried elapsed time, expressed in ticks.</param>
    /// <param name="expected">Whether sending should be suspended at that time.</param>
    [Theory]
    [InlineData(0L, false)]
    [InlineData(TimeSpan.TicksPerMinute - 1, false)]
    [InlineData(TimeSpan.TicksPerMinute, true)]
    [InlineData(TimeSpan.TicksPerMinute + 1, true)]
    [InlineData(3 * TimeSpan.TicksPerMinute - 1, true)]
    [InlineData(3 * TimeSpan.TicksPerMinute, false)]
    [InlineData(3 * TimeSpan.TicksPerMinute + 1, false)]
    public void IsActiveAtUsesAnInclusiveStartAndExclusiveEnd(long elapsedTicks, bool expected)
    {
        var window = CreateWindow();

        var result = window.IsActiveAt(TimeSpan.FromTicks(elapsedTicks));

        Assert.Equal(expected, result);
    }

    /// <summary>Verifies exact boundaries when an outage begins at simulation startup.</summary>
    /// <param name="elapsedTicks">The queried elapsed time, expressed in ticks.</param>
    /// <param name="expected">Whether the outage is active at that time.</param>
    [Theory]
    [InlineData(0L, true)]
    [InlineData(2 * TimeSpan.TicksPerMinute - 1, true)]
    [InlineData(2 * TimeSpan.TicksPerMinute, false)]
    public void IsActiveAtWithZeroStartUsesTheConfiguredDuration(long elapsedTicks, bool expected)
    {
        var window = CreateWindow(TimeSpan.Zero);

        var result = window.IsActiveAt(TimeSpan.FromTicks(elapsedTicks));

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that querying an ended window does not permanently complete it:
    /// later queries for earlier times still return their independent results.
    /// </summary>
    [Fact]
    public void IsActiveAtWithNonchronologicalQueriesDoesNotRetainExecutionState()
    {
        var window = CreateWindow();

        Assert.False(window.IsActiveAt(TimeSpan.FromMinutes(3)));
        Assert.True(window.IsActiveAt(TimeSpan.FromMinutes(1)));
        Assert.False(window.IsActiveAt(TimeSpan.Zero));
        Assert.True(window.IsActiveAt(TimeSpan.FromMinutes(2)));
        Assert.False(window.IsActiveAt(TimeSpan.FromMinutes(3)));
    }

    /// <summary>Creates a window using a valid automatically recovering outage definition.</summary>
    /// <param name="startsAfter">The start delay, or null to use one minute.</param>
    /// <returns>A new immutable window owned by the fixture device.</returns>
    private static DeviceOfflineWindow CreateWindow(TimeSpan? startsAfter = null)
        => new(CreateDefinition(startsAfter), DeviceId);

    /// <summary>Creates a definition with a two-minute outage and immediate recovery.</summary>
    /// <param name="startsAfter">The start delay, or null to use one minute.</param>
    /// <returns>A valid definition with no gradual recovery duration.</returns>
    private static DeviceOfflineScenarioDefinition CreateDefinition(TimeSpan? startsAfter = null)
        => new(
            Name: "NetworkOutage",
            StartsAfter: startsAfter ?? TimeSpan.FromMinutes(1),
            Duration: TimeSpan.FromMinutes(2),
            Target: new ScenarioTargetDefinition(ScenarioTargetMode.All),
            AutoRecover: true,
            RecoveryDuration: null);
}
