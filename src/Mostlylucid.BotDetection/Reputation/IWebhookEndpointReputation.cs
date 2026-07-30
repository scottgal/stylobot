namespace Mostlylucid.BotDetection.Reputation;

/// <summary>
///     Tracks, per (webhook receiver-endpoint, source-IP), request dominance ("commonest
///     IP") and a verified 2xx/4xx delivery track record. Consumed by the webhook sensor
///     (a later task) to corroborate a claimed webhook delivery against prior behaviour at
///     that endpoint, and by the post-<c>_next</c> outcome recorder to feed the track record
///     back in.
/// </summary>
public interface IWebhookEndpointReputation
{
    /// <summary>Records an inbound request from <paramref name="ip"/> to <paramref name="endpoint"/>.</summary>
    void RecordRequest(string endpoint, string ip);

    /// <summary>
    ///     Records the response outcome for a request from <paramref name="ip"/> to
    ///     <paramref name="endpoint"/>. A 2xx status increments the verified-success
    ///     counter; a 4xx status increments the rejected counter. Any other status
    ///     (notably 5xx) is neutral and touches neither counter — a receiver outage
    ///     must not demote a legitimate sender's track record.
    /// </summary>
    void RecordOutcome(string endpoint, string ip, int statusCode);

    /// <summary>
    ///     True when <paramref name="ip"/> is the dominant source of requests to
    ///     <paramref name="endpoint"/>: its request count meets the catalog's minimum
    ///     count AND its share of the endpoint's total request volume meets the
    ///     catalog's minimum share.
    /// </summary>
    bool IsDominantIp(string endpoint, string ip);

    /// <summary>
    ///     True when <paramref name="ip"/> has a verified delivery record at
    ///     <paramref name="endpoint"/>: at least the catalog's minimum count of 2xx
    ///     responses, and more 2xx responses than 4xx responses.
    /// </summary>
    bool HasVerifiedRecord(string endpoint, string ip);
}
