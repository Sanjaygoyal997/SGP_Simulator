using SgpSimulator.Core.Configuration;

namespace SgpSimulator.Core.Domain;

public enum BatchPhase
{
    Down,
    Running
}

public sealed class BatchCycleStateMachine
{
    private readonly RecipeProfile _recipe;
    private readonly Random _rng;

    public BatchPhase CurrentPhase { get; private set; } = BatchPhase.Running;

    public int PhaseElapsedSeconds { get; private set; }

    public int PhaseDurationSeconds { get; private set; }

    public BatchCycleStateMachine(RecipeProfile recipe, Random rng)
    {
        _recipe = recipe;
        _rng = rng;
        PhaseDurationSeconds = DrawRunningDuration();
    }

    public bool EnteredRunning { get; private set; }

    public bool EnteredDown { get; private set; }

    public void Tick()
    {
        EnteredRunning = false;
        EnteredDown = false;

        PhaseElapsedSeconds++;
        if (PhaseElapsedSeconds < PhaseDurationSeconds)
        {
            return;
        }

        PhaseElapsedSeconds = 0;
        if (CurrentPhase == BatchPhase.Running)
        {
            CurrentPhase = BatchPhase.Down;
            PhaseDurationSeconds = DrawDownDuration();
            EnteredDown = true;
        }
        else
        {
            CurrentPhase = BatchPhase.Running;
            PhaseDurationSeconds = DrawRunningDuration();
            EnteredRunning = true;
        }
    }

    private int DrawRunningDuration()
    {
        var cycle = Lerp(_recipe.CycleSeconds.Min, _recipe.CycleSeconds.Max, _rng.NextDouble());
        var down = Lerp(_recipe.DownPhaseSeconds.Min, _recipe.DownPhaseSeconds.Max, _rng.NextDouble());
        var running = cycle - down;
        return (int)Math.Max(60, running);
    }

    private int DrawDownDuration()
    {
        if (_rng.NextDouble() < _recipe.StoppageProbability)
        {
            return (int)Lerp(_recipe.StoppageSeconds.Min, _recipe.StoppageSeconds.Max, _rng.NextDouble());
        }

        return (int)Lerp(_recipe.DownPhaseSeconds.Min, _recipe.DownPhaseSeconds.Max, _rng.NextDouble());
    }

    private static double Lerp(double min, double max, double t) => min + (max - min) * t;
}
