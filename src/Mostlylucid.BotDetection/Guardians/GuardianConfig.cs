using Microsoft.Extensions.Configuration;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Reads per-guardian configuration from the standard
///     <c>BotDetection:Guardians:{name}:*</c> section. Every guardian calls this
///     once at startup so the walker picks up the live values. The helper is
///     intentionally static (no DI needed) and pure (no side effects).
/// </summary>
public static class GuardianConfig
{
    /// <summary>
    ///     Reads <c>BotDetection:Guardians:{name}:Enabled</c> (default <c>true</c>)
    ///     and <c>BotDetection:Guardians:{name}:Interval</c> (default
    ///     <paramref name="defaultInterval"/>) from <paramref name="config"/>.
    /// </summary>
    /// <param name="config">Application configuration root.</param>
    /// <param name="name">Guardian name (matches <see cref="IGuardian.Name"/>).</param>
    /// <param name="defaultInterval">Interval to use when not present in config.</param>
    /// <returns>A value tuple of (Enabled, Interval).</returns>
    public static (bool Enabled, TimeSpan Interval) Read(
        IConfiguration config,
        string name,
        TimeSpan defaultInterval)
    {
        var prefix = $"BotDetection:Guardians:{name}";
        var enabled = config.GetValue<bool?>($"{prefix}:Enabled") ?? true;
        var interval = config.GetValue<TimeSpan?>($"{prefix}:Interval") ?? defaultInterval;
        return (enabled, interval);
    }
}
