using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.StyloWall.Abstractions;
using Mostlylucid.StyloWall.Optimization.Markdown;

namespace Mostlylucid.StyloWall.Optimization.Extensions;

public static class StyloWallOptimizationServiceCollectionExtensions
{
    public static IServiceCollection AddStyloWallOptimization(
        this IServiceCollection services,
        Action<MarkdownEmitterOptions>? configureMarkdown = null)
    {
        services.AddOptions<MarkdownEmitterOptions>().BindConfiguration(MarkdownEmitterOptions.SectionName);
        if (configureMarkdown is not null) services.Configure(configureMarkdown);

        services.AddSingleton<IResponseTransform, HtmlToMarkdownTransform>();
        return services;
    }

    public static IServiceCollection AddStyloWallOptimization(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<MarkdownEmitterOptions>? configureMarkdown = null)
    {
        services.AddOptions<MarkdownEmitterOptions>()
            .Bind(configuration.GetSection(MarkdownEmitterOptions.SectionName));
        if (configureMarkdown is not null) services.Configure(configureMarkdown);

        services.AddSingleton<IResponseTransform, HtmlToMarkdownTransform>();
        return services;
    }
}
