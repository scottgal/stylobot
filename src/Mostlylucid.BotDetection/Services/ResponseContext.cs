namespace Mostlylucid.BotDetection.Services;

public sealed record ResponseContext(
    int StatusCode,
    long LatencyMs,
    string Path,
    string? ActionPolicyName);
