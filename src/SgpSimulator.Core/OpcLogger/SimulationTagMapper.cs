using System.Globalization;
using SgpSimulator.Core.Domain;

namespace SgpSimulator.Core.OpcLogger;

public sealed class SimulationTagMapper
{
    private readonly OpcTag[] _tags;
    private readonly int?[] _channels;
    private readonly Dictionary<string, IReadOnlyList<string[]>> _dataRows = new(StringComparer.Ordinal);
    private readonly int?[] _dataColumns;

    public SimulationTagMapper(OpcTagGroup group, SimulationDataCatalog? dataCatalog = null)
    {
        if (group.Tags.Count == 0)
            throw new InvalidOperationException($"Tag group '{group.Name}' has no tags.");

        _tags = group.Tags.ToArray();
        _channels = _tags.Select(ResolveChannel).ToArray();
        _dataColumns = new int?[_tags.Length];
        if (_tags.Any(tag => tag.SimulationKind == SimulationKind.DataFile))
        {
            if (dataCatalog is null) throw new InvalidOperationException("A data directory is required for data-file simulation.");
            var files = dataCatalog.List().ToDictionary(file => file.Id, StringComparer.Ordinal);
            for (var i = 0; i < _tags.Length; i++)
            {
                var tag = _tags[i];
                if (tag.SimulationKind != SimulationKind.DataFile) continue;
                if (!files.TryGetValue(tag.SimulationFile, out var file))
                    throw new InvalidOperationException($"Tag '{tag.Name}' references an unavailable data file '{tag.SimulationFile}'.");
                var column = file.Columns.ToList().FindIndex(item => item.Name == tag.SimulationColumn);
                if (column < 0)
                    throw new InvalidOperationException($"Tag '{tag.Name}' references an unavailable column '{tag.SimulationColumn}'.");
                _dataColumns[i] = column + 1;
                if (!_dataRows.ContainsKey(file.Id)) _dataRows[file.Id] = dataCatalog.LoadRows(file.Id);
            }
        }
    }

    public int TagCount => _tags.Length;

    public Session CreateSession(int? seed = null) => new(this, seed);

    public sealed class Session
    {
        private readonly SimulationTagMapper _mapper;
        private readonly Dictionary<string, SimulationPatternGenerator> _generators;
        private readonly Dictionary<string, object?[]> _samples = new(StringComparer.Ordinal);

        internal Session(SimulationTagMapper mapper, int? seed)
        {
            _mapper = mapper;
            var random = seed is int value ? new Random(value) : new Random();
            _generators = mapper._dataRows.ToDictionary(pair => pair.Key,
                pair => new SimulationPatternGenerator(pair.Value, random), StringComparer.Ordinal);
        }

        public SimulatedRow Map(SimulatedRow source)
        {
            foreach (var pair in _generators) _samples[pair.Key] = pair.Value.Next();
            var values = new object?[_mapper._tags.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var tag = _mapper._tags[i];
                values[i] = tag.SimulationKind switch
                {
                    SimulationKind.Constant => tag.SimulationValue,
                    SimulationKind.DataFile when _mapper._dataColumns[i] is int column =>
                        _samples[tag.SimulationFile][column - 1],
                    SimulationKind.None when _mapper._channels[i] is null => null,
                    _ when _mapper._channels[i] is int channel => source.Channels[channel],
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
