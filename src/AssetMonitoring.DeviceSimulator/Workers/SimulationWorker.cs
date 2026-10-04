using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Load;
using AssetMonitoring.DeviceSimulator.Preparation;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AssetMonitoring.DeviceSimulator.Workers;

/// <summary>
/// Prepares the simulation plan, device catalog, active devices,
/// and per-device temperature scenario sequences.
/// Coordinates concurrent heartbeat and telemetry execution.
/// </summary>
public sealed class SimulationWorker : BackgroundService
{
    private readonly ILogger<SimulationWorker> _logger;
    private readonly SimulationPlanLoader _planLoader;
    private readonly IOptions<DeviceSimulatorOptions> _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IDeviceSimulatorApiClient _apiClient;
    private readonly DevicePreparationService _devicePreparationService;
    private readonly DeviceHeartbeatCoordinator _heartbeatCoordinator;
    private readonly DeviceTelemetryCoordinator _telemetryCoordinator;
    private readonly SimulationPlanResolver _planResolver;
    private readonly DeviceScenarioScheduleFactory _deviceScenarioScheduleFactory;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="SimulationWorker"/> class.
    /// </summary>
    /// <param name="planLoader">
    /// The loader used to read and validate the selected simulation plan.
    /// </param>
    /// <param name="options">
    /// The configuration containing the selected plan name.
    /// </param>
    /// <param name="logger">
    /// The logger used to report startup results and failures.
    /// </param>
    /// <param name="applicationLifetime">
    /// The service used to request application shutdown.
    /// </param>
    /// <param name="apiClient">
    /// The client used to communicate with the Asset Monitoring API
    /// during simulator startup.
    /// </param>
    /// <param name="devicePreparationService">
    /// The service used to select eligible devices and activate
    /// registered devices before simulation starts.
    /// </param>
    /// <param name="heartbeatCoordinator">
    /// The coordinator used to run heartbeat loops
    /// for the prepared devices.
    /// </param>
    /// <param name="telemetryCoordinator">
    /// The coordinator used to run telemetry loops
    /// for the prepared devices.
    /// </param>
    /// <param name="planResolver">
    /// The resolver used to associate simulation scenarios
    /// with compatible prepared devices.
    /// </param>
    /// <param name="deviceScenarioScheduleFactory">
    /// The factory used to prepare independent scenario schedules
    /// for selected devices before telemetry execution starts.
    /// Each schedule contains one sequence per affected telemetry metric.
    /// </param>
    /// <param name="time">
    /// The shared time provider used to capture the simulation start timestamp.
    /// Must be the same provider used by the telemetry runner
    /// to calculate elapsed scenario time.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any constructor dependency is null.
    /// </exception>
    public SimulationWorker(ILogger<SimulationWorker> logger, SimulationPlanLoader planLoader, IOptions<DeviceSimulatorOptions> options, IHostApplicationLifetime applicationLifetime,
        IDeviceSimulatorApiClient apiClient, DevicePreparationService devicePreparationService, DeviceHeartbeatCoordinator heartbeatCoordinator, DeviceTelemetryCoordinator telemetryCoordinator
        , SimulationPlanResolver planResolver, DeviceScenarioScheduleFactory deviceScenarioScheduleFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(planLoader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(devicePreparationService);
        ArgumentNullException.ThrowIfNull(heartbeatCoordinator);
        ArgumentNullException.ThrowIfNull(telemetryCoordinator);
        ArgumentNullException.ThrowIfNull(planResolver);
        ArgumentNullException.ThrowIfNull(deviceScenarioScheduleFactory);
        ArgumentNullException.ThrowIfNull(time);

        _logger = logger;
        _planLoader = planLoader;
        _options = options;
        _applicationLifetime = applicationLifetime;
        _apiClient = apiClient;
        _devicePreparationService = devicePreparationService;
        _heartbeatCoordinator = heartbeatCoordinator;
        _telemetryCoordinator = telemetryCoordinator;
        _planResolver = planResolver;
        _deviceScenarioScheduleFactory = deviceScenarioScheduleFactory;
        _time = time;
    }

    /// <summary>
    /// Loads and validates the simulation plan, synchronizes the device catalog,
    /// prepares eligible devices, resolves scenario targets, and creates
    /// per-device temperature scenario sequences.
    /// Runs heartbeat and telemetry loops concurrently.
    /// </summary>
    /// <remarks>
    /// Temperature scenario sequences are created once before either
    /// runtime group starts. The plan seed, when provided, initializes
    /// the random source used to select abnormal temperature targets.
    ///
    /// A shared simulation start timestamp is captured after preparation
    /// and passed to the telemetry coordinator for all device loops.
    ///
    /// Handled startup failures or the absence of eligible devices
    /// request application shutdown. Unhandled preparation exceptions
    /// propagate to the host.
    ///
    /// After both runtime groups have ended, application shutdown is
    /// requested unless host cancellation has already been requested.
    /// </remarks>
    /// <param name="stoppingToken">
    /// A token that signals host shutdown and is forwarded to asynchronous
    /// preparation operations and both runtime coordinators.
    /// </param>
    /// <returns>
    /// A task representing startup preparation and the lifetime of both
    /// runtime groups. Completes when preparation stops early or all
    /// heartbeat and telemetry loops have ended.
    /// </returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plan = await TryLoadPlanAsync(stoppingToken);
        if (plan == null)
        {
            return;
        }

        var synchronized = await TrySynchronizeDeviceCatalogAsync(stoppingToken);
        if (!synchronized)
        {
            return;
        }

        var devices = await TryGetDevicesAsync(stoppingToken);
        if (devices is null)
        {
            return;
        }

        var preparedDevices = await TryPrepareDevicesAsync(devices, stoppingToken);
        if (preparedDevices is null)
        {
            return;
        }

        var resolvedScenarios = TryResolveScenarios(plan, preparedDevices);
        if (resolvedScenarios is null)
        {
            return;
        }

        var random = plan.Seed.HasValue ? new Random(plan.Seed.Value) : new Random();
        var sequencesByDevice = TryCreateScenarioSchedules(resolvedScenarios, random);
        if (sequencesByDevice is null)
        {
            return;
        }

        var simulationStartedAt = _time.GetTimestamp();

        await Task.WhenAll(
            _heartbeatCoordinator.RunAsync(preparedDevices, stoppingToken),
            _telemetryCoordinator.RunAsync(preparedDevices, sequencesByDevice, simulationStartedAt, stoppingToken));

        if (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("All device heartbeat and telemetry loops have ended. Simulation will stop.");

            _applicationLifetime.StopApplication();
        }
    }

