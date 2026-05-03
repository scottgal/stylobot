using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.StyloSpam.Core.Contributors;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStyloSpamScoring(
        this IServiceCollection services,
        Action<EmailScoringOptions>? configure = null)
    {
        services.AddOptions<EmailScoringOptions>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<EmailScoringEngine>();
        services.AddSingleton<IEmailScoreContributor, AuthenticationSignalsContributor>();
        services.AddSingleton<IEmailScoreContributor, UrlPatternContributor>();
        services.AddSingleton<IEmailScoreContributor, SpamPhraseContributor>();
        services.AddSingleton<IEmailScoreContributor, LocalLlmSemanticContributor>();
        services.AddSingleton<IEmailScoreContributor, AttachmentRiskContributor>();
        services.AddSingleton<IEmailScoreContributor, RecipientSpreadContributor>();
        services.AddSingleton<IEmailScoreContributor, OutgoingVelocityContributor>();

        return services;
    }
}
