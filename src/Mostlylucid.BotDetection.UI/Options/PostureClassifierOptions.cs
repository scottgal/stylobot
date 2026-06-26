namespace Mostlylucid.BotDetection.UI.Options;

public sealed class PostureClassifierOptions
{
    public int StrictThresholdBlockRules { get; set; } = 3;
    public int BalancedThresholdBlockRules { get; set; } = 1;
    public TimeSpan StatsWindow { get; set; } = TimeSpan.FromHours(24);
    public bool EnableSuggestions { get; set; } = true;
}