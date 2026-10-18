using System.Globalization;
using SgpSimulator.Core.Domain;

namespace SgpSimulator.Core.OpcLogger;

/// <summary>Where an equipment is in its recipe schedule.</summary>
public sealed record ScheduleState(string Recipe, int Step, int StepCount, int Cycle, int Cycles, bool Idle,
    bool? RecordedData = null);

public sealed class SimulationTagMapper
{
    private readonly OpcTag[] _tags;
    private readonly int?[] _channels;
    private readonly Dictionary<string, IReadOnlyList<string[]>> _dataRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SimulationDataColumn>> _dataColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _recipeNameColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataSource> _sources = new(StringComparer.Ordinal);
    private readonly OpcScheduleStep[] _steps;
    private readonly string?[] _stepSources;
    private readonly bool _repeat;
    private readonly Dictionary<string, OpcRecipe> _recipes = new(StringComparer.Ordinal);
    private readonly SimulationDataCatalog? _catalog;
    private IReadOnlyList<SimulationDataFile>? _catalogFiles;

    public SimulationTagMapper(OpcTagGroup group, SimulationDataCatalog? dataCatalog = null,
        IReadOnlyList<OpcRecipe>? recipes = null)
    {
        if (group.Tags.Count == 0)
            throw new InvalidOperationException($"Tag group '{group.Name}' has no tags.");

        _tags = group.Tags.ToArray();
        _channels = _tags.Select(ResolveChannel).ToArray();
        _steps = group.Schedule?.Steps.ToArray() ?? [];
        _repeat = group.Schedule?.Repeat ?? false;
        _catalog = dataCatalog;
        foreach (var recipe in recipes ?? []) _recipes[recipe.Name] = recipe;

        var dataTags = _tags.Where(tag => tag.SimulationKind == SimulationKind.DataFile).ToArray();
        if (dataTags.Length > 0 && dataCatalog is null)
            throw new InvalidOperationException("A data directory is required for data-file simulation.");
        foreach (var tag in dataTags)
        {
            LoadFile(tag.SimulationFile);
            ColumnIndex(tag.SimulationFile, tag);
            _sources.TryAdd(tag.SimulationFile, new DataSource(tag.SimulationFile, tag.SimulationFile, null, false));
        }

        _stepSources = new string?[_steps.Length];
        for (var i = 0; i < _steps.Length; i++)
        {
            var step = _steps[i];
            if (step.Cycles < 1)
                throw new InvalidOperationException($"Schedule step '{step.Recipe}' needs at least one cycle.");
            if (!_recipes.TryGetValue(step.Recipe, out var recipe))
                throw new InvalidOperationException($"Schedule recipe '{step.Recipe}' is not defined.");
            _stepSources[i] = ResolveRecipeSource(recipe, dataTags);
        }
    }

    public int TagCount => _tags.Length;

    public bool HasSchedule => _steps.Length > 0;

    public Session CreateSession(int? seed = null) => new(this, seed);

    /// <summary>
    /// Finds the data a recipe runs on: the rows recorded while it was running, in its own file if it names
    /// one, else in the equipment's reference file or another reference file with the same columns. Without
    /// recorded rows the whole file is used and only the recipe name changes.
    /// </summary>
    private string? ResolveRecipeSource(OpcRecipe recipe, OpcTag[] dataTags)
    {
        var defaultFile = dataTags.FirstOrDefault()?.SimulationFile;
        var explicitFile = string.IsNullOrWhiteSpace(recipe.ReferenceFile) ? null : recipe.ReferenceFile;
        if (explicitFile is null && defaultFile is null) return null;
        if (_catalog is null) throw new InvalidOperationException("A data directory is required for recipe data.");

        IEnumerable<string> candidates = explicitFile is not null ? [explicitFile] :
            new[] { defaultFile! }.Concat(CatalogFiles()
                .Where(file => dataTags.All(tag => file.Columns.Any(column => column.Name == tag.SimulationColumn)))
                .Select(file => file.Id).Where(id => id != defaultFile));
        foreach (var file in candidates)
        {
            var rows = LoadFile(file);
            var key = RecordedRecipes.KeyColumn(rows, _recipes.Keys);
            if (key is null) continue;
            var ranges = RecordedRecipes.Ranges(rows, key.Value, recipe.Name);
            if (ranges.Count == 0 ||
                !new SimulationPatternGenerator(rows, new Random(0), ranges).CanCountCycles) continue;
            foreach (var tag in dataTags) ColumnIndex(file, tag);
            var id = $"{file}|{recipe.Name}";
            _sources.TryAdd(id, new DataSource(id, file, ranges, true));
            return id;
        }

        var fallback = explicitFile ?? defaultFile!;
        LoadFile(fallback);
        foreach (var tag in dataTags) ColumnIndex(fallback, tag);
        if (!new SimulationPatternGenerator(_dataRows[fallback], new Random(0)).CanCountCycles)
            throw new InvalidOperationException(
                $"Data file '{fallback}' needs a 0/1 running state in its first column to count cycles of recipe '{recipe.Name}'.");
        _sources.TryAdd(fallback, new DataSource(fallback, fallback, null, false));
        return fallback;
    }

