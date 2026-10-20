using System.Globalization;
using System.Text;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Core.History;

/// <summary>
/// Writes one CSV line per finished cycle (0 -> 1 ... 1 -> 0) with its timing, recipe, set and actual cycle
/// time, the stop before it, and the mean of every numeric cycle-file column: a table for training models.
/// </summary>
public sealed class CycleSummaryWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly IReadOnlyList<CycleColumn> _columns;
    private readonly double _longStopSeconds;
    private readonly double[] _sums;
    private readonly int[] _counts;
    private bool? _wasRunning;
    private bool _inCycle;
    private DateTime _start;
    private DateTime _last;
    private long _rows;
    private long _gapRows;
    private double _gapBeforeSeconds;
    private string _recipe = string.Empty;
    private int _setSeconds;

    public CycleSummaryWriter(string path, IReadOnlyList<CycleColumn> columns, double longStopSeconds)
    {
        _columns = columns;
        _longStopSeconds = longStopSeconds;
        _sums = new double[columns.Count];
        _counts = new int[columns.Count];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        _writer.WriteLine(string.Join(',', new[]
        {
            "cycle", "recipe", "start", "end", "set_cycle_s", "actual_cycle_s", "over_set_s", "over_set",
            "stop_before_s", "long_stop_before"
        }.Concat(columns.Select(column => Csv("mean_" + column.Header)))));
    }

    public int Cycles { get; private set; }

    /// <param name="setSeconds">Set cycle time of the running step; 0 when there is none.</param>
    public void Write(SimulatedRow row, bool running, string? recipe, int setSeconds, double secondsPerRow)
    {
        var started = _wasRunning == false && running;
        var ended = _wasRunning == true && !running;
        _wasRunning = running;

        if (ended && _inCycle) Finish(secondsPerRow);
        if (started)
        {
            _inCycle = true;
            _start = row.Timestamp;
            _rows = 0;
            _gapBeforeSeconds = _gapRows * secondsPerRow;
            _recipe = recipe ?? string.Empty;
            _setSeconds = setSeconds;
            Array.Clear(_sums);
            Array.Clear(_counts);
        }

        if (!running)
        {
            _gapRows = ended ? 1 : _gapRows + 1;
            return;
        }
        if (!_inCycle) return;
        _rows++;
        _last = row.Timestamp;
        for (var i = 0; i < _columns.Count; i++)
        {
            if (Number(row.Channels[_columns[i].Index]) is not { } value) continue;
            _sums[i] += value;
            _counts[i]++;
        }
    }

    private void Finish(double secondsPerRow)
    {
        _inCycle = false;
        Cycles++;
        var actual = Math.Round(_rows * secondsPerRow, 1);
        var over = _setSeconds > 0 ? Math.Round(Math.Max(0, actual - _setSeconds), 1) : (double?)null;
        var end = _last.AddSeconds(secondsPerRow);
        var cells = new List<string>
        {
            Cycles.ToString(CultureInfo.InvariantCulture), Csv(_recipe), Time(_start), Time(end),
            _setSeconds > 0 ? _setSeconds.ToString(CultureInfo.InvariantCulture) : string.Empty,
            Format(actual), over is { } value ? Format(value) : string.Empty,
            over is > 0 ? "1" : "0", Format(Math.Round(_gapBeforeSeconds, 1)),
            _gapBeforeSeconds >= _longStopSeconds ? "1" : "0"
        };
        for (var i = 0; i < _columns.Count; i++)
            cells.Add(_counts[i] > 0 ? Format(Math.Round(_sums[i] / _counts[i], 3)) : string.Empty);
        _writer.WriteLine(string.Join(',', cells));
    }

    private static double? Number(object? value) => value switch
    {
        null => null,
        double d => d,
        long l => l,
        int i => i,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => null
    };

    private static string Time(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Format(double value) => (value == 0 ? 0d : value).ToString(CultureInfo.InvariantCulture);

    private static string Csv(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}
