using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using OllamaSharp;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that generates LLM-based descriptions for bot clusters (GraphRAG-style).
///     Subscribes to BotClusterService.ClustersUpdated events and processes clusters asynchronously.
///     NEVER runs in the request pipeline - only fires after background clustering completes.
///     Pushes updates via IClusterDescriptionCallback (SignalR) so dashboard users see live updates.
/// </summary>
public class BotClusterDescriptionService : IDisposable
{
    private readonly ILogger<BotClusterDescriptionService> _logger;
    private readonly BotClusterService _clusterService;
    private readonly ClusterOptions _options;
    private readonly BotDetectionOptions _botOptions;
    private readonly IClusterDescriptionCallback? _callback;
    private IOllamaApiClient? _ollama;
    private bool _ollamaChecked;

    public BotClusterDescriptionService(
        ILogger<BotClusterDescriptionService> logger,
        BotClusterService clusterService,
        IOptions<BotDetectionOptions> options,
        IClusterDescriptionCallback? callback = null)
    {
        _logger = logger;
        _clusterService = clusterService;
        _options = options.Value.Cluster;
        _botOptions = options.Value;
        _callback = callback;

        // Always subscribe to cluster updates if we have a callback (to broadcast cluster list to dashboard)
        // LLM descriptions are gated separately inside ProcessClustersAsync
        if (_options.EnableLlmDescriptions || _callback != null)
        {
            _clusterService.ClustersUpdated += OnClustersUpdated;
            if (_options.EnableLlmDescriptions)
                _logger.LogInformation(
                    "BotClusterDescriptionService enabled (model={Model})",
                    _options.DescriptionModel);
            else
                _logger.LogInformation("BotClusterDescriptionService: LLM disabled, broadcasting cluster updates only");
        }
    }

