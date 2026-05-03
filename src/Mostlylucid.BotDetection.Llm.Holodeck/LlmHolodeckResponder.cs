using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

public sealed class LlmHolodeckResponder : IHolodeckResponder
{
    private readonly ILlmProvider _llmProvider;
    private readonly HolodeckLlmOptions _options;
    private readonly HolodeckResponseCache _cache;
    private readonly ILogger<LlmHolodeckResponder> _logger;

    public LlmHolodeckResponder(
        ILlmProvider llmProvider,
        IOptions<HolodeckLlmOptions> options,
        ILogger<LlmHolodeckResponder> logger)
    {
        _llmProvider = llmProvider;
        _options = options.Value;
        _logger = logger;
        _cache = new HolodeckResponseCache(_options.CacheSize, TimeSpan.FromHours(_options.CacheTtlHours));
    }

    public bool IsAvailable => _llmProvider.IsReady;

    public async Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default)
    {
        var fingerprint = requestContext.Fingerprint ?? "unknown";

        if (_cache.TryGet(fingerprint, requestContext.Path, out var cached))
        {
            _logger.LogDebug("Holodeck cache hit for {Fp}:{Path}",
                fingerprint[..Math.Min(8, fingerprint.Length)], requestContext.Path);
            return cached!;
        }

        if (_llmProvider.IsReady)
        {
            try
            {
                var prompt = HolodeckPromptBuilder.Build(template, requestContext, canary);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_options.TimeoutMs);

                var content = await _llmProvider.CompleteAsync(new LlmRequest
                {
                    Prompt = prompt,
                    Temperature = _options.Temperature,
                    MaxTokens = _options.MaxTokens,
                    TimeoutMs = _options.TimeoutMs
                }, cts.Token);

                var response = new HolodeckResponse
                {
                    Content = content,
                    ContentType = template.ContentType,
                    StatusCode = template.StatusCode,
                    Headers = template.Headers,
                    WasGenerated = true
                };

                _cache.Set(fingerprint, requestContext.Path, response);

                _logger.LogInformation("Holodeck generated {Format} response for {Path} ({Length} chars)",
                    template.ResponseHints?.ResponseFormat ?? "unknown", requestContext.Path, content.Length);

                return response;
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                _logger.LogWarning("Holodeck LLM timeout for {Path}, falling back to static", requestContext.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Holodeck LLM failed for {Path}, falling back to static", requestContext.Path);
            }
        }

        return BuildStaticFallback(template, canary);
    }

    private static HolodeckResponse BuildStaticFallback(PackResponseTemplate template, string? canary)
    {
        var body = template.Body;
        if (canary != null)
        {
            body = body.Replace("{{nonce}}", canary)
                       .Replace("{{api_key}}", canary)
                       .Replace("{{token}}", canary);
        }

        return new HolodeckResponse
        {
            Content = body,
            ContentType = template.ContentType,
            StatusCode = template.StatusCode,
            Headers = template.Headers,
            WasGenerated = false
        };
    }
}