    private IReadOnlyList<SimulationDataFile> CatalogFiles() => _catalogFiles ??= _catalog!.List();

    private IReadOnlyList<string[]> LoadFile(string id)
    {
        if (_dataRows.TryGetValue(id, out var loaded)) return loaded;
        var file = CatalogFiles().FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException($"Data file '{id}' is not available.");
        var rows = _catalog!.LoadRows(id);
        _dataRows[id] = rows;
        _dataColumns[id] = file.Columns;
        var key = RecordedRecipes.KeyColumn(rows, _recipes.Keys);
        _recipeNameColumns[id] = key is null ? [] :
            RecordedRecipes.NameColumns(rows, key.Value).Select(column => column - 1).ToHashSet();
        return rows;
    }

    private int ColumnIndex(string file, OpcTag tag)
    {
        var index = _dataColumns[file].ToList().FindIndex(column => column.Name == tag.SimulationColumn);
        if (index < 0)
            throw new InvalidOperationException(
                $"Tag '{tag.Name}' column '{tag.SimulationColumn}' is not in data file '{file}'.");
        return index;
    }

    private sealed record DataSource(string Id, string File, IReadOnlyList<RowRange>? Ranges, bool Recorded);

    public sealed class Session
    {
        private readonly SimulationTagMapper _mapper;
        private readonly Dictionary<string, SimulationPatternGenerator> _generators;
        private readonly Dictionary<string, object?[]> _samples = new(StringComparer.Ordinal);
        private int _step;
        private int _cyclesAtStepStart;
        private int _sourceCycles;
        private bool _sourceWasRunning;
        private bool _idle;

        internal Session(SimulationTagMapper mapper, int? seed)
        {
            _mapper = mapper;
            var random = seed is int value ? new Random(value) : new Random();
            _generators = mapper._sources.Values.ToDictionary(source => source.Id,
                source => new SimulationPatternGenerator(mapper._dataRows[source.File], random, source.Ranges),
                StringComparer.Ordinal);
        }

        /// <summary>Schedule progress after the last mapped row, or null without a schedule.</summary>
        public ScheduleState? Schedule => _mapper._steps.Length == 0 ? null :
            new ScheduleState(CurrentStep.Recipe, _step + 1, _mapper._steps.Length,
                Math.Min(Cycles - _cyclesAtStepStart, CurrentStep.Cycles), CurrentStep.Cycles, _idle,
                ActiveSource is { } source ? _mapper._sources[source].Recorded : null);

        private OpcScheduleStep CurrentStep => _mapper._steps[_step];

        private string? ActiveSource => _mapper._steps.Length == 0 ? null : _mapper._stepSources[_step];

        // Cycles are counted on the active recipe's data pattern, or on the generated running state.
        private int Cycles => ActiveSource is { } source ? _generators[source].CompletedCycles : _sourceCycles;

