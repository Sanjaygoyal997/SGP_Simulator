using System.Globalization;

namespace SgpSimulator.Core.OpcLogger;

public readonly record struct RowRange(int Start, int Length);

/// <summary>Finds which recipe was running in recorded data, from its recipe-name text column.</summary>
public static class RecordedRecipes
{
    private const int MaxDistinctNames = 50;

    /// <summary>Text columns (row cell index) holding a few repeated non-numeric values, such as recipe names.</summary>
    public static IReadOnlyList<int> CandidateColumns(IReadOnlyList<string[]> rows)
    {
        var result = new List<int>();
        for (var column = 1; column < rows[0].Length; column++)
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var valid = true;
            foreach (var row in rows)
            {
                var text = row[column];
                if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    (distinct.Add(text) && distinct.Count > MaxDistinctNames))
                {
                    valid = false;
                    break;
                }
            }
            if (valid && distinct.Count > 0) result.Add(column);
        }
        return result;
    }

    /// <summary>The recipe column: the candidate naming the most known recipes, else the first candidate.</summary>
    public static int? KeyColumn(IReadOnlyList<string[]> rows, ICollection<string> knownRecipes)
    {
        var candidates = CandidateColumns(rows);
        if (candidates.Count == 0) return null;
        return candidates.MaxBy(column => Names(rows, column).Count(knownRecipes.Contains));
    }

    /// <summary>Columns repeating the recipe column on every row (for example ch15 and ch16).</summary>
    public static IReadOnlyList<int> NameColumns(IReadOnlyList<string[]> rows, int key) =>
        Enumerable.Range(1, rows[0].Length - 1)
            .Where(column => rows.All(row => row[column] == row[key])).ToArray();

    public static IReadOnlyList<string> Names(IReadOnlyList<string[]> rows, int key) =>
        rows.Select(row => row[key]).Where(name => !name.Equals("null", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Consecutive row blocks recorded while the recipe was running.</summary>
    public static IReadOnlyList<RowRange> Ranges(IReadOnlyList<string[]> rows, int key, string recipe)
    {
        var ranges = new List<RowRange>();
        var start = -1;
        for (var index = 0; index <= rows.Count; index++)
        {
            var match = index < rows.Count && rows[index][key] == recipe;
            if (match && start < 0) start = index;
            else if (!match && start >= 0)
            {
                ranges.Add(new RowRange(start, index - start));
                start = -1;
            }
        }
        return ranges;
    }
}
