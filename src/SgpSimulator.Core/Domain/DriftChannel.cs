namespace SgpSimulator.Core.Domain;

public sealed class DriftChannel(double startValue, double volatility, Random rng, double reversionRate = 0.01)
{
    public double Value { get; private set; } = startValue;

    public void Tick(double target)
    {
        Value += (target - Value) * reversionRate + rng.NextGaussian(0, volatility);
    }
}