        public SimulatedRow Map(SimulatedRow source)
        {
            foreach (var pair in _generators) _samples[pair.Key] = pair.Value.Next();
            var scheduled = _mapper._steps.Length > 0;
            if (scheduled)
            {
                var running = Convert.ToDouble(source.Channels[0] ?? 0, CultureInfo.InvariantCulture) != 0;
                if (_sourceWasRunning && !running) _sourceCycles++;
                _sourceWasRunning = running;
                // A cycle that just ended moves to the next step on this row, so the stop belongs to it.
                AdvanceSchedule();
            }

            var recipeName = scheduled ? CurrentStep.Recipe : null;
            var activeSource = scheduled ? ActiveSource : null;
            var sourceChannels = source.Channels;
            if (scheduled)
            {
                sourceChannels = (object?[])source.Channels.Clone();
                if (_idle) for (var i = 0; i < 14 && i < sourceChannels.Length; i++) sourceChannels[i] = 0;
                if (sourceChannels.Length > 15) sourceChannels[14] = sourceChannels[15] = recipeName;
            }

            var values = new object?[_mapper._tags.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var tag = _mapper._tags[i];
                values[i] = tag.SimulationKind switch
                {
                    SimulationKind.Constant => tag.SimulationValue,
                    SimulationKind.DataFile => DataValue(tag, activeSource ?? tag.SimulationFile, recipeName),
                    SimulationKind.None when _mapper._channels[i] is null => null,
                    _ when _mapper._channels[i] is int channel => sourceChannels[channel],
                    _ => null
                };

                if (values[i] is IConvertible and not string &&
                    (tag.SimulationMinSpecified || tag.SimulationMaxSpecified))
                {
                    var value = Convert.ToDouble(values[i], CultureInfo.InvariantCulture);
                    values[i] = Math.Clamp(value,
                        tag.SimulationMinSpecified ? tag.SimulationMin : double.NegativeInfinity,
                        tag.SimulationMaxSpecified ? tag.SimulationMax : double.PositiveInfinity);
                }
            }

            return new SimulatedRow { Timestamp = source.Timestamp, Channels = values };
        }

        private object? DataValue(OpcTag tag, string sourceId, string? recipeName)
        {
            var file = _mapper._sources[sourceId].File;
            var column = _mapper.ColumnIndex(file, tag);
            return recipeName is not null && _mapper._recipeNameColumns[file].Contains(column)
                ? recipeName : _samples[sourceId][column];
        }

        private void AdvanceSchedule()
        {
            if (_idle || Cycles - _cyclesAtStepStart < CurrentStep.Cycles) return;

            var previousSource = ActiveSource;
            if (_step + 1 < _mapper._steps.Length) _step++;
            else if (_mapper._repeat) _step = 0;
            else
            {
                _idle = true;
                if (previousSource is not null) _generators[previousSource].Idle = true;
                return;
            }

            // The next recipe starts with a changeover stop, then its first running cycle.
            if (ActiveSource is { } next && next != previousSource)
            {
                _generators[next].BeginStop();
                _samples[next] = _generators[next].Next();
            }
            _cyclesAtStepStart = Cycles;
        }
    }

    private static int? ResolveChannel(OpcTag tag)
    {
        if (tag.SimulationKind is SimulationKind.None or SimulationKind.Constant or SimulationKind.DataFile)
        {
            if (!string.IsNullOrWhiteSpace(tag.SimulationChannel))
                throw new InvalidOperationException($"Tag '{tag.Name}' has a SimulationChannel without a channel simulation kind.");
            return null;
        }

        var value = tag.SimulationChannel;
        if (value.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
            value = value[2..];

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var channel) ||
            channel < 1 || channel > SimulatedRow.ChannelCount)
            throw new InvalidOperationException($"Tag '{tag.Name}' needs a SimulationChannel from ch1 to ch{SimulatedRow.ChannelCount}.");

        var valid = tag.SimulationKind switch
        {
            SimulationKind.BatchRunning => channel <= 14,
            SimulationKind.RecipeName => channel is 15 or 16,
            SimulationKind.StepValue => channel is >= 17 and <= 22,
            SimulationKind.Drift => channel is 27 or 28,
            SimulationKind.Pulse => channel is 29 or 30,
            SimulationKind.Setpoint => channel is 31 or 32,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException($"Tag '{tag.Name}' has incompatible SimulationKind and SimulationChannel '{tag.SimulationChannel}'.");

        return channel - 1;
    }
}
