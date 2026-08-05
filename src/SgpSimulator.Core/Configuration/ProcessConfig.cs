namespace SgpSimulator.Core.Configuration;

public sealed class ProcessConfig
{
    public string ProcessId { get; set; } = string.Empty;

    public string? OutputPath { get; set; }

    public string ActiveRecipe { get; set; } = string.Empty;

    public int? TickIntervalMs { get; set; }

    public ShiftSettings? Shift { get; set; }
}
