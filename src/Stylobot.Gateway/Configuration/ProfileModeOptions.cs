namespace Stylobot.Gateway.Configuration;

public class ProfileModeOptions
{
    public const string SectionName = "Gateway:ProfileMode";

    public bool Enabled { get; set; }

    public int ChannelCapacity { get; set; } = 5000;

    public int Concurrency { get; set; } = 2;

    public string? DatabasePath { get; set; }
}
