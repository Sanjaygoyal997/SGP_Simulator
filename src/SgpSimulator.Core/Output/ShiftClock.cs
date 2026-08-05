using SgpSimulator.Core.Configuration;

namespace SgpSimulator.Core.Output;

public readonly record struct ShiftIdentity(DateOnly ShiftDate, string Label)
{
    public string ToFileName() => $"{ShiftDate:ddMMMyy}{Label}.txt";
}

public sealed class ShiftClock(ShiftSettings settings)
{
    private readonly ShiftSettings _settings = settings;

    public ShiftIdentity Resolve(DateTime timestamp)
    {
        var duration = _settings.DurationHours;
        var labelCount = _settings.Labels.Length;
        if (labelCount == 0 || duration <= 0 || duration * labelCount != 24)
        {
            throw new InvalidOperationException(
                "Shift.Labels length * Shift.DurationHours must equal 24.");
        }

        var hoursSinceStart = (timestamp.TimeOfDay.TotalHours - _settings.StartHour + 24) % 24;
        var shiftIndex = (int)(hoursSinceStart / duration);
        var shiftBeginTime = timestamp.Date.AddHours(_settings.StartHour + shiftIndex * duration);
        if (shiftBeginTime > timestamp)
        {
            shiftBeginTime = shiftBeginTime.AddDays(-1);
        }

        return new ShiftIdentity(DateOnly.FromDateTime(shiftBeginTime), _settings.Labels[shiftIndex]);
    }
}
