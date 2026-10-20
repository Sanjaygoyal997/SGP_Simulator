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
// Environment variables override the file. A .env file at the repository (or install) root supplies the
// SGP_DB_ database settings without committing the password.
MasterDataOptions.FindAndLoadDotEnv(Directory.GetCurrentDirectory(), baseDirectory, AppContext.BaseDirectory);
var settings = new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false)
    .AddEnvironmentVariables().Build();
var masterDataOptions = new MasterDataOptions();
settings.GetSection(MasterDataOptions.SectionName).Bind(masterDataOptions);
masterDataOptions.ApplyEnvironment();
builder.Services.AddSingleton(new MasterDataService(masterDataOptions));
var simulatorOptions = new SimulatorOptions();
settings.GetSection(SimulatorOptions.SectionName).Bind(simulatorOptions);
var outputRoot = Environment.GetEnvironmentVariable("SGP_OUTPUT_PATH") ??
    Path.Combine(baseDirectory, "Output", "Live");
builder.Services.AddSingleton(provider => new LiveSimulationManager(simulatorOptions, outputRoot,
    provider.GetRequiredService<ILogger<LiveSimulationManager>>()));

var app = builder.Build();
app.UseDefaultFiles();
// The page and its scripts change with each update; make browsers check for a newer copy every time.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapGet("/api/master", (MasterDataService master) =>
    Results.Ok(new
    {
        configured = master.Configured, plantId = master.PlantId, target = master.Target,
        envFile = MasterDataOptions.DotEnvPath
    }));
app.MapGet("/api/master/equipment", async (MasterDataService master, CancellationToken cancellationToken) =>
    await MasterResult(() => master.GetEquipmentAsync(cancellationToken)));
