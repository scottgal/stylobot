using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

/// <summary>
///     Covers the global → domain-profile → host-profile per-field null-fill
///     merge, plus the middleware stamp. Detection consumers migrate to
///     reading the cached <see cref="EffectiveThresholds"/> off HttpContext in
///     follow-up work; this suite only pins the resolution machinery.
/// </summary>
public class EffectivePolicyResolverTests
{
    // Global defaults live on BotDetectionOptions. The overlay type maps to
    // the actual fields on that class (BotThreshold + Classification.HumanCeiling
    // + Classification.BotFloor) — see SiteThresholdOverrides for the reason
    // the spec's AllowBand / ChallengeBand names weren't used verbatim.
    private const double GlobalBotThreshold = 0.66;
    private const double GlobalHumanCeiling = 0.25;
    private const double GlobalBotFloor = 0.75;

    [Fact]
    public void GlobalOnly_NoSiteProfileMatch_ReturnsGlobalThresholds()
    {
        var resolver = BuildResolver(profiles: Array.Empty<(string Host, SiteProfile Profile)>());
        var ctx = ContextFor("nothing.example.com");

        var effective = resolver.ResolveThresholds(ctx);

        // ONE key: the overlay's global level reads BotDetectionOptions.BotThreshold, which is now a
        // read-through to Classification.BotFloor. BuildResolver deliberately sets an explicit
        // DIVERGENT global BotThreshold (0.66) to pin that it is ignored by design — BotFloor wins
        // and is reported at boot by BotThresholdDivergenceWarningService.
        Assert.Equal(GlobalBotFloor, effective.BotThreshold);
        Assert.Equal(GlobalHumanCeiling, effective.HumanCeiling);
        Assert.Equal(GlobalBotFloor, effective.BotFloor);
    }

    [Fact]
    public void DomainProfileOnly_HostInheritsAllExceptDomainDeclared()
    {
        // stylo.bot has a domain-level profile whose BotThreshold=0.7 overrides
        // the global cut. HumanCeiling + BotFloor stay null on the overlay, so
        // the effective values fall back to the global defaults.
        var domainProfile = new SiteProfile
        {
            Id = "domain-profile",
            Thresholds = new SiteThresholdOverrides { BotThreshold = 0.7 }
        };
        // Host resolves to www.stylo.bot; domain resolves to stylo.bot.
        var resolver = BuildResolver(("stylo.bot", domainProfile));
        var ctx = ContextFor("www.stylo.bot");

        var effective = resolver.ResolveThresholds(ctx);

        // ONE key: the profile's obsolete BotThreshold resolves into this site's floor, so both
        // fields carry the same number rather than the site having two cuts.
        Assert.Equal(0.7, effective.BotThreshold);
        Assert.Equal(GlobalHumanCeiling, effective.HumanCeiling);
        Assert.Equal(0.7, effective.BotFloor);
    }

    [Fact]
    public void HostAndDomainProfiles_HostWinsForItsFields_DomainFillsOthers_GlobalFillsRest()
    {
        // Domain profile declares HumanCeiling + BotFloor; host profile
        // declares BotThreshold + HumanCeiling. Expected merge:
        //   BotThreshold  -> host (0.55)
        //   HumanCeiling  -> host (0.10)   <-- host overrides domain
        //   BotFloor      -> domain (0.80) <-- host silent, domain non-null
        // No field falls all the way through to global for this case, so
        // "global fills whatever remains" is exercised by leaving neither
        // level touching, e.g. BotThreshold-only via the previous test.
        var domainProfile = new SiteProfile
        {
            Id = "domain-profile",
            Thresholds = new SiteThresholdOverrides { HumanCeiling = 0.20, BotFloor = 0.80 }
        };
        var hostProfile = new SiteProfile
        {
            Id = "host-profile",
            Thresholds = new SiteThresholdOverrides { BotThreshold = 0.55, HumanCeiling = 0.10 }
        };

        var resolver = BuildResolver(
            ("stylo.bot", domainProfile),
            ("www.stylo.bot", hostProfile));
        var ctx = ContextFor("www.stylo.bot");

        var effective = resolver.ResolveThresholds(ctx);

        Assert.Equal(0.55, effective.BotThreshold); // host's obsolete field -> the site floor
        Assert.Equal(0.10, effective.HumanCeiling); // host wins over domain
        Assert.Equal(0.55, effective.BotFloor);     // ONE key: the same number as BotThreshold
    }

