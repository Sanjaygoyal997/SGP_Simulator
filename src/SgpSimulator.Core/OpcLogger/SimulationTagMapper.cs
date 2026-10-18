using System.Globalization;
using SgpSimulator.Core.Domain;

namespace SgpSimulator.Core.OpcLogger;

/// <summary>Where an equipment is in its recipe schedule.</summary>
public sealed record ScheduleState(string Recipe, int Step, int StepCount, int Cycle, int Cycles, bool Idle);

public sealed class SimulationTagMapper
{
    private readonly OpcTag[] _tags;
    private readonly int?[] _channels;
    private readonly Dictionary<string, IReadOnlyList<string[]>> _dataRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SimulationDataColumn>> _dataColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _recipeNameColumns = new(StringComparer.Ordinal);
    private readonly OpcScheduleStep[] _steps;
    private readonly bool _repeat;
    private readonly Dictionary<string, OpcRecipe> _recipes = new(StringComparer.Ordinal);

    public SimulationTagMapper(OpcTagGroup group, SimulationDataCatalog? dataCatalog = null,
        IReadOnlyList<OpcRecipe>? recipes = null)
    {
        if (group.Tags.Count == 0)
            throw new InvalidOperationException($"Tag group '{group.Name}' has no tags.");

        _tags = group.Tags.ToArray();
        _channels = _tags.Select(ResolveChannel).ToArray();
        _steps = group.Schedule?.Steps.ToArray() ?? [];
        _repeat = group.Schedule?.Repeat ?? false;
        foreach (var recipe in recipes ?? []) _recipes[recipe.Name] = recipe;

        var files = new List<string>();
        foreach (var tag in _tags.Where(tag => tag.SimulationKind == SimulationKind.DataFile))
            files.Add(tag.SimulationFile);
        foreach (var step in _steps)
        {
            if (step.Cycles < 1)
                throw new InvalidOperationException($"Schedule step '{step.Recipe}' needs at least one cycle.");
            if (!_recipes.TryGetValue(step.Recipe, out var recipe))
                throw new InvalidOperationException($"Schedule recipe '{step.Recipe}' is not defined.");
            if (!string.IsNullOrWhiteSpace(recipe.ReferenceFile)) files.Add(recipe.ReferenceFile);
        }

        if (files.Count > 0)
        {
            if (dataCatalog is null) throw new InvalidOperationException("A data directory is required for data-file simulation.");
            var available = dataCatalog.List().ToDictionary(file => file.Id, StringComparer.Ordinal);
            foreach (var id in files.Distinct(StringComparer.Ordinal))
            {
                if (!available.TryGetValue(id, out var file))
                    throw new InvalidOperationException($"Data file '{id}' is not available.");
                var rows = dataCatalog.LoadRows(id);
                _dataRows[id] = rows;
                _dataColumns[id] = file.Columns;
                _recipeNameColumns[id] = FindRecipeNameColumns(rows);
            }

            foreach (var tag in _tags.Where(tag => tag.SimulationKind == SimulationKind.DataFile))
                ColumnIndex(tag.SimulationFile, tag);
            foreach (var step in _steps)
            {
                var file = _recipes[step.Recipe].ReferenceFile;
                if (string.IsNullOrWhiteSpace(file)) continue;
                foreach (var tag in _tags.Where(tag => tag.SimulationKind == SimulationKind.DataFile))
                    ColumnIndex(file, tag);
                if (!new SimulationPatternGenerator(_dataRows[file], new Random(0)).HasBinaryState)
                    throw new InvalidOperationException(
                        $"Recipe '{step.Recipe}' reference file '{file}' needs a 0/1 running state in its first column to count cycles.");
            }
        }
    }

    public int TagCount => _tags.Length;

    public bool HasSchedule => _steps.Length > 0;

    public Session CreateSession(int? seed = null) => new(this, seed);

