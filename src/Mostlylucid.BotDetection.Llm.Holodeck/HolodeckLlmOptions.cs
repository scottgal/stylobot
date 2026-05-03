namespace Mostlylucid.BotDetection.Llm.Holodeck;

public class HolodeckLlmOptions
{
    public const string SectionName = "BotDetection:LlmHolodeck";
    public int TimeoutMs { get; set; } = 3000;
    public float Temperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 2048;
    public int CacheSize { get; set; } = 500;
    public int CacheTtlHours { get; set; } = 24;
}
