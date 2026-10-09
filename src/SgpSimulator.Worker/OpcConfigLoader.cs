using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.OpcLogger;

namespace SgpSimulator.Worker;

internal sealed record EquipmentRun(OpcTagGroup Equipment, ProcessConfig Process, SimulationTagMapper Mapper);

internal static class OpcConfigLoader
{
    /// <summary>Loads every enabled TagGroup (equipment) in the XML, each with its process and tag mapper.</summary>
    public static IReadOnlyList<EquipmentRun> Load(SimulatorOptions options)
    {
        var path = OpcConfigPath.Resolve(options.OpcLoggerConfigPath);
        var config = OpcLoggerConfig.Load(path);
        OpcLoggerConfig.ValidateEquipmentNames(config.Equipment);
        var enabled = config.Equipment.Where(group => group.Enabled).ToArray();
        if (enabled.Length == 0)
            throw new InvalidOperationException($"No enabled equipment (TagGroup) in '{path}'.");

        var realtime = enabled.FirstOrDefault(group => group.RuntimeMode != RuntimeMode.Simulation);
        if (realtime is not null)
            throw new NotSupportedException(
                $"Equipment '{realtime.Name}' uses Realtime OPC mode, but OPC acquisition is not implemented yet.");

        var dataCatalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(path));
        return enabled.Select(group => new EquipmentRun(group, options.ResolveProcess(group.ProcessId),
            new SimulationTagMapper(group, dataCatalog))).ToArray();
    }
}
