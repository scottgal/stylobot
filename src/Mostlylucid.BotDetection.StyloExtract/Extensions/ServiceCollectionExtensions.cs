using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.StyloExtract.Actions;
using Mostlylucid.BotDetection.StyloExtract.ContentCache;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Extensions;

/// <summary>
/// DI registration for the StyloExtract action policies.
/// Call this after <c>AddStyloExtract()</c> and <c>AddBotDetection()</c> / <c>AddStyloBot()</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the StyloExtract action policies with StyloBot's <see cref="IActionPolicyRegistry"/>:
    /// <list type="bullet">
    ///   <item><c>content-cache-search</c> - short-circuits a bounded cached HTML response for search engines</item>
    ///   <item><c>extract-markdown-cache-ai</c> - serves cached HTML→Markdown to AI scrapers only</item>
    ///   <item><c>extract-headers</c> - adds X-StyloExtract-* response headers</item>
    ///   <item><c>extract-sidecar</c> - adds Link alternate header</item>
    ///   <item><c>extract-passthrough</c> - explicit no-op</item>
    /// </list>
    /// Options for each policy are read from named <see cref="StyloExtractActionOptions"/> instances
    /// (keyed by policy name: <c>content-cache-search</c>, <c>extract-markdown-cache-ai</c>, etc.). Configure them via
    /// <c>services.Configure&lt;StyloExtractActionOptions&gt;("content-cache-search", o => ...)</c>.
    /// </summary>
    public static IServiceCollection AddStyloExtractActionPolicies(this IServiceCollection services)
    {
        // The two visible content-cache policies (spec): content-cache-search (HTML for search engines)
        // and extract-markdown-cache-ai (Markdown for AI scrapers). They share ONE process-local LFU store
        // (MarkdownResponseCache) — keys carry policy variant + representation, so the two can never
        // cross-serve each other's payloads.
        services.AddSingleton<IActionPolicy, ContentCacheSearchActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractMarkdownCacheAiActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractHeadersActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractSidecarActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractPassthroughActionPolicy>();
        services.TryAddSingleton<CacheControlWriter>();
        services.TryAddSingleton<ResponseBodyCapture>();
        services.TryAddSingleton<CacheKeyBuilder>();
        services.TryAddSingleton<CacheabilityEvaluator>();
        services.TryAddSingleton<IContentCacheTelemetry, ContentCacheTelemetry>();
        services.TryAddSingleton<MarkdownResponseCache>(sp =>
        {
            var factory = sp.GetRequiredService<IOptionsFactory<StyloExtractActionOptions>>();
            return new MarkdownResponseCache(factory.Create("content-cache-search").TransformedContentCache);
        });

        // Register named options for each policy AND bind them to configuration sections.
        // Without BindConfiguration the per-policy Profile / Cache / Sidecar fields would
        // always return defaults regardless of what appsettings says, contradicting the
        // README and option docs.
        //
        // Config layout:
        //   StyloExtract:
        //     Actions:
        //       content-cache-search:
        //         TransformedContentCache: { Enabled, MaxEntries, ..., AllowedQueryKeys: ["q", "page"] }
        //       extract-markdown-cache-ai:
        //         Profile: RagFull
        //         Cache: { Mode: Override, MaxAge: 86400, VaryByBotType: true }
        //       extract-sidecar:
        //         SidecarRouteTemplate: "/{path}.md"
        //
        // BindConfiguration uses the .NET 9+ source-generated configuration binder when
        // the consumer project enables it (EnableConfigurationBindingGenerator). For
        // non-AOT consumers it falls back to reflection-based binding. The pack itself
        // sets IsAotCompatible=true; AOT consumers must opt into the source-gen binder.
        services.AddOptions<StyloExtractActionOptions>("content-cache-search")
            .BindConfiguration("StyloExtract:Actions:content-cache-search");
        services.AddOptions<StyloExtractActionOptions>("extract-markdown-cache-ai")
            .BindConfiguration("StyloExtract:Actions:extract-markdown-cache-ai");
        services.AddOptions<StyloExtractActionOptions>("extract-headers")
            .BindConfiguration("StyloExtract:Actions:extract-headers");
        services.AddOptions<StyloExtractActionOptions>("extract-sidecar")
            .BindConfiguration("StyloExtract:Actions:extract-sidecar");
        services.AddOptions<StyloExtractActionOptions>("extract-passthrough")
            .BindConfiguration("StyloExtract:Actions:extract-passthrough");

        return services;
    }
}
