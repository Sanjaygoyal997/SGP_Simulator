namespace SgpSimulator.Core.OpcLogger;

public static class OpcConfigPath
{
    public static string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        if (path == "DataLoggerConfigFile.xml")
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "SmartOPCLogger", "Configuration", path);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}
