using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.Atoms.Ephemeral;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Adapts the <see cref="EphemeralPrompt"/> back to the reflective LLM provider
///     call that the legacy <c>LlmDescriptionCoordinator.ProcessClusterAsync</c>
///     +<c>GenerateClusterDescriptionAsync</c> used. Preserves the same reflective
///     dispatch + <see cref="UnconditionalSuppressMessageAttribute"/> suppressions —
///     the cluster-naming feature is JIT-only by design.
///
///     Flow (mirrors the legacy verbatim):
///     <list type="number">
///         <item>Lazy-resolve <c>ILlmProvider</c> via <see cref="Type.GetType(string)"/>
///               on the Llm assembly (null when the optional Llm package isn't loaded).</item>
///         <item>Deserialize <see cref="EphemeralPrompt.UserPrompt"/> back into the
///               cluster + members shape the <see cref="ClusterNamingPrompter"/> serialised.</item>
///         <item>Compose the same prompt string the legacy
///               <c>GenerateClusterDescriptionAsync</c> built and dispatch via the same
///               reflective <c>CompleteAsync</c> method handle.</item>
///         <item>Parse the JSON response into <see cref="ClusterNamingResult"/>;
///               throw on any failure so the EphemeralLlmCoordinator counts the fault
///               and skips writeback — the picker surfaces the item again next tick.</item>
///     </list>
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Reflective LLM provider call; cluster-naming is JIT-only.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "Dynamic dispatch; cluster-naming is JIT-only.")]
public sealed class ClusterLlmInvoker : IEphemeralLlmInvoker<ClusterNamingResult>
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ClusterLlmInvoker>? _logger;
    private object? _llmProvider;
    private bool _providerChecked;

    public ClusterLlmInvoker(IServiceProvider services, ILogger<ClusterLlmInvoker>? logger = null)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<ClusterNamingResult> InvokeAsync(EphemeralPrompt prompt, CancellationToken ct)
    {
        var provider = GetLlmProvider()
                       ?? throw new InvalidOperationException("ILlmProvider is not resolvable; cluster-naming requires the Mostlylucid.BotDetection.Llm package.");

        var payload = JsonSerializer.Deserialize<ClusterNamingPrompter.ClusterPromptPayload>(prompt.UserPrompt)
                      ?? throw new InvalidOperationException("ClusterLlmInvoker: failed to deserialize cluster prompt payload.");

        var response = await CompleteAsync(provider, payload, ct);

        return ParseClusterResponse(response)
               ?? throw new InvalidOperationException("ClusterLlmInvoker: failed to parse LLM cluster response as JSON.");
    }

    [RequiresUnreferencedCode("Calls GetLlmProvider + the reflective ILlmRequest/CompleteAsync handle; cluster-naming is JIT-only.")]
    [RequiresDynamicCode("Uses dynamic dispatch through Type.GetType + Activator + reflective MethodInfo.Invoke.")]
    private async Task<string> CompleteAsync(
        object provider,
        ClusterNamingPrompter.ClusterPromptPayload payload,
        CancellationToken ct)
    {
        var cluster = payload.Cluster;
        var members = payload.Members;

        // Aggregate computation copied verbatim from the legacy
        // LlmDescriptionCoordinator.GenerateClusterDescriptionAsync.
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

        var promptText = $"""
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

        var requestType = Type.GetType("Mostlylucid.BotDetection.Llm.LlmRequest, Mostlylucid.BotDetection.Llm")
                          ?? throw new InvalidOperationException("ClusterLlmInvoker: Mostlylucid.BotDetection.Llm.LlmRequest not resolvable.");

        var request = Activator.CreateInstance(requestType)
                      ?? throw new InvalidOperationException("ClusterLlmInvoker: failed to instantiate LlmRequest.");

        requestType.GetProperty("Prompt")!.SetValue(request, promptText);
        requestType.GetProperty("Temperature")!.SetValue(request, 0.7f);
        requestType.GetProperty("MaxTokens")!.SetValue(request, 300);
        requestType.GetProperty("TimeoutMs")!.SetValue(request, 15000);

        var completeMethod = provider.GetType().GetMethod("CompleteAsync")
                             ?? throw new InvalidOperationException("ClusterLlmInvoker: ILlmProvider.CompleteAsync not resolvable.");

        var task = (Task<string>)completeMethod.Invoke(provider, new[] { request, (object)ct })!;
        return await task;
    }

    private ClusterNamingResult? ParseClusterResponse(string response)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < jsonStart) return null;

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = root.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name)) return null;
            var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;

            return new ClusterNamingResult(name, description);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to parse LLM cluster description response");
            return null;
        }
    }

    [RequiresUnreferencedCode(
        "Resolves ILlmProvider via Type.GetType to keep the Llm assembly an optional runtime dependency; cluster-naming is JIT-only.")]
    private object? GetLlmProvider()
    {
        if (_llmProvider != null) return _llmProvider;
        if (_providerChecked) return null;
        _providerChecked = true;

        try
        {
            var providerType = Type.GetType("Mostlylucid.BotDetection.Llm.ILlmProvider, Mostlylucid.BotDetection.Llm");
            if (providerType == null) return null;

            _llmProvider = _services.GetService(providerType);
            return _llmProvider;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve ILlmProvider for cluster descriptions");
            return null;
        }
    }
}
