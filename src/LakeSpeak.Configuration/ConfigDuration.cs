namespace LakeSpeak.Configuration;

/// <summary>Parses the compact durations accepted by configuration and command defaults.</summary>
internal static class ConfigDuration
{
    // CancellationTokenSource is the narrowest timer used by the Genie polling loop.
    // Reject values it cannot schedule while configuration is still actionable.
    private static readonly TimeSpan MaximumTimerDuration =
        TimeSpan.FromMilliseconds(int.MaxValue);

    internal static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            return false;
        }

        var unitTicks = value[^1] switch
        {
            's' => TimeSpan.TicksPerSecond,
            'm' => TimeSpan.TicksPerMinute,
            'h' => TimeSpan.TicksPerHour,
            _ => 0,
        };

        var digits = value.AsSpan(0, value.Length - 1);
        if (unitTicks == 0
            || digits.ContainsAnyExceptInRange('0', '9')
            || !long.TryParse(digits, out var amount)
            || amount <= 0)
        {
            return false;
        }

        try
        {
            duration = TimeSpan.FromTicks(checked(amount * unitTicks));
            return duration <= MaximumTimerDuration;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
