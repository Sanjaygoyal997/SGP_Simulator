using Microsoft.Extensions.Configuration;
using SgpSimulator.Core.Configuration;
using SgpSimulator.Worker;

var batchArgs = BatchModeArgs.Parse(args);
if (batchArgs is not null)
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    var options = new SimulatorOptions();
    configuration.GetSection(SimulatorOptions.SectionName).Bind(options);

    BatchGenerator.Run(options, batchArgs);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.SectionName));
builder.Services.AddHostedService<RealtimeSimulationService>();

var host = builder.Build();
host.Run();
