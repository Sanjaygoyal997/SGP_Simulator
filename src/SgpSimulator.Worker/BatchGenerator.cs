using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Worker;

public static class BatchGenerator
{
    public static void Run(SimulatorOptions options, BatchModeArgs args)
    {
        var (_, mapper) = OpcConfigLoader.Load(options);
        var processes = options.GetEffectiveProcesses();
        if (args.ProcessId is not null)
        {
            processes = processes.Where(p => p.ProcessId == args.ProcessId).ToArray();
            if (processes.Count == 0)
            {
                throw new ArgumentException($"No configured process with id '{args.ProcessId}'.");
            }
        }

        var singleProcess = processes.Count == 1;
        for (var i = 0; i < processes.Count; i++)
        {
            var seed = args.Seed is null ? (int?)null : args.Seed.Value + i;
            RunProcess(options, processes[i], args, seed, singleProcess, mapper);
        }
    }

    private static void RunProcess(
        SimulatorOptions options,
        ProcessConfig process,
        BatchModeArgs args,
        int? seed,
        bool singleProcess,
        SgpSimulator.Core.OpcLogger.SimulationTagMapper mapper)
    {
        var recipe = options.GetRecipe(process.ActiveRecipe);
        var simulator = new ProcessSimulator(recipe, seed);
        var simulation = mapper.CreateSession(seed);
        var shiftClock = new ShiftClock(process.Shift ?? options.Shift);
        var tickIntervalMs = process.TickIntervalMs ?? options.TickIntervalMs;

        // When --output is given for a multi-process run, it is a base directory;
        // each process still gets its own subfolder so files don't collide.
        var outputPath = args.OutputPath is null
            ? process.OutputPath ?? Path.Combine(options.OutputPath, process.ProcessId)
            : singleProcess
                ? args.OutputPath
                : Path.Combine(args.OutputPath, process.ProcessId);

        using var writer = new ShiftFileWriter(outputPath, shiftClock, mapper.TagCount);

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
            $"[{process.ProcessId}] Generated {rowCount} rows from {args.From:yyyy-MM-dd HH:mm:ss} to {args.To:yyyy-MM-dd HH:mm:ss} into {Path.GetFullPath(outputPath)}");
    }
}
