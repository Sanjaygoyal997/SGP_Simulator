namespace SgpSimulator.Core.Configuration;

public sealed class ShiftSettings
{
    public int StartHour { get; set; } = 7;

    public int DurationHours { get; set; } = 8;

    public string[] Labels { get; set; } = [];
}

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public string OutputPath { get; set; } = "./Data";

    public string ProcessId { get; set; } = "PCP001";

    public ShiftSettings Shift { get; set; } = new();

    public int TickIntervalMs { get; set; } = 1000;

    public string ActiveRecipe { get; set; } = string.Empty;

    public RecipeProfile[] Recipes { get; set; } = [];

    public ProcessConfig[] Processes { get; set; } = [];

    public RecipeProfile GetRecipe(string name)
    {
        var recipe = Array.Find(Recipes, r => r.Name == name);
        if (recipe is null)
        {
            throw new InvalidOperationException(
                $"Recipe '{name}' was not found among the configured Recipes.");
        }

        return recipe;
    }

    public IReadOnlyList<ProcessConfig> GetEffectiveProcesses()
    {
        if (Processes.Length > 0)
        {
            return Processes;
        }

        return
        [
            new ProcessConfig
            {
                ProcessId = ProcessId,
                OutputPath = OutputPath,
                ActiveRecipe = ActiveRecipe,
                TickIntervalMs = TickIntervalMs,
                Shift = Shift
            }
        ];
    }
}
