using SgpSimulator.Core.Configuration;

namespace SgpSimulator.Core.Domain;

public sealed class PulseChannelPair
{
    private readonly RecipeProfile _recipe;
    private readonly Random _rng;
    private readonly List<int> _pendingOffsets = [];

    private double _decayRate;
    private double _primary;
    private double _secondary;

    public PulseChannelPair(RecipeProfile recipe, Random rng)
    {
        _recipe = recipe;
        _rng = rng;
    }

    public double Primary => _primary;

    public double Secondary => _secondary;

    public void OnEnterDown(int downPhaseDurationSeconds)
    {
        _pendingOffsets.Clear();
        foreach (var fraction in _recipe.DownPhasePulseOffsets)
        {
            var jitter = (_rng.NextDouble() - 0.5) * 0.05;
            var offset = (int)Math.Clamp((fraction + jitter) * downPhaseDurationSeconds, 1, downPhaseDurationSeconds - 1);
            _pendingOffsets.Add(offset);
        }
    }

    public void OnEnterRunning()
    {
        Trigger();
    }

    public void Tick(int phaseElapsedSeconds)
    {
        if (_pendingOffsets.Remove(phaseElapsedSeconds))
        {
            Trigger();
        }

        _primary *= _decayRate;
        _secondary += (_primary - _secondary) * 0.5;

        if (_primary < 0.005) _primary = 0;
        if (_secondary < 0.005) _secondary = 0;
    }

    private void Trigger()
    {
        _decayRate = Lerp(_recipe.PulseDecay.Min, _recipe.PulseDecay.Max, _rng.NextDouble());
        _primary = Lerp(_recipe.PulseAmplitude.Min, _recipe.PulseAmplitude.Max, _rng.NextDouble());
    }

    private static double Lerp(double min, double max, double t) => min + (max - min) * t;
}
