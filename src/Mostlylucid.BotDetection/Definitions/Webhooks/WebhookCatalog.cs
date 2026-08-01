using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Definitions.Webhooks;

/// <summary>Named webhook provider (e.g. Stripe, GitHub) with its signature header and known source IP ranges.</summary>
public sealed record WebhookProvider(string Name, string SignatureHeader, string[] IpRanges);

/// <summary>
///     Loads the Webhook archetype seed (signature headers + named providers +
///     scoring knobs) from the embedded
///     <c>Definitions/Webhooks/*.archetype.yaml</c> resource(s).
///     Mirrors <see cref="Mostlylucid.BotDetection.Definitions.RegistryClients.RegistryClientCatalog"/>:
///     static <see cref="Default"/> for non-DI callers, per-resource fault isolation.
/// </summary>
public sealed class WebhookCatalog
{
    private static readonly Lazy<WebhookCatalog> _default = new(() => new WebhookCatalog());

    /// <summary>Process-wide default instance (embedded seed).</summary>
    public static WebhookCatalog Default => _default.Value;

    public WebhookCatalog(ILogger? logger = null)
    {
        var file = LoadFromEmbeddedResources(logger);
        SignatureHeaders = file.SignatureHeaders.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        Providers = file.Providers
            .Where(p => !string.IsNullOrEmpty(p.Name) && !string.IsNullOrEmpty(p.SignatureHeader))
            .Select(p => new WebhookProvider(p.Name, p.SignatureHeader, p.IpRanges))
            .ToList();
        CorroboratedConfidenceDelta = file.Scoring.CorroboratedConfidenceDelta;
        CorroboratedWeight = file.Scoring.CorroboratedWeight;
        DominanceMinCount = file.Scoring.DominanceMinCount;
        DominanceMinShare = file.Scoring.DominanceMinShare;
        VerifiedMin2xx = file.Scoring.VerifiedMin2xx;
    }

    /// <summary>Direct-construction overload for tests -- bypasses embedded-resource loading.</summary>
    internal WebhookCatalog(IReadOnlyList<string> signatureHeaders, IReadOnlyList<WebhookProvider> providers)
    {
        SignatureHeaders = signatureHeaders;
        Providers = providers;
        var s = new WebhookScoring();
        CorroboratedConfidenceDelta = s.CorroboratedConfidenceDelta;
        CorroboratedWeight = s.CorroboratedWeight;
        DominanceMinCount = s.DominanceMinCount;
        DominanceMinShare = s.DominanceMinShare;
        VerifiedMin2xx = s.VerifiedMin2xx;
    }

    /// <summary>Webhook signature/event header names (truth signal - presence is behavioural).</summary>
    public IReadOnlyList<string> SignatureHeaders { get; }

    /// <summary>Named webhook provider seeds.</summary>
    public IReadOnlyList<WebhookProvider> Providers { get; }

    /// <summary>Negative confidence delta for a corroborated webhook delivery.</summary>
    public double CorroboratedConfidenceDelta { get; }

    /// <summary>Weight of the corroborated contribution.</summary>
    public double CorroboratedWeight { get; }

    /// <summary>Minimum observed request count before a source IP can be considered dominant.</summary>
    public int DominanceMinCount { get; }

    /// <summary>Minimum share of requests from a single source IP to be considered dominant.</summary>
    public double DominanceMinShare { get; }

    /// <summary>Minimum successful (2xx) deliveries before a fingerprint is considered verified.</summary>
    public int VerifiedMin2xx { get; }

    private static WebhookArchetypeFile LoadFromEmbeddedResources(ILogger? logger)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Definitions.Webhooks") && n.EndsWith(".archetype.yaml"))
            .OrderBy(n => n);

        var merged = new WebhookArchetypeFile();

        foreach (var resourceName in resources)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    logger?.LogWarning("Could not load webhook resource: {Resource}", resourceName);
                    continue;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var file = YamlSerializer.Deserialize<WebhookArchetypeFile>(ms.ToArray());
                if (file is null) continue;

                if (file.SignatureHeaders is { Count: > 0 }) merged.SignatureHeaders.AddRange(file.SignatureHeaders);
                if (file.Providers is { Count: > 0 }) merged.Providers.AddRange(file.Providers);
                // Last-writer-wins for scoring; a single seed file is the norm.
                merged.Scoring = file.Scoring;
                if (!string.IsNullOrEmpty(file.ArchetypeId)) merged.ArchetypeId = file.ArchetypeId;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error loading webhook archetype from {Resource}", resourceName);
            }
        }

        logger?.LogInformation(
            "WebhookCatalog: loaded {Headers} signature headers, {Providers} named providers",
            merged.SignatureHeaders.Count, merged.Providers.Count);
        return merged;
    }
}
