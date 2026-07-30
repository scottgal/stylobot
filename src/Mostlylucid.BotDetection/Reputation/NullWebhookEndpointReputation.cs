namespace Mostlylucid.BotDetection.Reputation;

/// <summary>
///     Ephemeral-mode no-op: every read returns <c>false</c> (no dominant IP, no
///     verified record), every write is dropped. Mirrors the ephemeral null-object
///     pattern used elsewhere (e.g. <see cref="Data.NullWeightStore"/>) — swapped in by
///     <c>AddBotDetectionInMemory</c> so no <c>webhooks.db</c> file is created and
///     <see cref="Orchestration.Atoms.WebhookSensor"/> degrades to shape-only
///     (never-corroborated) scoring instead of throwing.
/// </summary>
public sealed class NullWebhookEndpointReputation : IWebhookEndpointReputation
{
    public void RecordRequest(string endpoint, string ip)
    {
        // no-op: ephemeral mode persists nothing
    }

    public void RecordOutcome(string endpoint, string ip, int statusCode)
    {
        // no-op: ephemeral mode persists nothing
    }

    public bool IsDominantIp(string endpoint, string ip) => false;

    public bool HasVerifiedRecord(string endpoint, string ip) => false;
}
