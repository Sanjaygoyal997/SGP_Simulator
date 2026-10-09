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
// Equipment renames are saved here, so prefer the worker's own settings during repository development.
var workerSettingsPath = Path.Combine(baseDirectory, "src", "SgpSimulator.Worker", "appsettings.json");
var settingsPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SGP_SETTINGS_PATH") ??
    (File.Exists(workerSettingsPath) ? workerSettingsPath
        : Path.Combine(AppContext.BaseDirectory, "simulator-settings.json")));
var settings = new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false).Build();
var simulatorOptions = new SimulatorOptions();
settings.GetSection(SimulatorOptions.SectionName).Bind(simulatorOptions);
var outputRoot = Environment.GetEnvironmentVariable("SGP_OUTPUT_PATH") ??
    Path.Combine(baseDirectory, "Output", "Live");
builder.Services.AddSingleton(provider => new LiveSimulationManager(simulatorOptions, settingsPath, outputRoot,
    provider.GetRequiredService<ILogger<LiveSimulationManager>>()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/simulation/processes", (LiveSimulationManager manager) => Results.Ok(manager.Equipment));
app.MapPut("/api/simulation/processes/{processId}",
    (string processId, RenameEquipmentRequest request, LiveSimulationManager manager) =>
{
    try
    {
        return Results.Ok(manager.RenameEquipment(processId, request.EquipmentName));
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { message = error.Message });
    }
});
app.MapGet("/api/simulation", (LiveSimulationManager manager) => Results.Ok(manager.GetStatus()));
app.MapGet("/api/simulation/output", (LiveSimulationManager manager) =>
{
    var path = manager.GetStatus().OutputFile;
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
        return Results.Ok(manager.Start(path, request.ProcessId));
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
        var group = ActiveGroup(config);
        return Results.Ok(new ConfigResponse(
            Path.GetFileName(selectedPath), Fingerprint(selectedPath), group.Name, group.Description,
            group.OPCServer, group.RuntimeMode, group.Enabled, group.Tags));
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
        if (request.Tags is null || request.Tags.Count == 0)
            return Results.BadRequest(new { message = "Add at least one tag." });
        if (request.Tags.Any(tag => string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.Address)))
            return Results.BadRequest(new { message = "Every tag needs a name and OPC address." });
        if (request.Tags.Any(tag => tag.SimulationMinSpecified && tag.SimulationMaxSpecified &&
                                    tag.SimulationMin > tag.SimulationMax))
            return Results.BadRequest(new { message = "Simulation minimum cannot exceed maximum." });
        if (request.Tags.Any(tag => tag.SimulationKind == SimulationKind.Constant &&
                                    string.IsNullOrWhiteSpace(tag.SimulationValue)))
            return Results.BadRequest(new { message = "Constant simulation tags need a value." });
        if (request.Tags.Any(tag => tag.SimulationKind == SimulationKind.None &&
                                    !string.IsNullOrWhiteSpace(tag.SimulationChannel)))
            return Results.BadRequest(new { message = "Unmapped tags cannot have a simulation channel." });
        if (string.IsNullOrWhiteSpace(request.GroupName))
            return Results.BadRequest(new { message = "The tag group needs a name." });
        if (!request.Enabled)
            return Results.BadRequest(new { message = "The active tag group must stay enabled." });
        if (request.RuntimeMode == RuntimeMode.Realtime && string.IsNullOrWhiteSpace(request.OpcServer))
            return Results.BadRequest(new { message = "Realtime mode needs an OPC server name." });
        var dataCatalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(selectedPath));
        _ = new SimulationTagMapper(new OpcTagGroup { Tags = request.Tags }, dataCatalog);

        if (request.Revision != Fingerprint(selectedPath))
            return Results.Conflict(new { message = "The XML changed on disk. Reload before saving." });

        var config = OpcLoggerConfig.Load(selectedPath);
        var group = ActiveGroup(config);
        group.Name = request.GroupName.Trim();
        group.Description = request.Description ?? string.Empty;
        group.OPCServer = request.OpcServer ?? string.Empty;
        group.RuntimeMode = request.RuntimeMode;
        group.Enabled = request.Enabled;
        group.Tags = request.Tags;

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

static OpcTagGroup ActiveGroup(OpcLoggerConfig config)
{
    var groups = config.Projects.SelectMany(project => project.Groups)
        .SelectMany(group => group.TagGroups).Where(group => group.Enabled).ToArray();
    if (groups.Length != 1)
        throw new InvalidOperationException($"Expected one enabled tag group, found {groups.Length}.");
    return groups[0];
}

static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

internal sealed record ConfigResponse(string FileName, string Revision, string GroupName,
    string Description, string OpcServer, RuntimeMode RuntimeMode, bool Enabled, List<OpcTag> Tags);

internal sealed record ConfigRequest(string Revision, string GroupName, string? Description,
    string? OpcServer, RuntimeMode RuntimeMode, bool Enabled, List<OpcTag> Tags);

internal sealed record NewConfigRequest(string FileName, string ProjectName,
    string? OpcServer, RuntimeMode RuntimeMode);

internal sealed record RenameEquipmentRequest(string? EquipmentName);

internal sealed record StartSimulationRequest(string FileName, string ProcessId, string Revision);
