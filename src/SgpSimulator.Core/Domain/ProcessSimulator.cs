using SgpSimulator.Core.Configuration;

namespace SgpSimulator.Core.Domain;

public sealed class ProcessSimulator
{
    private readonly RecipeProfile _recipe;
    private readonly Random _rng;
    private readonly BatchCycleStateMachine _batchCycle;
    private readonly StepValueChannels _stepValues;
    private readonly PulseChannelPair _pulses;
    private readonly DriftChannel _temp27;
    private readonly DriftChannel _temp28;
    private readonly SetpointChannel _setpoint31;
    private readonly SetpointChannel _setpoint32;

    private long _elapsedSeconds;

    public ProcessSimulator(RecipeProfile recipe, int? seed = null)
    {
        _recipe = recipe;
        _rng = seed is null ? new Random() : new Random(seed.Value);
        _batchCycle = new BatchCycleStateMachine(recipe, _rng);
        _stepValues = new StepValueChannels(recipe.StepChannels, _rng);
        _pulses = new PulseChannelPair(recipe, _rng);
        _temp27 = new DriftChannel(recipe.StartupAmbient, recipe.TempVolatility, _rng);
        _temp28 = new DriftChannel(recipe.StartupAmbient - 0.06, recipe.TempVolatility, _rng);
        _setpoint31 = new SetpointChannel(recipe.StartupAmbient, recipe.SetpointVariance, _rng);
        _setpoint32 = new SetpointChannel(recipe.StartupAmbient, recipe.SetpointVariance, _rng);
    }

    public SimulatedRow Tick(DateTime timestamp)
    {
        _batchCycle.Tick();
        if (_batchCycle.EnteredRunning)
        {
            _stepValues.OnNewCycle();
            _pulses.OnEnterRunning();
        }
        else if (_batchCycle.EnteredDown)
        {
            _pulses.OnEnterDown(_batchCycle.PhaseDurationSeconds);
        }

        _pulses.Tick(_batchCycle.PhaseElapsedSeconds);

        var rampFraction = Math.Min(1.0, ++_elapsedSeconds / _recipe.StartupRampSeconds);
        var setpointTarget = Lerp(_recipe.StartupAmbient, _recipe.Setpoint, rampFraction);
        var tempTarget = Lerp(_recipe.StartupAmbient, _recipe.TempBaseline, rampFraction);

        if (_batchCycle.CurrentPhase == BatchPhase.Down &&
            _batchCycle.PhaseElapsedSeconds > _recipe.TempStoppageTriggerSeconds)
        {
            var stoppageWindow = Math.Max(1, _batchCycle.PhaseDurationSeconds - _recipe.TempStoppageTriggerSeconds);
            var stoppageProgress = Math.Min(
                1.0,
                (_batchCycle.PhaseElapsedSeconds - _recipe.TempStoppageTriggerSeconds) / stoppageWindow);
            tempTarget = Lerp(tempTarget, _recipe.TempStoppageFloor, stoppageProgress);
        }

        _temp27.Tick(tempTarget);
        _temp28.Tick(tempTarget - 0.06);
        _setpoint31.Tick(setpointTarget);
        _setpoint32.Tick(setpointTarget);

        var channels = new object?[SimulatedRow.ChannelCount];
        var isRunning = _batchCycle.CurrentPhase == BatchPhase.Running ? 1 : 0;
        for (var i = 0; i < 14; i++)
        {
            channels[i] = isRunning;
        }

        channels[14] = _recipe.Name;
        channels[15] = _recipe.Name;

        for (var i = 0; i < _stepValues.Values.Count && i < 6; i++)
        {
            channels[16 + i] = _stepValues.Values[i];
        }

        channels[22] = null;
        channels[23] = null;
        channels[24] = null;
        channels[25] = null;

        channels[26] = Math.Round(_temp27.Value, 2);
        channels[27] = Math.Round(_temp28.Value, 2);
        channels[28] = Math.Round(_pulses.Primary, 2);
        channels[29] = Math.Round(_pulses.Secondary, 2);
        channels[30] = _setpoint31.Value;
        channels[31] = _setpoint32.Value;
        channels[32] = 0;
        channels[33] = 0;

        return new SimulatedRow { Timestamp = timestamp, Channels = channels };
    }

    private static double Lerp(double min, double max, double t) => min + (max - min) * t;
}
