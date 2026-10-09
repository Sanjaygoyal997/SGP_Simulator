using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Worker;

public static class BatchGenerator
{
    public static void Run(SimulatorOptions options, BatchModeArgs args)
    {
        IReadOnlyList<EquipmentRun> runs = OpcConfigLoader.Load(options);
        if (args.Equipment is not null)
        {
            runs = runs.Where(run => string.Equals(run.Equipment.Name, args.Equipment,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (runs.Count == 0)
            {
                throw new ArgumentException($"No enabled equipment named '{args.Equipment}'.");
            }
        }

        var singleEquipment = runs.Count == 1;
        for (var i = 0; i < runs.Count; i++)
        {
            var seed = args.Seed is null ? (int?)null : args.Seed.Value + i;
            RunEquipment(options, runs[i], args, seed, singleEquipment);
        }
    }

    private static void RunEquipment(
        SimulatorOptions options,
        EquipmentRun run,
        BatchModeArgs args,
        int? seed,
        bool singleEquipment)
    {
        var process = run.Process;
        var recipe = options.GetRecipe(process.ActiveRecipe);
        var simulator = new ProcessSimulator(recipe, seed);
        var simulation = run.Mapper.CreateSession(seed);
        var shiftClock = new ShiftClock(process.Shift ?? options.Shift);
        var tickIntervalMs = process.TickIntervalMs ?? options.TickIntervalMs;

        // When --output is given for a multi-equipment run, it is a base directory;
        // each equipment still gets its own subfolder so files don't collide.
        var outputPath = args.OutputPath is null
            ? Path.Combine(process.OutputPath ?? options.OutputPath, run.Equipment.Name)
            : singleEquipment
                ? args.OutputPath
                : Path.Combine(args.OutputPath, run.Equipment.Name);

        using var writer = new ShiftFileWriter(outputPath, shiftClock, run.Mapper.TagCount);

        var interval = TimeSpan.FromMilliseconds(tickIntervalMs);
        var current = args.From;
        var rowCount = 0;
        while (current < args.To)
        {
            writer.Write(simulation.Map(simulator.Tick(current)));
            current += interval;
            rowCount++;
        }

        writer.Flush();
        Console.WriteLine(
            $"[{run.Equipment.Name} / {process.ProcessId}] Generated {rowCount} rows from {args.From:yyyy-MM-dd HH:mm:ss} to {args.To:yyyy-MM-dd HH:mm:ss} into {Path.GetFullPath(outputPath)}");
    }
}
