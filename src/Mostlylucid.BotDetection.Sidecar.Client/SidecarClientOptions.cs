namespace Mostlylucid.BotDetection.Sidecar.Client;

public sealed class SidecarClientOptions
{
    public string Endpoint { get; set; } = "http://localhost:5090";
    public int TimeoutMs { get; set; } = 50;
}
