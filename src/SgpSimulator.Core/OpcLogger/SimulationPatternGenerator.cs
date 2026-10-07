using System.Globalization;

namespace SgpSimulator.Core.OpcLogger;

// Builds new runs from observed phase lengths and related rows, while varying continuous readings.
internal sealed class SimulationPatternGenerator
{
    private readonly IReadOnlyList<string[]> _rows;
    private readonly Random _random;
    private readonly Segment[] _segments;
    private readonly Segment[] _running;
    private readonly Segment[] _stopped;
    private readonly int[] _runningDurations;
    private readonly int[] _stoppedDurations;
    private readonly int[] _allDurations;
    private readonly ColumnPattern[] _columns;
    private readonly double[] _noise;
    private readonly bool _hasBinaryState;
    private Segment _segment;
    private int _position;
    private int _duration;
    private string? _nextState;
    private double _segmentVariation;

    public SimulationPatternGenerator(IReadOnlyList<string[]> rows, Random random)
    {
        _rows = rows;
        _random = random;
        _hasBinaryState = rows.All(row => row[1] is "0" or "1");
        var segments = BuildSegments(rows, _hasBinaryState);
        _segments = segments;
        _running = segments.Where(segment => segment.State == "1").ToArray();
        _stopped = segments.Where(segment => segment.State == "0").ToArray();
        _runningDurations = _running.Select(segment => segment.Length).Order().ToArray();
        _stoppedDurations = _stopped.Select(segment => segment.Length).Order().ToArray();
        _allDurations = segments.Select(segment => segment.Length).Order().ToArray();
        _columns = Enumerable.Range(1, rows[0].Length - 1)
            .Select(column => AnalyzeColumn(rows, column)).ToArray();
        _noise = new double[_columns.Length];
        _nextState = _hasBinaryState && _running.Length > 0 && _stopped.Length > 0
            ? (_random.Next(2) == 0 ? "0" : "1") : null;
    }

    public object?[] Next()
    {
        if (_position >= _duration) StartSegment();

        var index = _segment.Start + Math.Min(_segment.Length - 1,
            (int)((long)_position * _segment.Length / _duration));
        var source = _rows[index];
        var values = new object?[_columns.Length];
        for (var column = 0; column < _columns.Length; column++)
        {
            var text = source[column + 1];
            var pattern = _columns[column];
            if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                values[column] = null;
                continue;
            }
            if (!pattern.Continuous || !double.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value) ||
                (pattern.ZeroDominant && value == 0))
            {
                values[column] = text;
                continue;
            }

            _noise[column] = 0.8 * _noise[column] + NextNormal() * pattern.Scale * 0.45;
            var generated = value + _segmentVariation * pattern.Scale + _noise[column];
            values[column] = Math.Round(Math.Clamp(generated, pattern.Minimum, pattern.Maximum), 2);
        }

        _position++;
        return values;
    }

    private void StartSegment()
    {
        var pool = _nextState == "1" ? _running : _nextState == "0" ? _stopped : [];
        if (pool.Length == 0) pool = _segments;
        _segment = pool[_random.Next(pool.Length)];
        _nextState = _hasBinaryState ? (_segment.State == "1" ? "0" : "1") : null;
        var durations = _segment.State == "1" ? _runningDurations :
            _segment.State == "0" ? _stoppedDurations : _allDurations;
        _duration = SampleDuration(durations, _segment.State == "1" && _hasBinaryState);
        _position = 0;
        _segmentVariation = NextNormal();
        Array.Clear(_noise);
    }

    private int SampleDuration(int[] durations, bool isCycle)
    {
        var position = _random.NextDouble() * (durations.Length - 1);
        var lower = (int)position;
        var fraction = position - lower;
        var baseline = durations[lower] +
            (durations[Math.Min(lower + 1, durations.Length - 1)] - durations[lower]) * fraction;
        if (isCycle)
        {
            var spread = durations[^1] - durations[0];
            var tolerance = Math.Max(1, Math.Min(spread * 0.25, baseline * 0.02));
            var varied = baseline + (_random.NextDouble() * 2 - 1) * tolerance;
            return Math.Max(1, (int)Math.Round(Math.Clamp(varied,
                durations[0] - tolerance, durations[^1] + tolerance)));
        }
        return Math.Max(1, (int)Math.Round(baseline * (0.75 + _random.NextDouble() * 0.5)));
    }

    private double NextNormal() => Math.Sqrt(-2 * Math.Log(Math.Max(_random.NextDouble(), 1e-12))) *
                                   Math.Cos(2 * Math.PI * _random.NextDouble());

    private static Segment[] BuildSegments(IReadOnlyList<string[]> rows, bool binary)
    {
        if (!binary)
        {
            var windows = new List<Segment>();
            for (var start = 0; start < rows.Count; start += 300)
                windows.Add(new Segment(start, Math.Min(300, rows.Count - start), string.Empty));
            return windows.ToArray();
        }

        var segments = new List<Segment>();
        var beginning = 0;
        for (var index = 1; index < rows.Count; index++)
        {
            if (rows[index][1] == rows[beginning][1]) continue;
            segments.Add(new Segment(beginning, index - beginning, rows[beginning][1]));
            beginning = index;
        }
        segments.Add(new Segment(beginning, rows.Count - beginning, rows[beginning][1]));
        return segments.Count > 3 ? segments.Skip(1).SkipLast(1).ToArray() : segments.ToArray();
    }

    private static ColumnPattern AnalyzeColumn(IReadOnlyList<string[]> rows, int column)
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var numeric = 0;
        var nonNull = 0;
        var zero = 0;
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        var previous = double.NaN;
        var changeSum = 0.0;
        var changes = 0;
        foreach (var row in rows)
        {
            var text = row[column];
            if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            nonNull++;
            if (distinct.Count <= 20) distinct.Add(text);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            numeric++;
            if (value == 0) zero++;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            if (!double.IsNaN(previous) && value != previous)
            {
                changeSum += Math.Abs(value - previous);
                changes++;
            }
            previous = value;
        }

        if (nonNull == 0 || numeric < nonNull * 0.98 || distinct.Count <= 20 || maximum <= minimum)
            return default;

        var range = maximum - minimum;
        var averageChange = changes == 0 ? 0 : changeSum / changes;
        var scale = Math.Max(0.005, Math.Min(range * 0.02,
            Math.Max(averageChange * 1.5, range * 0.002)));
        return new ColumnPattern(true, zero > numeric / 2, minimum, maximum, scale);
    }

    private readonly record struct Segment(int Start, int Length, string State);
    private readonly record struct ColumnPattern(bool Continuous, bool ZeroDominant,
        double Minimum, double Maximum, double Scale);
}
