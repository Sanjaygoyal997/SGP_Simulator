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

    private readonly bool _keepOpen;

    private ShiftIdentity? _currentShift;
    private string? _currentPath;
    private StreamWriter? _writer;
    private readonly List<string> _pending = [];

    /// <param name="keepOpen">
    /// True keeps the shift file open between rows (fast, for batch runs). False appends each row and closes
    /// the file again, so its size and modified time update with every row, as other programs watching the
    /// file (for example on Windows) expect from a live logger.
    /// </param>
    public ShiftFileWriter(string outputDirectory, ShiftClock shiftClock, int channelCount = SimulatedRow.ChannelCount,
        bool keepOpen = true)
    {
        if (channelCount < 1)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        _outputDirectory = outputDirectory;
        _shiftClock = shiftClock;
        _channelCount = channelCount;
        _keepOpen = keepOpen;
        _header = Header(channelCount);
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

        if (_keepOpen)
        {
            _writer!.WriteLine(FormatRow(row));
            return;
        }

        _pending.Add(FormatRow(row));
        WritePending();
    }

    // Rows wait for the next tick while another program holds the file without sharing it for writing.
    private void WritePending()
    {
        try
        {
            using var stream = new FileStream(_currentPath!, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, Encoding.ASCII);
            foreach (var line in _pending) writer.WriteLine(line);
            writer.Flush();
            _pending.Clear();
        }
        catch (IOException) when (_pending.Count < 3600)
        {
        }
    }

    private void RollTo(ShiftIdentity shift)
    {
        if (_pending.Count > 0) WritePending();
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
        _currentPath = path;
        if (!_keepOpen)
        {
            _writer = null;
            if (isNewFile) File.WriteAllText(path, _header + Environment.NewLine, Encoding.ASCII);
            _currentShift = shift;
            return;
        }
        _writer = new StreamWriter(path, append: true, Encoding.ASCII) { AutoFlush = false };
        if (isNewFile)
        {
            _writer.WriteLine(_header);
        }

        _currentShift = shift;
    }

    internal static string Header(int channelCount) =>
        "(X)\t" + string.Join('\t', Enumerable.Range(1, channelCount).Select(i => $"ch{i}(Y)"));

    internal static string FormatRow(SimulatedRow row)
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

    internal static string FormatValue(object? value) => value switch
    {
        null => "null",
        double d => (d == 0 ? 0d : d).ToString("0.00", CultureInfo.InvariantCulture), // no "-0.00"
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    public void Flush() => _writer?.Flush();

    public void Dispose()
    {
        if (_pending.Count > 0) WritePending();
        _writer?.Flush();
        _writer?.Dispose();
    }
}
