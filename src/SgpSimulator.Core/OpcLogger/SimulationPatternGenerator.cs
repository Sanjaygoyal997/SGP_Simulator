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
    private readonly double[] _processFactors;
    private bool _skipCycleCount;

    public bool HasBinaryState => _hasBinaryState;

    /// <summary>True when the data has complete running and stopped periods to count cycles on.</summary>
    public bool CanCountCycles => _hasBinaryState && _running.Length > 0 && _stopped.Length > 0;

    /// <summary>Running periods (0 -> 1 -> 0) finished so far.</summary>
    public int CompletedCycles { get; private set; }

    /// <summary>Rows in the last finished running period, i.e. its actual cycle time in ticks.</summary>
    public int LastCycleRows { get; private set; }

    /// <summary>While idle, only stopped periods are generated.</summary>
    public bool Idle { get; set; }

    /// <summary>Typical running-period length to aim for; null keeps the recorded lengths.</summary>
    public int? TargetCycleSeconds { get; set; }

    /// <summary>Tick length; converts the second-based targets and breakdowns into rows.</summary>
    public double SecondsPerRow { get; set; } = 1;

    private int Rows(double seconds) => Math.Max(1, (int)Math.Round(seconds / SecondsPerRow));

    /// <summary>Extra spread, slow cycles, and breakdowns so KPIs vary; null keeps the learned pattern.</summary>
    public VariationSettings? Variation { get; set; }

    /// <param name="ranges">Row blocks to learn from (for example one recipe's periods); null uses all rows.</param>
    public SimulationPatternGenerator(IReadOnlyList<string[]> rows, Random random,
        IReadOnlyList<RowRange>? ranges = null)
    {
        _rows = rows;
        _random = random;
        ranges ??= [new RowRange(0, rows.Count)];
        var selected = ranges.SelectMany(range => Enumerable.Range(range.Start, range.Length).Select(i => rows[i]))
            .ToArray();
        _hasBinaryState = selected.All(row => row[1] is "0" or "1");
        var segments = ranges.SelectMany(range => BuildSegments(rows, _hasBinaryState, range)).ToArray();
        _segments = segments;
        _running = segments.Where(segment => segment.State == "1").ToArray();
        _stopped = segments.Where(segment => segment.State == "0").ToArray();
        _runningDurations = _running.Select(segment => segment.Length).Order().ToArray();
        _stoppedDurations = _stopped.Select(segment => segment.Length).Order().ToArray();
        _allDurations = segments.Select(segment => segment.Length).Order().ToArray();
        _columns = Enumerable.Range(1, rows[0].Length - 1)
            .Select(column => AnalyzeColumn(selected, column)).ToArray();
        _noise = new double[_columns.Length];
        _processFactors = new double[_columns.Length];
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
            var factor = _processFactors[column];
            if (!pattern.Variable || (!pattern.Continuous && factor == 0) || !double.TryParse(text,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                (pattern.ZeroDominant && value == 0))
            {
                values[column] = text;
                continue;
            }

            var generated = value;
            if (pattern.Continuous)
            {
                _noise[column] = 0.8 * _noise[column] + NextNormal() * pattern.Scale * 0.45;
                generated += _segmentVariation * pattern.Scale + _noise[column];
            }
            // Process values shift per period by the configured percentage, a little beyond the recorded range.
            generated *= 1 + factor;
            var margin = Math.Max(Math.Abs(pattern.Minimum), Math.Abs(pattern.Maximum)) *
                         (Variation?.ProcessVariation ?? 0);
            generated = Math.Clamp(generated, pattern.Minimum - margin, pattern.Maximum + margin);
            values[column] = pattern.Continuous || !pattern.Integer
                ? Math.Round(generated, 2) : (object)(long)Math.Round(generated);
        }

        _position++;
        return values;
    }

    /// <summary>Ends the current period without counting it and continues with a stopped period.</summary>
    public void BeginStop()
    {
        if (!_hasBinaryState || _stopped.Length == 0) return;
        _nextState = "0";
        _position = _duration;
        _skipCycleCount = true;
    }

    private void StartSegment()
    {
        if (_duration > 0 && _segment.State == "1" && !_skipCycleCount)
        {
            CompletedCycles++;
            LastCycleRows = _duration;
        }
        _skipCycleCount = false;
        if (Idle && _hasBinaryState && _stopped.Length > 0) _nextState = "0";
        var pool = _nextState == "1" ? _running : _nextState == "0" ? _stopped : [];
        if (pool.Length == 0) pool = _segments;
        _segment = pool[_random.Next(pool.Length)];
        _nextState = _hasBinaryState ? (_segment.State == "1" ? "0" : "1") : null;
        var durations = _segment.State == "1" ? _runningDurations :
            _segment.State == "0" ? _stoppedDurations : _allDurations;
        _duration = Vary(SampleDuration(durations, _segment.State == "1" && _hasBinaryState), _segment.State);
        _position = 0;
        _segmentVariation = NextNormal();
        var process = Variation?.ProcessVariation ?? 0;
        for (var column = 0; column < _processFactors.Length; column++)
            _processFactors[column] = process > 0 ? Math.Clamp(NextNormal() * process / 2, -process, process) : 0;
        Array.Clear(_noise);
    }

    private int Vary(int duration, string state)
    {
        if (Variation is not { } variation || !_hasBinaryState) return duration;
        if (state == "1")
        {
            var spread = variation.CycleVariation;
            // Centred above the set time by the performance loss, so most cycles run longer than set.
            var factor = (1 + variation.PerformanceLoss) * (1 + Math.Clamp(NextNormal() * spread / 2, -spread, spread));
            if (_random.NextDouble() < variation.SlowCycleChance) factor *= 1.10 + _random.NextDouble() * 0.25;
            return Math.Max(1, (int)Math.Round(duration * factor));
        }
        if (state == "0" && variation.GapVariation > 0)
        {
            var gap = variation.GapVariation;
            duration = Math.Max(1, (int)Math.Round(duration * (1 + Math.Clamp(NextNormal() * gap / 2, -gap, gap))));
        }
        if (state == "0" && !Idle && _random.NextDouble() < variation.BreakdownChance)
        {
            var minimum = Rows(variation.BreakdownMinSeconds);
            var maximum = Math.Max(minimum, Rows(variation.BreakdownMaxSeconds));
            return _random.Next(minimum, maximum + 1);
        }
        return duration;
    }

    private int SampleDuration(int[] durations, bool isCycle)
    {
        var position = _random.NextDouble() * (durations.Length - 1);
        var lower = (int)position;
        var fraction = position - lower;
        var baseline = durations[lower] +
            (durations[Math.Min(lower + 1, durations.Length - 1)] - durations[lower]) * fraction;
        if (isCycle && TargetCycleSeconds is > 0 && durations.Length > 0)
        {
            // Keep the recorded spread around the median, centred on the target cycle time.
            var median = durations[durations.Length / 2];
            var target = Rows(TargetCycleSeconds.Value);
            var relative = durations.Length > 1
                ? Math.Clamp(Math.Abs(baseline - median) / median, 0, 0.02) * Math.Sign(baseline - median) : 0;
            var jitter = (_random.NextDouble() * 2 - 1) * Math.Max(1, target * 0.01);
            return Math.Max(1, (int)Math.Round(target * (1 + relative) + jitter));
        }
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

    private static Segment[] BuildSegments(IReadOnlyList<string[]> rows, bool binary, RowRange range)
    {
        var end = range.Start + range.Length;
        if (!binary)
        {
            var windows = new List<Segment>();
            for (var start = range.Start; start < end; start += 300)
                windows.Add(new Segment(start, Math.Min(300, end - start), string.Empty));
            return windows.ToArray();
        }

        var segments = new List<Segment>();
        var beginning = range.Start;
        for (var index = range.Start + 1; index < end; index++)
        {
            if (rows[index][1] == rows[beginning][1]) continue;
            segments.Add(new Segment(beginning, index - beginning, rows[beginning][1]));
            beginning = index;
        }
        segments.Add(new Segment(beginning, end - beginning, rows[beginning][1]));
        // The first and last periods of a block are usually cut off, so they are not typical lengths.
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
        var integer = true;
        foreach (var row in rows)
        {
            var text = row[column];
            if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            nonNull++;
            if (distinct.Count <= 20) distinct.Add(text);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            numeric++;
            if (value != Math.Floor(value)) integer = false;
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

        if (nonNull == 0 || numeric < nonNull * 0.98 || maximum <= minimum)
            return default;
        // A 0/1 column is a state, not a process value.
        var variable = !(integer && minimum == 0 && maximum == 1);
        if (distinct.Count <= 20)
            return new ColumnPattern(false, zero > numeric / 2, minimum, maximum, 0, variable, integer);

        var range = maximum - minimum;
        var averageChange = changes == 0 ? 0 : changeSum / changes;
        var scale = Math.Max(0.005, Math.Min(range * 0.02,
            Math.Max(averageChange * 1.5, range * 0.002)));
        return new ColumnPattern(true, zero > numeric / 2, minimum, maximum, scale, true, integer);
    }

    private readonly record struct Segment(int Start, int Length, string State);
    private readonly record struct ColumnPattern(bool Continuous, bool ZeroDominant,
        double Minimum, double Maximum, double Scale, bool Variable = false, bool Integer = false);
}