    private void OnClustersUpdated(IReadOnlyList<BotCluster> clusters, IReadOnlyList<SignatureBehavior> behaviors)
    {
        // Fire-and-forget background processing - don't block the clustering thread
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessClustersAsync(clusters, behaviors, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error generating cluster descriptions");
            }
        });
    }

    internal async Task ProcessClustersAsync(
        IReadOnlyList<BotCluster> clusters,
        IReadOnlyList<SignatureBehavior> behaviors,
        CancellationToken ct)
    {
        // Broadcast the full cluster list immediately (before LLM descriptions)
        if (_callback != null)
        {
            try
            {
                await _callback.OnClustersRefreshedAsync(clusters, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast cluster refresh");
            }
        }

        var client = GetOllamaClient();
        if (client == null)
        {
            _logger.LogDebug("Ollama not available, skipping cluster descriptions");
            return;
        }

        var behaviorMap = behaviors.ToDictionary(b => b.Signature);

        foreach (var cluster in clusters)
        {
            if (ct.IsCancellationRequested) break;

            // Skip if cluster already has a description from a previous run
            if (!string.IsNullOrEmpty(cluster.Description))
                continue;

            try
            {
                var members = cluster.MemberSignatures
                    .Where(s => behaviorMap.ContainsKey(s))
                    .Select(s => behaviorMap[s])
                    .ToList();

                if (members.Count == 0) continue;

                var result = await GenerateDescriptionAsync(client, cluster, members, ct);
                if (result == null) continue;

                // Update the cluster in the service (atomic snapshot swap)
                _clusterService.UpdateClusterDescription(
                    cluster.ClusterId,
                    result.Value.Name,
                    result.Value.Description);

                _logger.LogInformation(
                    "LLM described cluster {ClusterId}: '{Name}' ({Confidence:F2})",
                    cluster.ClusterId[..16], result.Value.Name, result.Value.Confidence);

                // Push live update via SignalR
                if (_callback != null)
                {
                    await _callback.OnClusterDescriptionUpdatedAsync(
                        cluster.ClusterId,
                        result.Value.Name,
                        result.Value.Description,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to generate description for cluster {ClusterId}", cluster.ClusterId);
            }
        }
    }

    private async Task<ClusterDescription?> GenerateDescriptionAsync(
        IOllamaApiClient client,
        BotCluster cluster,
        List<SignatureBehavior> members,
        CancellationToken ct)
    {
        var avgRate = members.Average(m =>
        {
            var duration = (m.LastSeen - m.FirstSeen).TotalSeconds;
            return duration > 0 ? m.RequestCount / (duration / 60.0) : 0;
        });
        var avgEntropy = members.Average(m => m.PathEntropy);
        var avgTimingCoeff = members.Average(m => m.TimingCoefficient);
        var datacenterPercent = members.Count(m => m.IsDatacenter) * 100.0 / members.Count;
        var uniqueAsns = members.Select(m => m.Asn).Where(a => !string.IsNullOrEmpty(a)).Distinct().Count();
        var uniqueCountries = members.Select(m => m.CountryCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().Count();

        var topPaths = members
            .SelectMany(m => m.Requests.Select(r => r.Path))
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key);

        var jsonExample = """{"name": "Short descriptive name (2-5 words)", "description": "2-3 sentence technical summary", "confidence": 0.85}""";

        var prompt = $"""
            You are analyzing a detected bot cluster. Generate a concise name and description.

            CLUSTER DATA:
            Type: {cluster.Type}
            Members: {cluster.MemberCount}
            Average Bot Probability: {cluster.AverageBotProbability:F2}
            Dominant Country: {cluster.DominantCountry ?? "unknown"}
            Dominant ASN: {cluster.DominantAsn ?? "unknown"}
            Temporal Density: {cluster.TemporalDensity:F2} (1.0 = all active simultaneously)
            Average Similarity: {cluster.AverageSimilarity:F2}

            BEHAVIORAL SIGNALS:
            - Average request rate: {avgRate:F1} requests/min
            - Path entropy: {avgEntropy:F2} (0=focused, 4=broad crawl)
            - Timing regularity: {avgTimingCoeff:F2} (low=robotic, high=human-like)
            - Most common paths: {string.Join(", ", topPaths)}

            INFRASTRUCTURE:
            - Datacenter traffic: {datacenterPercent:F0}%
            - ASN diversity: {uniqueAsns} providers
            - Country diversity: {uniqueCountries} countries

            Generate a JSON response with exactly these fields:
            {jsonExample}

            Be creative but accurate. Focus on observable behavior, not speculation about identity.
            Respond with ONLY the JSON object, no other text.
            """;

        try
        {
            var chat = new Chat(client)
            {
                Options = new OllamaSharp.Models.RequestOptions { Temperature = 0.7f },
                Think = false
            };
            client.SelectedModel = _options.DescriptionModel;

            var responseBuilder = new StringBuilder();
            await foreach (var token in chat.SendAsync(prompt, ct))
                responseBuilder.Append(token);

            return ParseResponse(responseBuilder.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM generation failed for cluster");
            return null;
        }
    }

    private ClusterDescription? ParseResponse(string response)
    {
        try
        {
            // Try to extract JSON from the response (LLM may add extra text)
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < jsonStart) return null;

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = root.GetProperty("name").GetString() ?? "Unknown";
            var description = root.GetProperty("description").GetString() ?? "";
            var confidence = root.TryGetProperty("confidence", out var conf)
                ? conf.GetDouble() : 0.5;

            return new ClusterDescription(name, description, confidence);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse LLM cluster description response");
            return null;
        }
    }

    private IOllamaApiClient? GetOllamaClient()
    {
        if (_ollama != null) return _ollama;
        if (_ollamaChecked) return null;
        _ollamaChecked = true;

        try
        {
            var endpoint = _options.DescriptionEndpoint
                           ?? _botOptions.AiDetection.Ollama.Endpoint;
            if (string.IsNullOrEmpty(endpoint)) return null;

            _ollama = new OllamaApiClient(new Uri(endpoint));
            return _ollama;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create Ollama client for cluster descriptions");
            return null;
        }
    }

    public void Dispose()
    {
        _clusterService.ClustersUpdated -= OnClustersUpdated;
    }

    internal readonly record struct ClusterDescription(string Name, string Description, double Confidence);
}

/// <summary>
///     Callback interface for live cluster description updates via SignalR.
///     Implemented by the UI project's SignalR hub.
/// </summary>
public interface IClusterDescriptionCallback
{
    Task OnClusterDescriptionUpdatedAsync(
        string clusterId, string label, string description, CancellationToken ct = default);

    /// <summary>
    ///     Called when the full cluster list is refreshed (before LLM descriptions).
    ///     Default implementation does nothing for backward compatibility.
    /// </summary>
    Task OnClustersRefreshedAsync(
        IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
        => Task.CompletedTask;
}