    /// <summary>
    /// Attempts to load and validate the selected simulation plan.
    /// Logs validation errors and expected loading failures,
    /// requesting application shutdown when preparation fails.
    /// </summary>
    /// <param name="stoppingToken">
    /// A token that signals that the host is stopping.
    /// </param>
    /// <returns>
    /// The validated simulation plan, or null when preparation fails
    /// or loading is canceled.
    /// </returns>
    private async Task<SimulationPlanDefinition?> TryLoadPlanAsync(CancellationToken stoppingToken)
    {
        try
        {
            var loadResult = await _planLoader.LoadAsync(_options.Value.PlanName, stoppingToken);

            if (!loadResult.IsValid)
            {
                foreach (var error in loadResult.Validation.Errors)
                {
                    _logger.LogError("Simulation plan validation failed: {Code} at {Path}. {Message}", error.Code, error.Path, error.Message);
                }

                _applicationLifetime.StopApplication();
                return null;
            }
            _logger.LogInformation("Simulation plan {PlanName} was loaded and validated successfully.", _options.Value.PlanName);

            return loadResult.Plan;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Simulation plan loading was canceled.");
            return null;
        }
        catch (Exception exception) when (exception
        is IOException
        or UnauthorizedAccessException
        or JsonException)
        {
            _logger.LogError(exception, "Simulation plan {PlanName} could not be loaded.", _options.Value.PlanName);
            _applicationLifetime.StopApplication();
            return null;
        }
    }

