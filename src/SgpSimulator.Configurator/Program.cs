using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SgpSimulator.Configurator;
using SgpSimulator.Core.Configuration;
using SgpSimulator.Core.OpcLogger;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5187");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var configPath = OpcConfigPath.Resolve(
    Environment.GetEnvironmentVariable("SGP_CONFIG_PATH") ?? "DataLoggerConfigFile.xml");
var configDirectory = new DirectoryInfo(Path.GetDirectoryName(configPath)!);
var baseDirectory = configDirectory.Name == "Configuration" &&
                    configDirectory.Parent?.Name == "SmartOPCLogger"
    ? configDirectory.Parent.Parent!.FullName : configDirectory.FullName;
// Prefer the worker's own settings during repository development so both read the same processes.
var workerSettingsPath = Path.Combine(baseDirectory, "src", "SgpSimulator.Worker", "appsettings.json");
var settingsPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SGP_SETTINGS_PATH") ??
    (File.Exists(workerSettingsPath) ? workerSettingsPath
        : Path.Combine(AppContext.BaseDirectory, "simulator-settings.json")));
var settings = new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false).Build();
var simulatorOptions = new SimulatorOptions();
settings.GetSection(SimulatorOptions.SectionName).Bind(simulatorOptions);
var outputRoot = Environment.GetEnvironmentVariable("SGP_OUTPUT_PATH") ??
    Path.Combine(baseDirectory, "Output", "Live");
builder.Services.AddSingleton(provider => new LiveSimulationManager(simulatorOptions, outputRoot,
    provider.GetRequiredService<ILogger<LiveSimulationManager>>()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/simulation/processes", (LiveSimulationManager manager) => Results.Ok(manager.Processes));
app.MapGet("/api/simulation", (LiveSimulationManager manager) => Results.Ok(manager.GetStatus()));
app.MapGet("/api/simulation/output", (string equipment, LiveSimulationManager manager) =>
{
    var path = manager.GetStatus().Equipment.FirstOrDefault(item =>
        string.Equals(item.Equipment, equipment, StringComparison.OrdinalIgnoreCase))?.OutputFile;
    return path is not null && File.Exists(path)
        ? Results.File(path, "text/plain", Path.GetFileName(path))
        : Results.NotFound();
});
app.MapPost("/api/simulation", (StartSimulationRequest request, LiveSimulationManager manager) =>
{
    try
    {
        if (manager.GetStatus().Running)
            return Results.Conflict(new { message = "A simulation is already running." });
        var path = ResolveFile(request.FileName);
        if (request.Revision != Fingerprint(path))
            return Results.Conflict(new { message = "The XML changed on disk. Reload before starting." });
        return Results.Ok(manager.Start(path));
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { message = error.Message });
    }
});
app.MapDelete("/api/simulation", async (LiveSimulationManager manager) =>
    Results.Ok(await manager.StopAsync()));

app.MapGet("/api/configs", () =>
{
    var folder = Path.GetDirectoryName(configPath)!;
    return Results.Ok(Directory.EnumerateFiles(folder, "*.xml")
        .Select(Path.GetFileName).OrderBy(name => name).ToArray());
});

app.MapGet("/api/simulation-files", (string? file) =>
{
    try
    {
        var path = ResolveFile(file);
        return Results.Ok(new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(path)).List());
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
    {
        return Results.Problem(error.Message, statusCode: 500);
    }
});

app.MapGet("/api/config", (string? file) =>
{
    try
    {
        var selectedPath = ResolveFile(file);
        var config = OpcLoggerConfig.Load(selectedPath);
        return Results.Ok(new ConfigResponse(Path.GetFileName(selectedPath), Fingerprint(selectedPath),
            config.Equipment.Select((group, index) => new EquipmentConfig(index, group.Name, group.Description,
                group.OPCServer, group.RuntimeMode, group.Enabled, group.ProcessId, group.Tags)).ToList()));
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
    {
        return Results.Problem(error.Message, statusCode: 500);
    }
});

app.MapPost("/api/configs", (NewConfigRequest request) =>
{
    try
    {
        var fileName = NormalizeFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(request.ProjectName))
            return Results.BadRequest(new { message = "A project name is required." });
        if (request.RuntimeMode == RuntimeMode.Realtime && string.IsNullOrWhiteSpace(request.OpcServer))
            return Results.BadRequest(new { message = "Realtime mode needs an OPC server name." });

        var group = new OpcTagGroup
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            OPCServer = request.OpcServer?.Trim() ?? string.Empty,
            RuntimeMode = request.RuntimeMode, Trigger = new OpcTrigger()
        };
        var config = new OpcLoggerConfig
        {
            Projects = [new OpcProject
            {
                Name = request.ProjectName.Trim(),
                Groups = [new OpcGroup { Name = request.ProjectName.Trim(), TagGroups = [group] }]
            }]
        };
        var path = ResolveFile(fileName);
        if (File.Exists(path))
            return Results.Conflict(new { message = "A file with that name already exists." });
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            config.Save(stream);
        return Results.Created($"/api/config?file={Uri.EscapeDataString(fileName)}", new { fileName });
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { message = error.Message });
    }
});

