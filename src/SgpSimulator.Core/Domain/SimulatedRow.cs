namespace SgpSimulator.Core.Domain;

public sealed class SimulatedRow
{
    public required DateTime Timestamp { get; init; }

    public required object?[] Channels { get; init; }

    public const int ChannelCount = 34;
}