    /// <summary>
    /// Attempts to synchronize the device catalog through the API.
    /// Logs validation errors and expected request failures,
    /// requesting application shutdown when synchronization fails.
    /// </summary>
    /// <param name="stoppingToken">
    /// A token that signals that the host is stopping.
    /// </param>
    /// <returns>
    /// True when synchronization was applied; otherwise, false,
    /// including when the operation is canceled.
    /// </returns>
    private async Task<bool> TrySynchronizeDeviceCatalogAsync(CancellationToken stoppingToken)
    {
        try
        {
            var synchronizeResult = await _apiClient.SynchronizeDeviceCatalogAsync(stoppingToken);

            if (synchronizeResult.Applied == false)
            {
                foreach (var error in synchronizeResult.ValidationResult.Errors)
                {
                    _logger.LogError("Device catalog validation failed: {Code} at device {DeviceCode}, property {PropertyName}. {Message}",
                        error.ErrorCode, error.DeviceCode, error.PropertyName, error.Message);
                }
                _applicationLifetime.StopApplication();
                return false;
            }

            _logger.LogInformation("Device catalog synchronization completed. Created: {Created}, Updated: {Updated}, Unchanged: {Unchanged}, Retired: {Retired}, Restored: {Restored}.",
                synchronizeResult.Created, synchronizeResult.Updated, synchronizeResult.Unchanged, synchronizeResult.Retired, synchronizeResult.Restored);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Device catalog synchronization was canceled.");
            return false;
        }
        catch (Exception exception) when (exception
        is HttpRequestException
        or JsonException
        or OperationCanceledException)
        {
            _logger.LogError(exception, "Device catalog synchronization failed.");
            _applicationLifetime.StopApplication();
            return false;
        }
    }

    /// <summary>
    /// Attempts to retrieve devices from the API.
    /// Requests application shutdown when no devices are available
    /// or an expected retrieval failure occurs.
    /// </summary>
    /// <param name="stoppingToken">
    /// A token that signals that the host is stopping.
    /// </param>
    /// <returns>
    /// A nonempty device list, or null when retrieval fails,
    /// returns no devices, or is canceled.
    /// </returns>
    private async Task<IReadOnlyList<SimulatorDeviceResponse>?> TryGetDevicesAsync(CancellationToken stoppingToken)
    {
        try
        {
            var devices = await _apiClient.GetDevicesAsync(stoppingToken);

            if (devices.Count == 0)
            {
                _logger.LogWarning("No devices were returned by the API. Simulation cannot start.");

                _applicationLifetime.StopApplication();
                return null;
            }
            _logger.LogInformation("Retrieved {DeviceCount} devices from the API.", devices.Count);
            return devices;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Device retrieval was canceled.");
            return null;
        }
        catch (Exception exception) when (exception
        is HttpRequestException
        or JsonException
        or OperationCanceledException)
        {
            _logger.LogError(exception, "Device retrieval failed.");
            _applicationLifetime.StopApplication();
            return null;
        }
    }

    /// <summary>
    /// Attempts to prepare devices for simulation.
    /// Logs expected preparation failures and requests application shutdown
    /// when preparation fails or no eligible devices are available.
    /// </summary>
    /// <param name="devices">
    /// The devices retrieved from the API.
    /// </param>
    /// <param name="stoppingToken">
    /// A token that signals that the host is stopping.
    /// </param>
    /// <returns>
    /// A nonempty collection of prepared devices, or null when preparation
    /// fails, is canceled, or produces no eligible devices.
    /// </returns>
    private async Task<IReadOnlyList<SimulatorDeviceResponse>?> TryPrepareDevicesAsync(IReadOnlyList<SimulatorDeviceResponse> devices, CancellationToken stoppingToken = default)
    {
        try
        {
            var result = await _devicePreparationService.PrepareAsync(devices, stoppingToken);

            if (result.Count == 0)
            {
                _logger.LogWarning("No eligible devices are available after preparation. Simulation cannot start.");

                _applicationLifetime.StopApplication();
                return null;
            }

            _logger.LogInformation("Device preparation completed. {DeviceCount} devices are ready for simulation.", result.Count);

            foreach (var device in result)
            {
                _logger.LogInformation("Device {DeviceCode} is ready for simulation. ID: {DeviceId}, Name: {DeviceName}, Lifecycle: {Lifecycle}.",
                    device.Code, device.Id, device.Name, device.Lifecycle);
            }

            return result;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Device preparation was canceled.");
            return null;
        }
        catch (Exception exception) when (exception
        is HttpRequestException
        or JsonException
        or OperationCanceledException
        or InvalidOperationException)
        {
            _logger.LogError(exception, "Device preparation failed. Simulation startup will stop.");
            _applicationLifetime.StopApplication();
            return null;
        }
    }

