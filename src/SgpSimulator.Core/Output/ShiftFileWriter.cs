using System.Globalization;
using System.Text;
using SgpSimulator.Core.Domain;

namespace SgpSimulator.Core.Output;

public sealed class ShiftFileWriter : IDisposable
{
    private static readonly string Header =
        "(X)\t" + string.Join('\t', Enumerable.Range(1, SimulatedRow.ChannelCount).Select(i => $"ch{i}(Y)"));

    private readonly string _outputDirectory;
    private readonly ShiftClock _shiftClock;

    private ShiftIdentity? _currentShift;
    private StreamWriter? _writer;

    public ShiftFileWriter(string outputDirectory, ShiftClock shiftClock)
    {
        _outputDirectory = outputDirectory;
        _shiftClock = shiftClock;
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Write(SimulatedRow row)
    {
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
        _writer = new StreamWriter(path, append: true, Encoding.ASCII) { AutoFlush = false };
        if (isNewFile)
        {
            _writer.WriteLine(Header);
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
