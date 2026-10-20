using System.IO.Compression;
using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.History;
using SgpSimulator.Core.OpcLogger;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Configurator;

public sealed record HistoryEquipmentStatus(string Equipment, long Rows, long TotalRows, int Cycles, string? Error);

public sealed record HistoryStatus(
    bool Running,
    string? FileName,
    DateTime? From,
    DateTime? To,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? OutputFolder,
    bool Cancelled,
    string? Error,
    IReadOnlyList<HistoryEquipmentStatus> Equipment);

/// <summary>
/// Generates past data for a date range as fast as possible (not in real time): per equipment the same shift
/// files and cycle files as a live run, plus cycles.csv with one row per cycle for training models.
/// </summary>
public sealed class HistoryManager(SimulatorOptions options, string outputRoot, ILogger<HistoryManager> logger)
{
    public const int MaxDays = 92;

    private readonly object _gate = new();
    private HistoryStatus _status = new(false, null, null, null, null, null, null, false, null, []);
    private CancellationTokenSource? _cancellation;

    public HistoryStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public HistoryStatus Start(string configPath, DateTime from, DateTime to, IReadOnlyCollection<string>? equipment,
        int? seed, bool repeatSchedule)
    {
        lock (_gate)
        {
            if (_status.Running) throw new InvalidOperationException("Historical data is already being generated.");
            if (to <= from) throw new ArgumentException("The end of the period must be after its start.");
            if ((to - from).TotalDays > MaxDays)
                throw new ArgumentException($"Generate at most {MaxDays} days at a time.");

            var config = OpcLoggerConfig.Load(configPath);
            OpcLoggerConfig.ValidateEquipmentNames(config.Equipment);
            var selected = config.Equipment.Where(group => equipment is { Count: > 0 }
                ? equipment.Contains(group.Name, StringComparer.OrdinalIgnoreCase) : group.Enabled).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Select at least one equipment.");
            var realtime = selected.FirstOrDefault(group => group.RuntimeMode != RuntimeMode.Simulation);
            if (realtime is not null)
                throw new InvalidOperationException($"Equipment '{realtime.Name}' is in Realtime OPC mode.");

            var catalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(configPath));
            var folder = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(configPath),
                DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var jobs = selected.Select((group, index) =>
            {
                // A finite schedule would end early and leave the rest of the period idle.
                if (repeatSchedule && group.Schedule is { } schedule) schedule.Repeat = true;
                var process = options.ResolveProcess(group.ProcessId);
                var tick = process.TickIntervalMs ?? options.TickIntervalMs;
                if (tick <= 0) throw new InvalidOperationException($"Process '{process.ProcessId}' needs a positive tick.");
                return new Job(group, options.GetRecipe(process.ActiveRecipe), new SimulationTagMapper(group, catalog,
                    config.Recipes), new ShiftClock(process.Shift ?? options.Shift), tick,
                    Path.Combine(folder, group.Name), seed is int value ? value + index : null,
                    (long)Math.Ceiling((to - from).TotalMilliseconds / tick));
            }).ToArray();

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _status = new HistoryStatus(true, Path.GetFileName(configPath), from, to, DateTimeOffset.Now, null,
                folder, false, null,
                jobs.Select(job => new HistoryEquipmentStatus(job.Group.Name, 0, job.TotalRows, 0, null)).ToArray());
            _ = Task.WhenAll(jobs.Select((job, index) => Task.Run(() => Generate(index, job, from, to, token))))
                .ContinueWith(_ =>
                {
                    lock (_gate)
                        _status = _status with
                        {
                            Running = false, FinishedAt = DateTimeOffset.Now, Cancelled = token.IsCancellationRequested
                        };
                }, TaskScheduler.Default);
            return _status;
        }
    }

    public HistoryStatus Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
        return GetStatus();
    }

    /// <summary>Zips the last generated folder into a temporary file and returns its path.</summary>
    public string CreateZip()
    {
        var status = GetStatus();
        if (status.Running) throw new InvalidOperationException("Wait until the historical data is complete.");
        if (status.OutputFolder is not { } folder || !Directory.Exists(folder))
            throw new InvalidOperationException("No historical data has been generated yet.");
        var zip = Path.Combine(Path.GetTempPath(), $"sgp-history-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    private void Generate(int index, Job job, DateTime from, DateTime to, CancellationToken token)
    {
        try
        {
            var secondsPerRow = job.TickMs / 1000.0;
            var simulator = new ProcessSimulator(job.Recipe, job.Seed);
            var session = job.Mapper.CreateSession(job.Seed, secondsPerRow);
            using var shifts = new ShiftFileWriter(job.Directory, job.Clock, job.Mapper.TagCount);
            using var cycles = new CycleFileWriter(Path.Combine(job.Directory, "Cycles"), job.Mapper.CycleColumns);
            // Recipe-name columns are text; the cycle table keeps them out of the parameter means.
            using var summary = new CycleSummaryWriter(Path.Combine(job.Directory, "cycles.csv"),
                job.Mapper.CycleColumns.Where(column => !job.Mapper.IsRecipeColumn(column.Index)).ToArray(),
                Math.Max(60, job.Group.BreakdownMinMinutes * 60));
            var interval = TimeSpan.FromMilliseconds(job.TickMs);
            long rows = 0;
            for (var time = from; time < to; time += interval)
            {
                var row = session.Map(simulator.Tick(time));
                var recipe = session.Schedule?.Recipe ?? job.Mapper.RecordedRecipe(row);
                shifts.Write(row);
                cycles.Write(row, session.Running, recipe);
                summary.Write(row, session.Running, recipe, session.CurrentSetSeconds, secondsPerRow);
                if (++rows % 20000 != 0) continue;
                token.ThrowIfCancellationRequested();
                Update(index, item => item with { Rows = rows, Cycles = summary.Cycles });
            }
            Update(index, item => item with { Rows = rows, Cycles = summary.Cycles });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Historical data for {Equipment} failed", job.Group.Name);
            Update(index, item => item with { Error = error.Message });
        }
    }

    private void Update(int index, Func<HistoryEquipmentStatus, HistoryEquipmentStatus> change)
    {
        lock (_gate)
        {
            var equipment = _status.Equipment.ToArray();
            equipment[index] = change(equipment[index]);
            _status = _status with { Equipment = equipment };
        }
    }

    private sealed record Job(OpcTagGroup Group, RecipeProfile Recipe, SimulationTagMapper Mapper, ShiftClock Clock,
        int TickMs, string Directory, int? Seed, long TotalRows);
}
