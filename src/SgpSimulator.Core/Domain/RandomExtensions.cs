namespace SgpSimulator.Core.Domain;

public static class RandomExtensions
{
    public static double NextGaussian(this Random rng, double mean = 0, double stdDev = 1)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var standardNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * standardNormal;
    }
}
