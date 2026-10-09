using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.OpcLogger;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Configurator;

public sealed record EquipmentRunStatus(
    string Equipment,
    string ProcessId,
    string? OutputFile,
    long RowsWritten,
    DateTimeOffset? LastWrittenAt,
    bool Running,
    string? Error);

public sealed record LiveSimulationStatus(
    bool Running,
    string? FileName,
    DateTimeOffset? StartedAt,
    IReadOnlyList<EquipmentRunStatus> Equipment);

public sealed record ProcessInfo(string ProcessId, string ActiveRecipe);

public sealed class LiveSimulationManager(
    SimulatorOptions options,
    string outputRoot,
    ILogger<LiveSimulationManager> logger) : IAsyncDisposable
{
    private readonly object _gate = new();
    private LiveSimulationStatus _status = new(false, null, null, []);
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    public IReadOnlyList<ProcessInfo> Processes => options.GetEffectiveProcesses()
        .Select(process => new ProcessInfo(process.ProcessId, process.ActiveRecipe)).ToArray();

    public LiveSimulationStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public LiveSimulationStatus Start(string configPath)
    {
        lock (_gate)
        {
            if (_status.Running)
                throw new InvalidOperationException("A simulation is already running.");

            var config = OpcLoggerConfig.Load(configPath);
            OpcLoggerConfig.ValidateEquipmentNames(config.Equipment);
            var enabled = config.Equipment.Where(group => group.Enabled).ToArray();
            if (enabled.Length == 0)
                throw new InvalidOperationException("Enable at least one equipment before starting a simulation.");
            var realtime = enabled.FirstOrDefault(group => group.RuntimeMode != RuntimeMode.Simulation);
            if (realtime is not null)
                throw new InvalidOperationException(
                    $"Equipment '{realtime.Name}' is in Realtime OPC mode. Select Simulation mode or disable it.");

            // Build every equipment before starting any, so a bad one does not leave others half-started.
            var catalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(configPath));
            var root = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(configPath));
            var runs = enabled.Select(group =>
            {
                var process = options.ResolveProcess(group.ProcessId);
                var tickInterval = process.TickIntervalMs ?? options.TickIntervalMs;
                if (tickInterval <= 0)
                    throw new InvalidOperationException($"Process '{process.ProcessId}' needs a positive tick interval.");
                return new Run(group.Name, process.ProcessId, options.GetRecipe(process.ActiveRecipe),
                    new SimulationTagMapper(group, catalog), new ShiftClock(process.Shift ?? options.Shift),
                    Path.Combine(root, group.Name), tickInterval);
            }).ToArray();

            _cancellation = new CancellationTokenSource();
            _status = new LiveSimulationStatus(true, Path.GetFileName(configPath), DateTimeOffset.Now,
                runs.Select(run => new EquipmentRunStatus(run.Equipment, run.ProcessId, null, 0, null, true, null))
                    .ToArray());
            var token = _cancellation.Token;
            _task = Task.WhenAll(runs.Select((run, index) => Task.Run(() => RunAsync(index, run, token))))
                .ContinueWith(_ => { lock (_gate) _status = _status with { Running = false }; },
                    TaskScheduler.Default);
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

    private async Task RunAsync(int index, Run run, CancellationToken cancellationToken)
    {
        try
        {
            var simulator = new ProcessSimulator(run.Recipe);
            var session = run.Mapper.CreateSession();
            using var writer = new ShiftFileWriter(run.Directory, run.Clock, run.Mapper.TagCount);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(run.TickInterval));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTime.Now;
                writer.Write(session.Map(simulator.Tick(now)));
                writer.Flush();
                var outputFile = Path.Combine(run.Directory, run.Clock.Resolve(now).ToFileName());
                Update(index, item => item with
                {
                    RowsWritten = item.RowsWritten + 1, LastWrittenAt = DateTimeOffset.Now, OutputFile = outputFile
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Live simulation of {Equipment} stopped with an error", run.Equipment);
            Update(index, item => item with { Error = error.Message });
        }
        finally
        {
            Update(index, item => item with { Running = false });
        }
    }

    private void Update(int index, Func<EquipmentRunStatus, EquipmentRunStatus> change)
    {
        lock (_gate)
        {
            var equipment = _status.Equipment.ToArray();
            equipment[index] = change(equipment[index]);
            _status = _status with { Equipment = equipment };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellation?.Dispose();
    }

    private sealed record Run(string Equipment, string ProcessId, RecipeProfile Recipe, SimulationTagMapper Mapper,
        ShiftClock Clock, string Directory, int TickInterval);
}
