namespace Mostlylucid.StyloWall.Models;

public sealed class StyloWallOptions
{
    public const string SectionName = "StyloWall";

    public bool Enabled { get; set; } = true;

    public TriggerOptions Triggers { get; set; } = new();

    public List<StyloWallRouteRule> Routes { get; set; } = new();

    public int MaxBufferBytes { get; set; } = 4 * 1024 * 1024;

    public sealed class TriggerOptions
    {
        public bool AcceptHeader { get; set; } = true;

        public bool QueryString { get; set; } = true;

        public string QueryStringKey { get; set; } = "format";

        public bool DetectionVerdict { get; set; } = true;

        public double MinBotProbability { get; set; } = 0.75;
    }
}

public sealed class StyloWallRouteRule
{
    public string PathPrefix { get; set; } = "/";

    public string Mode { get; set; } = "markdown";

    public bool RequireBotVerdict { get; set; } = false;
}
