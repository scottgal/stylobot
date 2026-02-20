using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that generates LLM-based descriptions for bot clusters (GraphRAG-style).
///     Subscribes to BotClusterService.ClustersUpdated events and processes clusters asynchronously.
///     NEVER runs in the request pipeline - only fires after background clustering completes.
///     Pushes updates via IClusterDescriptionCallback (SignalR) so dashboard users see live updates.
///     Uses ILlmProvider (from plugin packages) if available; otherwise skips LLM descriptions.
/// </summary>
public class BotClusterDescriptionService : IDisposable
{
    private readonly ILogger<BotClusterDescriptionService> _logger;
    private readonly BotClusterService _clusterService;
    private readonly ClusterOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IClusterDescriptionCallback? _callback;
    private object? _llmProvider;
    private bool _providerChecked;

    public BotClusterDescriptionService(
        ILogger<BotClusterDescriptionService> logger,
        BotClusterService clusterService,
        IOptions<BotDetectionOptions> options,
        IServiceProvider serviceProvider,
        IClusterDescriptionCallback? callback = null)
    {
        _logger = logger;
        _clusterService = clusterService;
        _options = options.Value.Cluster;
        _serviceProvider = serviceProvider;
        _callback = callback;

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

        var provider = GetLlmProvider();
        if (provider == null)
        {
            _logger.LogDebug("No LLM provider available, skipping cluster descriptions");
            return;
        }

        var behaviorMap = behaviors.ToDictionary(b => b.Signature);

        foreach (var cluster in clusters)
        {
            if (ct.IsCancellationRequested) break;

            if (!string.IsNullOrEmpty(cluster.Description))
                continue;

            try
            {
                var members = cluster.MemberSignatures
                    .Where(s => behaviorMap.ContainsKey(s))
                    .Select(s => behaviorMap[s])
                    .ToList();

                if (members.Count == 0) continue;

                var result = await GenerateDescriptionAsync(provider, cluster, members, ct);
                if (result == null) continue;

                _clusterService.UpdateClusterDescription(
                    cluster.ClusterId,
                    result.Value.Name,
                    result.Value.Description);

                _logger.LogInformation(
                    "LLM described cluster {ClusterId}: '{Name}' ({Confidence:F2})",
                    cluster.ClusterId[..16], result.Value.Name, result.Value.Confidence);

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
        dynamic provider,
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
            // Use ILlmProvider.CompleteAsync via dynamic dispatch
            var requestType = Type.GetType("Mostlylucid.BotDetection.Llm.LlmRequest, Mostlylucid.BotDetection.Llm");
            if (requestType == null) return null;

            var request = Activator.CreateInstance(requestType);
            if (request == null) return null;

            requestType.GetProperty("Prompt")!.SetValue(request, prompt);
            requestType.GetProperty("Temperature")!.SetValue(request, 0.7f);
            requestType.GetProperty("MaxTokens")!.SetValue(request, 300);
            requestType.GetProperty("TimeoutMs")!.SetValue(request, 15000);

            var completeMethod = provider.GetType().GetMethod("CompleteAsync");
            if (completeMethod == null) return null;

            Task<string> task = completeMethod.Invoke(provider, new[] { request, ct });
            var response = await task;

            return ParseResponse(response);
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

    private object? GetLlmProvider()
    {
        if (_llmProvider != null) return _llmProvider;
        if (_providerChecked) return null;
        _providerChecked = true;

        try
        {
            var providerType = Type.GetType("Mostlylucid.BotDetection.Llm.ILlmProvider, Mostlylucid.BotDetection.Llm");
            if (providerType == null) return null;

            _llmProvider = _serviceProvider.GetService(providerType);
            return _llmProvider;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve ILlmProvider for cluster descriptions");
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
/// </summary>
public interface IClusterDescriptionCallback
{
    Task OnClusterDescriptionUpdatedAsync(
        string clusterId, string label, string description, CancellationToken ct = default);

    Task OnClustersRefreshedAsync(
        IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
        => Task.CompletedTask;
}
