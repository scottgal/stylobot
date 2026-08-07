namespace Mostlylucid.SignalShingle.AspNetCore;

public sealed class SignalShingleUiOptions
{
    public string EndpointPrefix { get; set; } = "/_signal-shingle";
    public string HubPath { get; set; } = "/_signal-shingle-hub";
}
