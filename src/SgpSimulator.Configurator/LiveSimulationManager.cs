using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.OpcLogger;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Configurator;

public sealed record LiveSimulationStatus(
    bool Running,
    string? FileName,
    string? ProcessId,
    string? OutputFile,
    long RowsWritten,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastWrittenAt,
    string? Error);

public sealed class LiveSimulationManager(
    SimulatorOptions options,
    string outputRoot,
    ILogger<LiveSimulationManager> logger) : IAsyncDisposable
{
    private readonly object _gate = new();
    private LiveSimulationStatus _status = new(false, null, null, null, 0, null, null, null);
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    public IReadOnlyList<string> ProcessIds => options.GetEffectiveProcesses()
        .Select(process => process.ProcessId).ToArray();

    public LiveSimulationStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public LiveSimulationStatus Start(string configPath, string processId)
    {
        lock (_gate)
        {
            if (_status.Running)
                throw new InvalidOperationException("A simulation is already running.");

            var process = options.GetEffectiveProcesses().SingleOrDefault(item => item.ProcessId == processId)
                ?? throw new ArgumentException($"Unknown process '{processId}'.");
            var config = OpcLoggerConfig.Load(configPath);
            var groups = config.Projects.SelectMany(project => project.Groups)
                .SelectMany(group => group.TagGroups).Where(group => group.Enabled).ToArray();
            if (groups.Length != 1)
                throw new InvalidOperationException("The XML needs exactly one enabled tag group.");
            if (groups[0].RuntimeMode != RuntimeMode.Simulation)
                throw new InvalidOperationException("Select Simulation mode before starting a simulation.");

            var catalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(configPath));
            var mapper = new SimulationTagMapper(groups[0], catalog);
            var recipe = options.GetRecipe(process.ActiveRecipe);
            var tickInterval = process.TickIntervalMs ?? options.TickIntervalMs;
            if (tickInterval <= 0) throw new InvalidOperationException("Tick interval must be positive.");

            var started = DateTimeOffset.Now;
            var directory = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(configPath),
                process.ProcessId);
            var clock = new ShiftClock(process.Shift ?? options.Shift);
            _cancellation = new CancellationTokenSource();
            _status = new LiveSimulationStatus(true, Path.GetFileName(configPath), processId,
                null, 0, started, null, null);
            _task = Task.Run(() => RunAsync(recipe, mapper, clock, directory, tickInterval,
                _cancellation.Token));
            return _status;
        }
    }

    public async Task<LiveSimulationStatus> StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            if (!_status.Running) return _status;
            _cancellation?.Cancel();
            task = _task;
        }
        if (task is not null) await task;
        return GetStatus();
    }

    private async Task RunAsync(RecipeProfile recipe, SimulationTagMapper mapper, ShiftClock clock,
        string directory, int tickInterval, CancellationToken cancellationToken)
    {
        try
        {
            var simulator = new ProcessSimulator(recipe);
            var session = mapper.CreateSession();
            using var writer = new ShiftFileWriter(directory, clock, mapper.TagCount);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickInterval));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTime.Now;
                writer.Write(session.Map(simulator.Tick(now)));
                writer.Flush();
                var outputFile = Path.Combine(directory, clock.Resolve(now).ToFileName());
                lock (_gate)
                {
                    _status = _status with
                    {
                        RowsWritten = _status.RowsWritten + 1,
                        LastWrittenAt = DateTimeOffset.Now,
                        OutputFile = outputFile
                    };
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Live simulation stopped with an error");
            lock (_gate) _status = _status with { Error = error.Message };
        }
        finally
        {
            lock (_gate) _status = _status with { Running = false };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellation?.Dispose();
    }
}