    /// <summary>
    /// Attempts to resolve scenario targets using the prepared devices.
    /// Logs the resolution result and requests application shutdown
    /// when an expected resolution failure occurs.
    /// </summary>
    /// <param name="plan">
    /// The validated simulation plan whose scenario targets are resolved.
    /// </param>
    /// <param name="devices">
    /// The prepared active devices available for scenario target selection.
    /// </param>
    /// <returns>
    /// The resolved scenarios, or null when resolution fails.
    /// An empty collection when the plan contains no scenarios.
    /// </returns>
    private IReadOnlyList<ResolvedScenario>? TryResolveScenarios(SimulationPlanDefinition plan, IReadOnlyList<SimulatorDeviceResponse> devices)
    {
        try
        {
            var result = _planResolver.Resolve(plan, devices);
            _logger.LogInformation("Simulation plan {PlanName} was resolved. Scenarios: {ScenarioCount}.", plan.Name, result.Count);
            LogScenarioAssigments(result);
            return result;
        }
        catch (Exception exception) when (exception
            is ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            _logger.LogError(exception, "Simulation plan {PlanName} could not be resolved.",
                plan.Name);

            _applicationLifetime.StopApplication();
            return null;
        }
    }

    private void LogScenarioAssigments(IReadOnlyList<ResolvedScenario> scenarios)
    {
        foreach (var scenario in scenarios)
        {
            var definition = scenario.Definition;

            foreach (var device in scenario.Devices)
            {
                _logger.LogInformation("Scenario {ScenarioName} ({ScenarioType}) targets device {DeviceCode} ({DeviceId}). Starts after {StartsAfter}, duration {Duration}.",
                    definition.Name, definition.GetType().Name, device.Code, device.Id, definition.StartsAfter, definition.Duration);
            }
        }
    }

    /// <summary>
    /// Attempts to create per-device scenario schedules
    /// from resolved scenarios.
    /// Logs the preparation result and requests application shutdown
    /// when an expected creation failure occurs.
    /// </summary>
    /// <param name="resolvedScenarios">
    /// The resolved scenarios in plan order, containing validated
    /// definitions and the devices selected for each scenario.
    /// </param>
    /// <param name="random">
    /// The random source used to select abnormal measurement targets
    /// while creating independent scenario runtimes.
    /// </param>
    /// <returns>
    /// A dictionary of scenario schedules keyed by device identifier,
    /// or null when an expected creation failure occurs.
    /// An empty dictionary is a successful preparation result.
    /// </returns>
    /// <remarks>
    /// Call once during startup, before starting heartbeat or telemetry loops.
    /// Unexpected exceptions propagate to the caller.
    /// </remarks>
    private IReadOnlyDictionary<Guid, DeviceScenarioSchedule>? TryCreateScenarioSchedules(IReadOnlyList<ResolvedScenario> resolvedScenarios, Random random)
    {
        try
        {
            var result = _deviceScenarioScheduleFactory.Create(resolvedScenarios, random);

            _logger.LogInformation("Scenario schedule preparation completed. Resolved scenarios: {ScenarioCount}, devices with schedules: {DeviceCount}.",
                resolvedScenarios.Count, result.Count);

            return result;
        }
        catch (Exception exception) when (exception
            is ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            _logger.LogError( exception,"Scenario schedules could not be created. Simulation startup will stop.");

            _applicationLifetime.StopApplication();
            return null;
        }
    }
}
