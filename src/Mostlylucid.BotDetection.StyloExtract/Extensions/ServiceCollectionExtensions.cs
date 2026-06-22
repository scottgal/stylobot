using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.StyloExtract.Actions;
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
    /// Registers the four StyloExtract action policies with StyloBot's <see cref="IActionPolicyRegistry"/>:
    /// <list type="bullet">
    ///   <item><c>extract-markdown</c> - replaces HTML body with Markdown via stream interception</item>
    ///   <item><c>extract-headers</c> - adds X-StyloExtract-* response headers</item>
    ///   <item><c>extract-sidecar</c> - adds Link alternate header</item>
    ///   <item><c>extract-passthrough</c> - explicit no-op</item>
    /// </list>
    /// Options for each policy are read from named <see cref="StyloExtractActionOptions"/> instances
    /// (keyed by policy name: <c>extract-markdown</c>, etc.). Configure them via
    /// <c>services.Configure&lt;StyloExtractActionOptions&gt;("extract-markdown", o => ...)</c>.
    /// </summary>
    public static IServiceCollection AddStyloExtractActionPolicies(this IServiceCollection services)
    {
        services.AddSingleton<IActionPolicy, ExtractMarkdownActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractHeadersActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractSidecarActionPolicy>();
        services.AddSingleton<IActionPolicy, ExtractPassthroughActionPolicy>();
        services.TryAddSingleton<CacheControlWriter>();
        services.TryAddSingleton<ResponseBodyCapture>();

        // Register named options for each policy with defaults.
        // Operators override via services.Configure<StyloExtractActionOptions>(policyName, o => ...).
        services.AddOptions<StyloExtractActionOptions>("extract-markdown");
        services.AddOptions<StyloExtractActionOptions>("extract-headers");
        services.AddOptions<StyloExtractActionOptions>("extract-sidecar");
        services.AddOptions<StyloExtractActionOptions>("extract-passthrough");

        return services;
    }
}