app.MapPut("/api/config", (ConfigRequest request, string? file, LiveSimulationManager manager) =>
{
    try
    {
        var selectedPath = ResolveFile(file);
        var run = manager.GetStatus();
        if (run.Running && string.Equals(run.FileName, Path.GetFileName(selectedPath),
                StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { message = "Stop the simulation before saving this XML." });
        if (request.Equipment is null || request.Equipment.Count == 0)
            return Results.BadRequest(new { message = "Add at least one equipment." });
        var dataCatalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(selectedPath));
        foreach (var item in request.Equipment)
        {
            var problem = Validate(item, dataCatalog);
            if (problem is not null)
                return Results.BadRequest(new { message = $"{DisplayName(item)}: {problem}" });
        }

        if (request.Revision != Fingerprint(selectedPath))
            return Results.Conflict(new { message = "The XML changed on disk. Reload before saving." });

        var config = OpcLoggerConfig.Load(selectedPath);
        var originals = config.Projects.SelectMany(project => project.Groups)
            .SelectMany(parent => parent.TagGroups.Select(group => (Parent: parent, Group: group))).ToArray();
        var defaultParent = originals.FirstOrDefault().Parent ?? config.Projects.SelectMany(project => project.Groups)
            .FirstOrDefault() ?? throw new InvalidOperationException("The XML has no OPCGroup to hold equipment.");
        foreach (var parent in config.Projects.SelectMany(project => project.Groups)) parent.TagGroups = [];

        // Existing equipment keeps its OPCGroup and unedited attributes (such as Trigger); new equipment
        // joins the first OPCGroup.
        var saved = new List<OpcTagGroup>();
        foreach (var item in request.Equipment)
        {
            var original = item.Index is int index && index >= 0 && index < originals.Length
                ? originals[index] : (Parent: defaultParent, Group: new OpcTagGroup { Trigger = new OpcTrigger() });
            var group = original.Group;
            group.Name = item.Name.Trim();
            group.Description = item.Description ?? string.Empty;
            group.OPCServer = item.OpcServer?.Trim() ?? string.Empty;
            group.RuntimeMode = item.RuntimeMode;
            group.Enabled = item.Enabled;
            group.ProcessId = item.ProcessId?.Trim() ?? string.Empty;
            group.Tags = item.Tags;
            original.Parent.TagGroups.Add(group);
            saved.Add(group);
        }
        OpcLoggerConfig.ValidateEquipmentNames(saved);

        var temporaryPath = selectedPath + ".tmp";
        try
        {
            config.Save(temporaryPath);
            File.Move(temporaryPath, selectedPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return Results.Ok(new { revision = Fingerprint(selectedPath) });
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { message = error.Message });
    }
});

app.Run();

string ResolveFile(string? file)
{
    if (file is null) return configPath;
    return Path.Combine(Path.GetDirectoryName(configPath)!, NormalizeFileName(file));
}

static string NormalizeFileName(string file)
{
    var name = file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? file : file + ".xml";
    if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*\.xml$", RegexOptions.IgnoreCase) ||
        name.Length > 100 || name.Contains("..", StringComparison.Ordinal))
        throw new ArgumentException("Use a filename with letters, numbers, dots, hyphens, or underscores.");
    return name;
}

static string DisplayName(EquipmentConfig item) =>
    string.IsNullOrWhiteSpace(item.Name) ? "Unnamed equipment" : item.Name.Trim();

string? Validate(EquipmentConfig item, SimulationDataCatalog dataCatalog)
{
    if (string.IsNullOrWhiteSpace(item.Name)) return "Enter an equipment name.";
    if (!string.IsNullOrWhiteSpace(item.ProcessId) &&
        simulatorOptions.GetEffectiveProcesses().All(process => process.ProcessId != item.ProcessId))
        return $"Process '{item.ProcessId}' is not configured.";
    if (item.RuntimeMode == RuntimeMode.Realtime && string.IsNullOrWhiteSpace(item.OpcServer))
        return "Realtime mode needs an OPC server name.";
    var tags = item.Tags;
    if (tags is null || tags.Count == 0) return "Add at least one tag.";
    if (tags.Any(tag => string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.Address)))
        return "Every tag needs a name and OPC address.";
    if (tags.Any(tag => tag.SimulationMinSpecified && tag.SimulationMaxSpecified &&
                        tag.SimulationMin > tag.SimulationMax))
        return "Simulation minimum cannot exceed maximum.";
    if (tags.Any(tag => tag.SimulationKind == SimulationKind.Constant && string.IsNullOrWhiteSpace(tag.SimulationValue)))
        return "Constant simulation tags need a value.";
    if (tags.Any(tag => tag.SimulationKind == SimulationKind.None && !string.IsNullOrWhiteSpace(tag.SimulationChannel)))
        return "Unmapped tags cannot have a simulation channel.";
    _ = new SimulationTagMapper(new OpcTagGroup { Name = item.Name, Tags = tags }, dataCatalog);
    return null;
}

static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

internal sealed record EquipmentConfig(int? Index, string Name, string? Description, string? OpcServer,
    RuntimeMode RuntimeMode, bool Enabled, string? ProcessId, List<OpcTag> Tags);

internal sealed record ConfigResponse(string FileName, string Revision, List<EquipmentConfig> Equipment);

internal sealed record ConfigRequest(string Revision, List<EquipmentConfig> Equipment);

internal sealed record NewConfigRequest(string FileName, string ProjectName,
    string? OpcServer, RuntimeMode RuntimeMode);

internal sealed record StartSimulationRequest(string FileName, string Revision);