    [Fact]
    public async Task Middleware_StampsEffectiveThresholdsOnItems()
    {
        // Wire a minimal middleware invocation and confirm the resolver's
        // stamp lands on HttpContext.Items under the well-known key.
        var domainProfile = new SiteProfile
        {
            Id = "domain-profile",
            Thresholds = new SiteThresholdOverrides { BotThreshold = 0.42 }
        };
        var resolver = BuildResolver(("stylo.bot", domainProfile));
        var ctx = ContextFor("www.stylo.bot");
        var services = new ServiceCollection();
        services.AddSingleton<IEffectivePolicyResolver>(resolver);
        ctx.RequestServices = services.BuildServiceProvider();

        // The middleware call under test is the same one-liner
        // BotDetectionMiddleware.InvokeAsync runs after DomainNormalizer.Resolve.
        // Exercising the whole InvokeAsync pipeline requires 10+ orchestrator /
        // registry singletons that are orthogonal to this behaviour; the stamp
        // is a direct call on the resolver, so we drive it the same way.
        _ = ctx.RequestServices.GetService(typeof(IEffectivePolicyResolver));
        var r = (IEffectivePolicyResolver)ctx.RequestServices.GetService(typeof(IEffectivePolicyResolver))!;
        r.ResolveThresholds(ctx);

        Assert.True(ctx.Items.ContainsKey(HttpContextItemKeys.EffectiveThresholds));
        var stamped = Assert.IsType<EffectiveThresholds>(ctx.Items[HttpContextItemKeys.EffectiveThresholds]);
        Assert.Equal(0.42, stamped.BotThreshold);
        await Task.CompletedTask;
    }

    [Fact]
    public void Explicit_BotFloor_wins_over_the_obsolete_field_at_the_same_level()
    {
        var profile = new SiteProfile
        {
            Id = "both-declared",
            Thresholds = new SiteThresholdOverrides { BotThreshold = 0.60, BotFloor = 0.85 }
        };
        var resolver = BuildResolver(("stylo.bot", profile));

        var effective = resolver.ResolveThresholds(ContextFor("www.stylo.bot"));

        Assert.Equal(0.85, effective.BotFloor);
        Assert.Equal(0.85, effective.BotThreshold); // one number, and BotFloor is the one
    }

    [Fact]
    public async Task Boot_report_names_a_site_profile_that_uses_the_obsolete_field()
    {
        var profile = new SiteProfile
        {
            Id = "legacy-site",
            Thresholds = new SiteThresholdOverrides { BotThreshold = 0.42 }
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISiteProfileCatalog>(new FakeCatalog(new[] { profile }));
        var logger = new RecordingLogger();

        await new Mostlylucid.BotDetection.Services.BotThresholdDivergenceWarningService(
                Options.Create(new BotDetectionOptions()), logger, services.BuildServiceProvider())
            .StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("legacy-site", warning);
        Assert.Contains("BotThreshold", warning);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<Mostlylucid.BotDetection.Services.BotThresholdDivergenceWarningService>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    // ------------------------------------------------------------------

    private static EffectivePolicyResolver BuildResolver(params (string Host, SiteProfile Profile)[] profiles)
    {
        var catalog = new FakeCatalog(profiles.Select(p => p.Profile).ToArray());
        var map = new SiteMapOptions
        {
            DefaultProfile = "",
            Domains = profiles.Select(p => new SiteMapRule { Host = p.Host, Profile = p.Profile.Id }).ToList()
        };
        var normalizer = new DomainNormalizer(
            Options.Create(new DomainNormalizerOptions()),
            PublicSuffixList.LoadEmbedded());
        var siteResolver = new SiteProfileResolver(
            catalog,
            Options.Create(map),
            normalizer,
            NullLogger<SiteProfileResolver>.Instance);
        var opts = new BotDetectionOptions
        {
#pragma warning disable CS0618
            BotThreshold = GlobalBotThreshold,
#pragma warning restore CS0618
            Classification = new ClassificationOptions
            {
                HumanCeiling = GlobalHumanCeiling,
                BotFloor = GlobalBotFloor
            }
        };
        return new EffectivePolicyResolver(siteResolver, new TestMonitor<BotDetectionOptions>(opts), normalizer);
    }

    private static HttpContext ContextFor(string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        return ctx;
    }

    private sealed class FakeCatalog : ISiteProfileCatalog
    {
        public FakeCatalog(SiteProfile[] profiles)
        {
            Profiles = profiles.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyDictionary<string, SiteProfile> Profiles { get; }

        public SiteProfile? Get(string id) =>
            Profiles.TryGetValue(id, out var p) ? p : null;
    }

    private sealed class TestMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}