app.MapGet("/api/master/equipment/{id:int}/recipes",
    async (int id, MasterDataService master, CancellationToken cancellationToken) =>
        await MasterResult(() => master.GetRecipesAsync(id, cancellationToken)));
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
app.MapGet("/api/simulation/cycle-file", (string equipment, LiveSimulationManager manager) =>
{
    var path = manager.GetStatus().Equipment.FirstOrDefault(item =>
        string.Equals(item.Equipment, equipment, StringComparison.OrdinalIgnoreCase))?.LastCycleFile;
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

// Recipe names recorded in the reference data, with the files that contain each one.
app.MapGet("/api/data-recipes", (string? file) =>
{
    try
    {
        var path = ResolveFile(file);
        var known = OpcLoggerConfig.Load(path).Recipes.Select(recipe => recipe.Name).ToHashSet(StringComparer.Ordinal);
        var catalog = new SimulationDataCatalog(SimulationDataCatalog.ResolveDirectory(path));
        var found = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dataFile in catalog.List())
        {
            IReadOnlyList<string[]> rows;
            try { rows = catalog.LoadRows(dataFile.Id); }
            catch (InvalidOperationException) { continue; }
            if (RecordedRecipes.KeyColumn(rows, known) is not int key) continue;
            foreach (var name in RecordedRecipes.Names(rows, key))
            {
                if (!found.TryGetValue(name, out var files)) found[name] = files = [];
                files.Add(dataFile.Id);
            }
        }
        return Results.Ok(found.Select(pair => new { name = pair.Key, files = pair.Value }));
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
        var equipment = config.Projects.SelectMany(project => project.Groups)
            .SelectMany(parent => parent.TagGroups.Select(group => (Parent: parent, Group: group)))
            .Select((item, index) => new EquipmentConfig(index, item.Group.Name, item.Parent.Name,
                item.Group.Description, item.Group.OPCServer, item.Group.RuntimeMode, item.Group.Enabled,
                item.Group.ProcessId, item.Group.Tags, ToConfig(item.Group.Schedule),
                item.Group.MasterEquipmentId > 0 ? item.Group.MasterEquipmentId : null,
                new VariationConfig(item.Group.CycleVariationPercent, item.Group.SlowCyclePercent,
                    item.Group.BreakdownPercent, item.Group.BreakdownMinMinutes, item.Group.BreakdownMaxMinutes,
                    item.Group.GapVariationPercent, item.Group.ProcessVariationPercent,
                    item.Group.PerformanceLossPercent)))
            .ToList();
        var recipes = config.Recipes
            .Select(recipe => new RecipeConfig(recipe.Name, recipe.ReferenceFile, recipe.Description)).ToList();
        return Results.Ok(new ConfigResponse(Path.GetFileName(selectedPath), Fingerprint(selectedPath),
            equipment, recipes));
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
        var recipes = (request.Recipes ?? []).Select(recipe => new OpcRecipe
        {
            Name = recipe.Name?.Trim() ?? string.Empty, ReferenceFile = recipe.ReferenceFile?.Trim() ?? string.Empty,
            Description = recipe.Description?.Trim() ?? string.Empty
        }).ToList();
        var recipeProblem = ValidateRecipes(recipes, dataCatalog);
        if (recipeProblem is not null) return Results.BadRequest(new { message = recipeProblem });
        foreach (var item in request.Equipment)
        {
            var problem = Validate(item, dataCatalog, recipes);
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

        // Existing equipment keeps its unedited attributes (such as Trigger). Each equipment goes to the
        // OPCGroup it names; a new name creates that OPCGroup in the same project.
        var saved = new List<OpcTagGroup>();
        foreach (var item in request.Equipment)
        {
            var original = item.Index is int index && index >= 0 && index < originals.Length
                ? originals[index] : (Parent: defaultParent, Group: new OpcTagGroup { Trigger = new OpcTrigger() });
            var opcGroupName = string.IsNullOrWhiteSpace(item.OpcGroup) ? original.Parent.Name : item.OpcGroup.Trim();
            var parent = config.Projects.SelectMany(project => project.Groups)
                .FirstOrDefault(candidate => candidate.Name == opcGroupName);
            if (parent is null)
            {
                var project = config.Projects.First(candidate => candidate.Groups.Contains(original.Parent));
                parent = new OpcGroup { Name = opcGroupName };
                project.Groups.Add(parent);
            }
            original = (parent, original.Group);
            var group = original.Group;
            group.Name = item.Name.Trim();
            group.Description = item.Description ?? string.Empty;
            group.OPCServer = item.OpcServer?.Trim() ?? string.Empty;
            group.RuntimeMode = item.RuntimeMode;
            group.Enabled = item.Enabled;
            group.ProcessId = item.ProcessId?.Trim() ?? string.Empty;
            group.MasterEquipmentId = item.MasterEquipmentId is > 0 ? item.MasterEquipmentId.Value : 0;
            if (item.Variation is { } variation)
            {
                group.CycleVariationPercent = variation.CycleVariationPercent;
                group.SlowCyclePercent = variation.SlowCyclePercent;
                group.BreakdownPercent = variation.BreakdownPercent;
                group.BreakdownMinMinutes = variation.BreakdownMinMinutes;
                group.BreakdownMaxMinutes = variation.BreakdownMaxMinutes;
                group.GapVariationPercent = variation.GapVariationPercent;
                group.ProcessVariationPercent = variation.ProcessVariationPercent;
                group.PerformanceLossPercent = variation.PerformanceLossPercent;
            }
            group.Tags = item.Tags;
            group.Schedule = ToSchedule(item.Schedule);
            original.Parent.TagGroups.Add(group);
            saved.Add(group);
        }
        OpcLoggerConfig.ValidateEquipmentNames(saved);
        config.Recipes = recipes;
        foreach (var project in config.Projects) project.Groups.RemoveAll(parent => parent.TagGroups.Count == 0);

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

static string? ValidateRecipes(List<OpcRecipe> recipes, SimulationDataCatalog dataCatalog)
{
    var names = new HashSet<string>(StringComparer.Ordinal);
    var files = dataCatalog.List().Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
    foreach (var recipe in recipes)
    {
        if (recipe.Name.Length is 0 or > 64 || recipe.Name.Any(char.IsControl))
            return "Every recipe needs a name of up to 64 characters.";
        if (!names.Add(recipe.Name)) return $"Recipe '{recipe.Name}' is defined more than once.";
        if (recipe.ReferenceFile.Length > 0 && !files.Contains(recipe.ReferenceFile))
            return $"Recipe '{recipe.Name}' reference file '{recipe.ReferenceFile}' is not available.";
    }
    return null;
}

static ScheduleConfig? ToConfig(OpcSchedule? schedule) => schedule is null ? null :
    new ScheduleConfig(schedule.Repeat, schedule.Steps.Select(step => new ScheduleStepConfig(step.Recipe, step.Cycles, step.CycleSeconds, step.Loop)).ToList());

static OpcSchedule? ToSchedule(ScheduleConfig? schedule) => schedule is not { Steps.Count: > 0 } ? null :
    new OpcSchedule
    {
        Repeat = schedule.Repeat,
        Steps = schedule.Steps.Select(step => new OpcScheduleStep
        {
            Recipe = step.Recipe.Trim(), Cycles = step.Loop ? Math.Max(1, step.Cycles) : step.Cycles,
            CycleSeconds = Math.Max(0, step.CycleSeconds), Loop = step.Loop
        }).ToList()
    };

string? Validate(EquipmentConfig item, SimulationDataCatalog dataCatalog, List<OpcRecipe> recipes)
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
    if (tags.Any(tag => tag.CycleColumn.Any(char.IsControl) || tag.CycleColumn.Length > 64))
        return "Cycle file column headers must be one line without tabs (64 characters max).";
    if (item.Schedule?.Steps.Any(step => step.Cycles < 1 && !step.Loop) == true)
        return "Every schedule row needs at least one batch, or Loop.";
    if (item.Variation is { } v && (v.CycleVariationPercent is < 0 or > 50 || v.SlowCyclePercent is < 0 or > 100 ||
            v.BreakdownPercent is < 0 or > 100 || v.BreakdownMinMinutes < 0 ||
            v.BreakdownMaxMinutes < v.BreakdownMinMinutes || v.BreakdownMaxMinutes > 24 * 60 ||
            v.GapVariationPercent is < 0 or > 90 || v.ProcessVariationPercent is < 0 or > 50 ||
            v.PerformanceLossPercent is < 0 or > 100))
        return "Variation: cycle 0-50%, gap 0-90%, process values 0-50%, slow cycles and breakdowns 0-100%, " +
               "breakdown minutes from minimum to maximum.";
    if (item.Schedule?.Steps.Any(step => string.IsNullOrWhiteSpace(step.Recipe)) == true)
        return "Every schedule step needs a recipe.";
    // Recipes of an equipment linked to the master data come from item_master, not the XML list.
    if (item.MasterEquipmentId is not > 0 &&
        item.Schedule?.Steps.FirstOrDefault(step => recipes.All(recipe => recipe.Name != step.Recipe?.Trim())) is { } missing)
        return $"Schedule recipe '{missing.Recipe}' is not defined.";
    _ = new SimulationTagMapper(new OpcTagGroup { Name = item.Name, Tags = tags, Schedule = ToSchedule(item.Schedule) },
        dataCatalog, recipes);
    return null;
}

static async Task<IResult> MasterResult<T>(Func<Task<T>> query)
{
    try
    {
        return Results.Ok(await query());
    }
    catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
    {
        return Results.Problem($"Master data is not available: {error.Message}", statusCode: 502);
    }
}

static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

internal sealed record EquipmentConfig(int? Index, string Name, string? OpcGroup, string? Description, string? OpcServer,
    RuntimeMode RuntimeMode, bool Enabled, string? ProcessId, List<OpcTag> Tags, ScheduleConfig? Schedule = null,
    int? MasterEquipmentId = null, VariationConfig? Variation = null);

internal sealed record VariationConfig(double CycleVariationPercent, double SlowCyclePercent,
    double BreakdownPercent, double BreakdownMinMinutes, double BreakdownMaxMinutes,
    double GapVariationPercent = 30, double ProcessVariationPercent = 2, double PerformanceLossPercent = 5);

internal sealed record ScheduleConfig(bool Repeat, List<ScheduleStepConfig> Steps);

internal sealed record ScheduleStepConfig(string Recipe, int Cycles, int CycleSeconds = 0, bool Loop = false);

internal sealed record RecipeConfig(string Name, string? ReferenceFile, string? Description);

internal sealed record ConfigResponse(string FileName, string Revision, List<EquipmentConfig> Equipment,
    List<RecipeConfig> Recipes);

internal sealed record ConfigRequest(string Revision, List<EquipmentConfig> Equipment, List<RecipeConfig>? Recipes);

internal sealed record NewConfigRequest(string FileName, string ProjectName,
    string? OpcServer, RuntimeMode RuntimeMode);

internal sealed record StartSimulationRequest(string FileName, string Revision);
