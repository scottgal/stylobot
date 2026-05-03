using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Mostlylucid.Common.Telemetry;

/// <summary>
///     Extension methods for registering telemetry services
/// </summary>
public static class TelemetryExtensions
{
    /// <summary>
    ///     Adds telemetry options to the service collection
    /// </summary>
    public static IServiceCollection AddMostlylucidTelemetry(
        this IServiceCollection services,
        Action<TelemetryOptions>? configure = null)
    {
        services.Configure<TelemetryOptions>(options => { configure?.Invoke(options); });

        return services;
    }

    /// <summary>
    ///     Registers a TelemetryActivitySource as a singleton
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="name">The name of the activity source</param>
    /// <param name="version">The version of the activity source</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddActivitySource(
        this IServiceCollection services,
        string name,
        string? version = null)
    {
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetService<IOptions<TelemetryOptions>>();
            return new TelemetryActivitySource(name, version, options?.Value);
        });

        return services;
    }

    /// <summary>
    ///     Gets all Mostlylucid activity source names for OpenTelemetry configuration
    /// </summary>
    public static string[] GetMostlylucidActivitySourceNames()
    {
        return
        [
            ActivitySources.BotDetection,
            ActivitySources.GeoDetection,
            ActivitySources.LlmAltText,
            ActivitySources.LlmPiiRedactor,
            ActivitySources.LlmContentModeration,
            ActivitySources.LlmAccessibilityAuditor,
            ActivitySources.LlmSeoMetadata,
            ActivitySources.LlmLogSummarizer,
            ActivitySources.RagLlmSearch,
            ActivitySources.LlmI18nAssistant,
            ActivitySources.LlmSlideTranslator
        ];
    }
}

/// <summary>
///     Standard activity source names for all Mostlylucid packages
/// </summary>
public static class ActivitySources
{
    public const string BotDetection = "Mostlylucid.BotDetection";
    public const string GeoDetection = "Mostlylucid.GeoDetection";
    public const string LlmAltText = "Mostlylucid.LlmAltText";
    public const string LlmPiiRedactor = "Mostlylucid.LlmPiiRedactor";
    public const string LlmContentModeration = "Mostlylucid.LLMContentModeration";
    public const string LlmAccessibilityAuditor = "Mostlylucid.LlmAccessibilityAuditor";
    public const string LlmSeoMetadata = "Mostlylucid.LlmSeoMetadata";
    public const string LlmLogSummarizer = "Mostlylucid.LlmLogSummarizer";
    public const string RagLlmSearch = "Mostlylucid.RagLlmSearch";
    public const string LlmI18nAssistant = "Mostlylucid.LlmI18nAssistant";
    public const string LlmSlideTranslator = "Mostlylucid.LlmSlideTranslator";
}