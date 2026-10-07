using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.OpcLogger;

namespace SgpSimulator.Worker;

internal static class OpcConfigLoader
{
    public static (OpcTagGroup Group, SimulationTagMapper Mapper) Load(SimulatorOptions options)
    {
        var path = OpcConfigPath.Resolve(options.OpcLoggerConfigPath);
        var config = OpcLoggerConfig.Load(path);
        var groups = config.Projects.SelectMany(p => p.Groups).SelectMany(g => g.TagGroups)
            .Where(g => g.Enabled).ToArray();
        if (groups.Length != 1)
            throw new InvalidOperationException($"Expected one enabled TagGroup in '{path}', found {groups.Length}.");

        var group = groups[0];
        if (group.RuntimeMode != RuntimeMode.Simulation)
            throw new NotSupportedException("Realtime OPC mode is selected, but OPC acquisition is not implemented yet.");

        var dataCatalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(path));
        return (group, new SimulationTagMapper(group, dataCatalog));
    }
}
