using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.OpcLogger;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Configurator;

public sealed record LiveSimulationStatus(
    bool Running,
    string? FileName,
    string? ProcessId,
    string? EquipmentName,
    string? OutputFile,
    long RowsWritten,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastWrittenAt,
    string? Error);

public sealed record EquipmentInfo(string ProcessId, string EquipmentName);

public sealed class LiveSimulationManager(
    SimulatorOptions options,
    string settingsPath,
    string outputRoot,
    ILogger<LiveSimulationManager> logger) : IAsyncDisposable
{
    private readonly object _gate = new();
    private LiveSimulationStatus _status = new(false, null, null, null, null, 0, null, null, null);
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    public IReadOnlyList<EquipmentInfo> Equipment
    {
        get
        {
            lock (_gate)
                return options.GetEffectiveProcesses()
                    .Select(process => new EquipmentInfo(process.ProcessId, process.EffectiveEquipmentName))
                    .ToArray();
        }
    }

    public EquipmentInfo RenameEquipment(string processId, string? equipmentName)
    {
        var name = equipmentName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 64 || name.Contains("..", StringComparison.Ordinal) ||
            !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new ArgumentException(
                "Use an equipment name with letters, numbers, dots, hyphens, or underscores (64 characters max).");

        lock (_gate)
        {
            var processes = options.GetEffectiveProcesses();
            var process = processes.SingleOrDefault(item => item.ProcessId == processId)
                ?? throw new ArgumentException($"Unknown process '{processId}'.");
            if (_status.Running && _status.ProcessId == processId)
                throw new InvalidOperationException("Stop the simulation before renaming its equipment.");
            if (processes.Any(item => item.ProcessId != processId &&
                    string.Equals(item.EffectiveEquipmentName, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Equipment name '{name}' is already used by another process.");

            SaveEquipmentName(processId, name);
            if (options.Processes.Length > 0) process.EquipmentName = name;
            else options.EquipmentName = name;
            return new EquipmentInfo(processId, name);
        }
    }

    private void SaveEquipmentName(string processId, string name)
    {
        var root = JsonNode.Parse(File.ReadAllText(settingsPath),
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            })?.AsObject() ?? throw new InvalidOperationException("The simulator settings file is empty.");
        var simulator = root[SimulatorOptions.SectionName]?.AsObject()
            ?? throw new InvalidOperationException("The simulator settings file has no Simulator section.");
        if (options.Processes.Length > 0)
        {
            var entry = simulator["Processes"]?.AsArray().OfType<JsonObject>()
                .SingleOrDefault(item => item["ProcessId"]?.GetValue<string>() == processId)
                ?? throw new InvalidOperationException($"Process '{processId}' is not in the settings file.");
            entry["EquipmentName"] = name;
        }
        else simulator["EquipmentName"] = name;

        var temporaryPath = settingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

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
                process.EffectiveEquipmentName);
            var clock = new ShiftClock(process.Shift ?? options.Shift);
            _cancellation = new CancellationTokenSource();
            _status = new LiveSimulationStatus(true, Path.GetFileName(configPath), processId,
                process.EffectiveEquipmentName, null, 0, started, null, null);
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
