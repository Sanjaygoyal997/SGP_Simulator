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
        var recipe = opts.GetActiveRecipe();
        var simulator = new ProcessSimulator(recipe);
        var shiftClock = new ShiftClock(opts.Shift);

        using var writer = new ShiftFileWriter(opts.OutputPath, shiftClock);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(opts.TickIntervalMs));

        logger.LogInformation(
            "Simulating process {ProcessId} with recipe {Recipe}, writing shift files to {OutputPath}",
            opts.ProcessId,
            recipe.Name,
            Path.GetFullPath(opts.OutputPath));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var row = simulator.Tick(DateTime.Now);
            writer.Write(row);
            writer.Flush();
        }
    }
}
