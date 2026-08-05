namespace SgpSimulator.Core.Configuration;

public sealed class RangeSetting
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class StepChannelSetting
{
    public int Base { get; set; }
    public int MaxStep { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
}

public sealed class RecipeProfile
{
    public string Name { get; set; } = string.Empty;

    public RangeSetting CycleSeconds { get; set; } = new() { Min = 730, Max = 950 };

    public RangeSetting DownPhaseSeconds { get; set; } = new() { Min = 100, Max = 320 };

    public double StoppageProbability { get; set; } = 0.03;

    public RangeSetting StoppageSeconds { get; set; } = new() { Min = 400, Max = 1200 };

    public StepChannelSetting[] StepChannels { get; set; } = [];

    public int Setpoint { get; set; } = 170;

    public double SetpointVariance { get; set; } = 1.5;

    public double StartupAmbient { get; set; } = 20.0;

    public double StartupRampSeconds { get; set; } = 90;

    public double TempBaseline { get; set; } = 22.2;

    public double TempVolatility { get; set; } = 0.02;

    public double TempStoppageFloor { get; set; } = -0.4;

    public double TempStoppageTriggerSeconds { get; set; } = 400;

    public RangeSetting PulseAmplitude { get; set; } = new() { Min = 0.25, Max = 0.4 };

    public RangeSetting PulseDecay { get; set; } = new() { Min = 0.55, Max = 0.7 };

    public double[] DownPhasePulseOffsets { get; set; } = [];
}
