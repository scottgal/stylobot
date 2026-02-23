using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Single constrained coordinator for all background LLM description work.
///     Uses KeyedSequentialAtom to limit concurrency to 50% of CPU count,
///     with per-key sequentiality (never parallel on the same signature/cluster).
///     Replaces raw Task.Run fire-and-forget in SignatureDescriptionService and BotClusterDescriptionService.
/// </summary>
public sealed class LlmDescriptionCoordinator : IAsyncDisposable
{
    private readonly KeyedSequentialAtom<LlmDescriptionRequest, string> _atom;
    private readonly IBotNameSynthesizer _synthesizer;
    private readonly ILlmResultCallback? _resultCallback;
    private readonly IClusterDescriptionCallback? _clusterCallback;
    private readonly BotClusterService _clusterService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmDescriptionCoordinator> _logger;

    // Lazy-resolved LLM provider for cluster descriptions (reflection-based, same as BotClusterDescriptionService)
    private object? _llmProvider;
    private bool _providerChecked;

    public LlmDescriptionCoordinator(
        IBotNameSynthesizer synthesizer,
        BotClusterService clusterService,
        IServiceProvider serviceProvider,
        ILogger<LlmDescriptionCoordinator> logger,
        ILlmResultCallback? resultCallback = null,
        IClusterDescriptionCallback? clusterCallback = null)
    {
        _synthesizer = synthesizer;
        _clusterService = clusterService;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _resultCallback = resultCallback;
        _clusterCallback = clusterCallback;

        var maxConcurrency = Math.Max(1, Environment.ProcessorCount / 2);
        _atom = new KeyedSequentialAtom<LlmDescriptionRequest, string>(
            req => req.Key,
            async (req, ct) => await ProcessAsync(req, ct),
            maxConcurrency: maxConcurrency,
            perKeyConcurrency: 1,
            enableFairScheduling: true);

        _logger.LogInformation(
            "LlmDescriptionCoordinator started (maxConcurrency={MaxConcurrency})",
            maxConcurrency);
    }

    /// <summary>
    ///     Enqueue a signature for LLM description generation.
    /// </summary>
    public ValueTask<long> EnqueueSignatureAsync(
        string signature,
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
    {
        return _atom.EnqueueAsync(new LlmDescriptionRequest
        {
            Key = signature,
            Type = LlmRequestType.Signature,
            Signals = signals
        }, ct);
    }

    /// <summary>
    ///     Enqueue a cluster for LLM description generation.
    /// </summary>
    public ValueTask<long> EnqueueClusterAsync(
        string clusterId,
        BotCluster cluster,
        IReadOnlyList<SignatureBehavior> members,
        CancellationToken ct = default)
    {
        return _atom.EnqueueAsync(new LlmDescriptionRequest
        {
            Key = clusterId,
            Type = LlmRequestType.Cluster,
            Cluster = cluster,
            ClusterMembers = members
        }, ct);
    }

    /// <summary>
    ///     Get current processing statistics.
    /// </summary>
    public (int Pending, int Active, int Completed, int Failed) GetStats() => _atom.Stats();

    private async Task ProcessAsync(LlmDescriptionRequest req, CancellationToken ct)
    {
        try
        {
            switch (req.Type)
            {
                case LlmRequestType.Signature:
                    await ProcessSignatureAsync(req, ct);
                    break;
                case LlmRequestType.Cluster:
                    await ProcessClusterAsync(req, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM description failed for {Type} key={Key}",
                req.Type, req.Key[..Math.Min(16, req.Key.Length)]);
        }
    }

    private async Task ProcessSignatureAsync(LlmDescriptionRequest req, CancellationToken ct)
    {
        if (!_synthesizer.IsReady || req.Signals == null)
            return;

        var (name, description) = await _synthesizer.SynthesizeDetailedAsync(req.Signals, ct: ct);

        if (!string.IsNullOrEmpty(name))
        {
            _logger.LogInformation(
                "Generated description for signature {Sig}: '{Name}'",
                req.Key[..Math.Min(16, req.Key.Length)], name);

            if (_resultCallback != null)
            {
                await _resultCallback.OnSignatureDescriptionAsync(
                    req.Key, name, description ?? name, ct);
            }
        }
    }

    private async Task ProcessClusterAsync(LlmDescriptionRequest req, CancellationToken ct)
    {
        if (req.Cluster == null || req.ClusterMembers == null)
            return;

        var provider = GetLlmProvider();
        if (provider == null)
            return;

        var result = await GenerateClusterDescriptionAsync(
            provider, req.Cluster, req.ClusterMembers, ct);

        if (result == null)
            return;

        _clusterService.UpdateClusterDescription(
            req.Cluster.ClusterId,
            result.Value.Name,
            result.Value.Description);

        _logger.LogInformation(
            "LLM described cluster {ClusterId}: '{Name}' ({Confidence:F2})",
            req.Cluster.ClusterId[..Math.Min(16, req.Cluster.ClusterId.Length)],
            result.Value.Name, result.Value.Confidence);

        if (_clusterCallback != null)
        {
            await _clusterCallback.OnClusterDescriptionUpdatedAsync(
                req.Cluster.ClusterId,
                result.Value.Name,
                result.Value.Description,
                ct);
        }
    }

    private async Task<ClusterDescriptionResult?> GenerateClusterDescriptionAsync(
        dynamic provider,
        BotCluster cluster,
        IReadOnlyList<SignatureBehavior> members,
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
            You are analyzing a detected traffic cluster. Generate a concise name and description.

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

            return ParseClusterResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM generation failed for cluster");
            return null;
        }
    }

    private ClusterDescriptionResult? ParseClusterResponse(string response)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < jsonStart) return null;

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = root.GetProperty("name").GetString() ?? "Unknown";
            var description = root.GetProperty("description").GetString() ?? "";
            var confidence = root.TryGetProperty("confidence", out var conf)
                ? conf.GetDouble() : 0.5;

            return new ClusterDescriptionResult(name, description, confidence);
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
            _logger.LogDebug(ex, "Failed to resolve ILlmProvider for descriptions");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var stats = _atom.Stats();
        _logger.LogInformation(
            "LlmDescriptionCoordinator disposing (pending={Pending}, completed={Completed}, failed={Failed})",
            stats.Pending, stats.Completed, stats.Failed);
        await _atom.DisposeAsync();
    }

    internal readonly record struct ClusterDescriptionResult(string Name, string Description, double Confidence);
}

internal sealed record LlmDescriptionRequest
{
    public required string Key { get; init; }
    public required LlmRequestType Type { get; init; }

    // For signature descriptions
    public IReadOnlyDictionary<string, object?>? Signals { get; init; }

    // For cluster descriptions
    public BotCluster? Cluster { get; init; }
    public IReadOnlyList<SignatureBehavior>? ClusterMembers { get; init; }
}

internal enum LlmRequestType
{
    Signature,
    Cluster
}
