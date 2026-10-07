using System.Globalization;
using System.Text;
using SgpSimulator.Core.Domain;

namespace SgpSimulator.Core.Output;

public sealed class ShiftFileWriter : IDisposable
{
    private readonly string _outputDirectory;
    private readonly ShiftClock _shiftClock;
    private readonly string _header;
    private readonly int _channelCount;

    private ShiftIdentity? _currentShift;
    private StreamWriter? _writer;

    public ShiftFileWriter(string outputDirectory, ShiftClock shiftClock, int channelCount = SimulatedRow.ChannelCount)
    {
        if (channelCount < 1)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        _outputDirectory = outputDirectory;
        _shiftClock = shiftClock;
        _channelCount = channelCount;
        _header = "(X)\t" + string.Join('\t', Enumerable.Range(1, channelCount).Select(i => $"ch{i}(Y)"));
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Write(SimulatedRow row)
    {
        if (row.Channels.Length != _channelCount)
            throw new InvalidOperationException($"Expected {_channelCount} values, got {row.Channels.Length}.");

        var shift = _shiftClock.Resolve(row.Timestamp);
        if (shift != _currentShift)
        {
            RollTo(shift);
        }

        _writer!.WriteLine(FormatRow(row));
    }

    private void RollTo(ShiftIdentity shift)
    {
        _writer?.Flush();
        _writer?.Dispose();

        var path = Path.Combine(_outputDirectory, shift.ToFileName());
        var isNewFile = !File.Exists(path);
        if (!isNewFile)
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != _header)
                throw new InvalidOperationException($"Existing shift file '{path}' has a different tag count or header.");
        }
        _writer = new StreamWriter(path, append: true, Encoding.ASCII) { AutoFlush = false };
        if (isNewFile)
        {
            _writer.WriteLine(_header);
        }

        _currentShift = shift;
    }

    private static string FormatRow(SimulatedRow row)
    {
        var sb = new StringBuilder();
        sb.Append(row.Timestamp.ToOADate().ToString("F10", CultureInfo.InvariantCulture));
        foreach (var value in row.Channels)
        {
            sb.Append('\t');
            sb.Append(FormatValue(value));
        }

        return sb.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        double d => d.ToString("0.00", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    public void Flush() => _writer?.Flush();

    public void Dispose()
    {
        _writer?.Flush();
        _writer?.Dispose();
    }
}
