namespace SgpSimulator.Core.OpcLogger;

/// <summary>How much generated cycles and stops deviate from the plan, so KPIs show realistic losses.</summary>
/// <param name="CycleVariation">Spread of each running period, as a fraction (0.05 = about ±5%).</param>
/// <param name="SlowCycleChance">Share of running periods that take 10-35% longer.</param>
/// <param name="BreakdownChance">Share of stops that become a breakdown.</param>
public sealed record VariationSettings(double CycleVariation, double SlowCycleChance, double BreakdownChance,
    int BreakdownMinSeconds, int BreakdownMaxSeconds)
{
    public static VariationSettings? From(OpcTagGroup group)
    {
        var settings = new VariationSettings(group.CycleVariationPercent / 100.0, group.SlowCyclePercent / 100.0,
            group.BreakdownPercent / 100.0, (int)Math.Round(group.BreakdownMinMinutes * 60), (int)Math.Round(group.BreakdownMaxMinutes * 60));
        return settings is { CycleVariation: <= 0, SlowCycleChance: <= 0, BreakdownChance: <= 0 } ? null : settings;
    }
}
