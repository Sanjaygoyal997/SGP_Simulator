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
        var processes = opts.GetEffectiveProcesses();

        var tasks = processes.Select(process => RunProcessAsync(opts, process, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task RunProcessAsync(SimulatorOptions opts, ProcessConfig process, CancellationToken stoppingToken)
    {
        var recipe = opts.GetRecipe(process.ActiveRecipe);
        var simulator = new ProcessSimulator(recipe);
        var shiftClock = new ShiftClock(process.Shift ?? opts.Shift);
        var outputPath = process.OutputPath ?? Path.Combine(opts.OutputPath, process.ProcessId);
        var tickIntervalMs = process.TickIntervalMs ?? opts.TickIntervalMs;

        using var writer = new ShiftFileWriter(outputPath, shiftClock);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickIntervalMs));

        logger.LogInformation(
            "Simulating process {ProcessId} with recipe {Recipe}, writing shift files to {OutputPath}",
            process.ProcessId,
            recipe.Name,
            Path.GetFullPath(outputPath));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var row = simulator.Tick(DateTime.Now);
            writer.Write(row);
            writer.Flush();
        }
    }
}
