using Microsoft.Extensions.Options;
using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.Domain;
using SgpSimulator.Core.Output;

namespace SgpSimulator.Worker;

public sealed class RealtimeSimulationService(
    IOptions<SimulatorOptions> options,
    ILogger<RealtimeSimulationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var runs = OpcConfigLoader.Load(opts);

        var tasks = runs.Select(run => RunEquipmentAsync(opts, run, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task RunEquipmentAsync(SimulatorOptions opts, EquipmentRun run, CancellationToken stoppingToken)
    {
        var process = run.Process;
        var recipe = opts.GetRecipe(process.ActiveRecipe);
        var simulator = new ProcessSimulator(recipe);
        var simulation = run.Mapper.CreateSession();
        var shiftClock = new ShiftClock(process.Shift ?? opts.Shift);
        var outputPath = Path.Combine(process.OutputPath ?? opts.OutputPath, run.Equipment.Name);
        var tickIntervalMs = process.TickIntervalMs ?? opts.TickIntervalMs;

        using var writer = new ShiftFileWriter(outputPath, shiftClock, run.Mapper.TagCount);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickIntervalMs));

        logger.LogInformation(
            "Simulating equipment {Equipment} as process {ProcessId} with recipe {Recipe}, writing shift files to {OutputPath}",
            run.Equipment.Name,
            process.ProcessId,
            recipe.Name,
            Path.GetFullPath(outputPath));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var row = simulator.Tick(DateTime.Now);
            writer.Write(simulation.Map(row));
            writer.Flush();
        }
    }
}
