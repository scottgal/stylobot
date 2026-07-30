using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Definitions.Webhooks;

/// <summary>
///     One named webhook provider seed for the Webhook archetype. The provider
///     name + signature header are hints - a signature header match on its own
///     never lowers the score; it must be corroborated (named provider, learned
///     dominant source IP, or a verified 2xx track record).
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class WebhookProviderSeed
{
    /// <summary>Display name, e.g. <c>Stripe</c>, <c>GitHub</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The signature/event header this provider sends, e.g. <c>Stripe-Signature</c>.</summary>
    public string SignatureHeader { get; set; } = "";

    /// <summary>
    ///     Known source IP ranges (CIDR) for this provider, when published. Seeded
    ///     empty here - populated by a later increment.
    /// </summary>
    public string[] IpRanges { get; set; } = [];
}

/// <summary>Scoring knobs for a corroborated webhook delivery (strong good bias, no early-exit).</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class WebhookScoring
{
    /// <summary>
    ///     Negative confidence delta applied when the webhook shape is corroborated.
    ///     Negative biases the aggregate toward low-threat. Deliberately NOT an
    ///     early exit - detection still runs.
    /// </summary>
    public double CorroboratedConfidenceDelta { get; set; } = -0.8;

    /// <summary>Weight of the corroborated contribution.</summary>
    public double CorroboratedWeight { get; set; } = 2.5;

    /// <summary>Minimum observed request count before a source IP can be considered dominant.</summary>
    public int DominanceMinCount { get; set; } = 20;

    /// <summary>Minimum share of requests from a single source IP to be considered dominant.</summary>
    public double DominanceMinShare { get; set; } = 0.6;

    /// <summary>Minimum successful (2xx) deliveries before a fingerprint is considered verified.</summary>
    public int VerifiedMin2xx { get; set; } = 10;
}

/// <summary>Top-level structure of the <c>webhook.archetype.yaml</c> seed manifest.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class WebhookArchetypeFile
{
    /// <summary>Stable archetype identifier (<c>webhook</c>).</summary>
    public string ArchetypeId { get; set; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Webhook signature/event header names (truth signal - presence is behavioural).</summary>
    public List<string> SignatureHeaders { get; set; } = [];

    /// <summary>Named webhook provider seed list (hints only).</summary>
    public List<WebhookProviderSeed> Providers { get; set; } = [];

    /// <summary>Scoring knobs.</summary>
    public WebhookScoring Scoring { get; set; } = new();
}
