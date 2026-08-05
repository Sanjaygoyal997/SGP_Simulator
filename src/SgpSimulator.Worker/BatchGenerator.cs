using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Worker;

public static class BatchGenerator
{
    public static void Run(SimulatorOptions options, BatchModeArgs args)
    {
        var recipe = options.GetActiveRecipe();
        var simulator = new ProcessSimulator(recipe, args.Seed);
        var shiftClock = new ShiftClock(options.Shift);
        var outputPath = args.OutputPath ?? options.OutputPath;

        using var writer = new ShiftFileWriter(outputPath, shiftClock);

        var interval = TimeSpan.FromMilliseconds(options.TickIntervalMs);
        var current = args.From;
        var rowCount = 0;
        while (current < args.To)
        {
            writer.Write(simulator.Tick(current));
            current += interval;
            rowCount++;
        }

        writer.Flush();
        Console.WriteLine($"Generated {rowCount} rows from {args.From:yyyy-MM-dd HH:mm:ss} to {args.To:yyyy-MM-dd HH:mm:ss} into {Path.GetFullPath(outputPath)}");
    }
}
