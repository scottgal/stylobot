namespace Mostlylucid.BotDetection.Data.Centroids;

public sealed class CentroidWriterOptions
{
    public const string SectionName = "BotDetection:CentroidWriter";

    public int ChannelCapacity { get; set; } = 2048;

    public TimeSpan DropLogCadence { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan ReconnectBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public double SamplingThreshold { get; set; } = 0.05;

    public double DecisionThreshold { get; set; } = 0.70;

    public double DecisionHalfLifeSeconds { get; set; } = 604800; // 7 days
}
