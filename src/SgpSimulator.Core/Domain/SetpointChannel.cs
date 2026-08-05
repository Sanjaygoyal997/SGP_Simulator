namespace SgpSimulator.Core.Domain;

public sealed class SetpointChannel(double startValue, double variance, Random rng, double reversionRate = 0.2)
{
    private double _value = startValue;

    public int Value => (int)Math.Round(_value);

    public void Tick(double target)
    {
        _value += (target - _value) * reversionRate + rng.NextGaussian(0, variance * 0.15);
    }
}
