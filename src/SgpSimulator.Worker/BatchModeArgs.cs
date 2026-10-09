namespace SgpSimulator.Worker;

public sealed record BatchModeArgs(DateTime From, DateTime To, string? OutputPath, int? Seed, string? Equipment)
{
    public static BatchModeArgs? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        if (!map.TryGetValue("mode", out var mode) || !mode.Equals("batch", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!map.TryGetValue("from", out var fromStr) || !DateTime.TryParse(fromStr, out var from))
        {
            throw new ArgumentException("--mode batch requires --from <yyyy-MM-dd[THH:mm:ss]>");
        }

        var to = map.TryGetValue("to", out var toStr) && DateTime.TryParse(toStr, out var toDate)
            ? toDate
            : from.AddDays(1);

        var output = map.GetValueOrDefault("output");
        var seed = map.TryGetValue("seed", out var seedStr) && int.TryParse(seedStr, out var seedVal)
            ? seedVal
            : (int?)null;
        var equipment = map.GetValueOrDefault("equipment");

        return new BatchModeArgs(from, to, output, seed, equipment);
    }
}
