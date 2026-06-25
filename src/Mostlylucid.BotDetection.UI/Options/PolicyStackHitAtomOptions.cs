namespace Mostlylucid.BotDetection.UI.Options;

public sealed class PolicyStackHitAtomOptions
{
    public int MaxScopes { get; set; } = 1024;
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(26);
    public TimeSpan AgeOutTick { get; set; } = TimeSpan.FromMinutes(1);
}