    private int ColumnIndex(string file, OpcTag tag)
    {
        var index = _dataColumns[file].ToList().FindIndex(column => column.Name == tag.SimulationColumn);
        if (index < 0)
            throw new InvalidOperationException(
                $"Tag '{tag.Name}' column '{tag.SimulationColumn}' is not in data file '{file}'.");
        return index;
    }

    // Columns holding one non-numeric value throughout (for example the recorded recipe name).
    private static HashSet<int> FindRecipeNameColumns(IReadOnlyList<string[]> rows)
    {
        var result = new HashSet<int>();
        for (var column = 1; column < rows[0].Length; column++)
        {
            string? value = null;
            var constant = true;
            foreach (var row in rows)
            {
                var text = row[column];
                if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
                if (value is null) value = text;
                else if (value != text) { constant = false; break; }
            }
            if (constant && value is not null &&
                !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                result.Add(column - 1);
        }
        return result;
    }

    public sealed class Session
    {
        private readonly SimulationTagMapper _mapper;
        private readonly Dictionary<string, SimulationPatternGenerator> _generators;
        private readonly Dictionary<string, object?[]> _samples = new(StringComparer.Ordinal);
        private readonly string? _defaultFile;
        private int _step;
        private int _cyclesAtStepStart;
        private int _sourceCycles;
        private bool _sourceWasRunning;
        private bool _idle;

        internal Session(SimulationTagMapper mapper, int? seed)
        {
            _mapper = mapper;
            var random = seed is int value ? new Random(value) : new Random();
            _generators = mapper._dataRows.ToDictionary(pair => pair.Key,
                pair => new SimulationPatternGenerator(pair.Value, random), StringComparer.Ordinal);
            _defaultFile = mapper._tags.FirstOrDefault(tag => tag.SimulationKind == SimulationKind.DataFile)
                ?.SimulationFile;
        }

        /// <summary>Schedule progress after the last mapped row, or null without a schedule.</summary>
        public ScheduleState? Schedule => _mapper._steps.Length == 0 ? null :
            new ScheduleState(CurrentStep.Recipe, _step + 1, _mapper._steps.Length,
                Math.Min(Cycles - _cyclesAtStepStart, CurrentStep.Cycles), CurrentStep.Cycles, _idle);

        private OpcScheduleStep CurrentStep => _mapper._steps[_step];

        private string? ActiveFile
        {
            get
            {
                if (_mapper._steps.Length == 0) return null;
                var file = _mapper._recipes[CurrentStep.Recipe].ReferenceFile;
                return string.IsNullOrWhiteSpace(file) ? _defaultFile : file;
            }
        }

        // Cycles are counted on the active recipe's data pattern, or on the generated running state.
        private int Cycles => ActiveFile is { } file ? _generators[file].CompletedCycles : _sourceCycles;

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
            var overrideFile = scheduled && !string.IsNullOrWhiteSpace(_mapper._recipes[recipeName!].ReferenceFile)
                ? _mapper._recipes[recipeName!].ReferenceFile : null;
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
                    SimulationKind.DataFile => DataValue(tag, overrideFile ?? tag.SimulationFile, recipeName),
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

        private object? DataValue(OpcTag tag, string file, string? recipeName)
        {
            var column = _mapper.ColumnIndex(file, tag);
            return recipeName is not null && _mapper._recipeNameColumns[file].Contains(column)
                ? recipeName : _samples[file][column];
        }

        private void AdvanceSchedule()
        {
            if (_idle || Cycles - _cyclesAtStepStart < CurrentStep.Cycles) return;

            var previousFile = ActiveFile;
            if (_step + 1 < _mapper._steps.Length) _step++;
            else if (_mapper._repeat) _step = 0;
            else
            {
                _idle = true;
                if (previousFile is not null) _generators[previousFile].Idle = true;
                return;
            }

            // The next recipe starts with a changeover stop, then its first running cycle.
            if (ActiveFile is { } file && file != previousFile)
            {
                _generators[file].BeginStop();
                _samples[file] = _generators[file].Next();
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
