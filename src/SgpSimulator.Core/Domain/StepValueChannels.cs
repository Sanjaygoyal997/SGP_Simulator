using SgpSimulator.Core.Configuration;

namespace SgpSimulator.Core.Domain;

public sealed class StepValueChannels
{
    private const double RefreshProbability = 0.85;

    private readonly StepChannelSetting[] _settings;
    private readonly Random _rng;
    private readonly int[] _current;

    public StepValueChannels(StepChannelSetting[] settings, Random rng)
    {
        _settings = settings;
        _rng = rng;
        _current = new int[settings.Length];
        for (var i = 0; i < settings.Length; i++)
        {
            _current[i] = settings[i].Base;
        }
    }

    public IReadOnlyList<int> Values => _current;

    public void OnNewCycle()
    {
        for (var i = 0; i < _settings.Length; i++)
        {
            if (_rng.NextDouble() > RefreshProbability)
            {
                continue;
            }

            var setting = _settings[i];
            var step = _rng.Next(-setting.MaxStep, setting.MaxStep + 1);
            _current[i] = Math.Clamp(_current[i] + step, setting.Min, setting.Max);
        }
    }
}
