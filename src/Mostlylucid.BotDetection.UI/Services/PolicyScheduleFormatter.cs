namespace Mostlylucid.BotDetection.UI.Services;

public static class PolicyScheduleFormatter
{
    /// <summary>
    ///     Render an auto-promote timestamp for the policy card meta footer.
    ///     Relative ("in 23h") when within 24h; absolute ("at 2026-06-25 09:00")
    ///     otherwise. 24h threshold matches the AutoPromoteRelativeThresholdHours
    ///     option in the spec; hardcoded here, refactored to options in Phase B.
    /// </summary>
    public static string ToRelativeOrAbsolute(DateTimeOffset at)
    {
        var delta = at - DateTimeOffset.UtcNow;
        if (delta.TotalHours is > 0 and < 24)
        {
            var hours = Math.Max(1, (int)Math.Round(delta.TotalHours));
            return $"in {hours}h";
        }
        return $"at {at.ToLocalTime():yyyy-MM-dd HH:mm}";
    }
}
