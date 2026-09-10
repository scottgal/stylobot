using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

/// <summary>
///     The per-site bot/human cut, one level below the global one-key rule.
///     <para>
///     A site profile may override the cut, INCLUDING by widening it — that is a deliberate scoped
///     operator decision and is not clamped. What must never happen is a silent override, so these
///     tests pin the two halves of that contract: a legal divergence is APPLIED, a malformed value is
///     IGNORED, and BOTH are announced at boot with the direction stated.
///     </para>
///     <para>
///     The defect these exist for: the resolver assigned the overlay value with no range check and no
///     announcement, so a profile setting 0.40 against a global 0.70 moved this site's CLASSIFICATION
///     (not merely its enforcement) in complete silence.
///     </para>
/// </summary>
public sealed class PerSiteFloorOverrideTests
{
    private const double GlobalBotFloor = 0.70;

    // ------------------------------------------------------------------
    // The guard: what the resolver does with the value
    // ------------------------------------------------------------------

    [Fact]
    public void A_widening_override_is_APPLIED_and_not_clamped()
    {
        // Widening is legitimate. If this ever clamps to the global, a deliberate
        // operator decision is being silently overridden -- the same class of defect
        // this guard exists to close.
        var resolver = BuildResolver(("wide.example.com", ProfileWithFloor("wide", 0.40)));

        var effective = resolver.ResolveThresholds(ContextFor("wide.example.com"));

        effective.BotFloor.Should().Be(0.40, "a scoped override is a decision, not a mistake");
        effective.BotThreshold.Should().Be(0.40, "the record mirrors the floor -- one number per site");
    }

    [Fact]
    public void A_narrowing_override_is_also_applied()
    {
        var resolver = BuildResolver(("tight.example.com", ProfileWithFloor("tight", 0.95)));

        var effective = resolver.ResolveThresholds(ContextFor("tight.example.com"));

        effective.BotFloor.Should().Be(0.95);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void An_out_of_range_override_is_IGNORED_and_the_inherited_floor_stands(double nonsense)
    {
        // Not a probability. It must never reach the effective thresholds, so the
        // global floor stands for that site rather than being replaced by garbage.
        var resolver = BuildResolver(("broken.example.com", ProfileWithFloor("broken", nonsense)));

        var effective = resolver.ResolveThresholds(ContextFor("broken.example.com"));

        effective.BotFloor.Should().Be(GlobalBotFloor,
            $"a cut of {nonsense} is not a probability and must not take effect");
    }

    [Fact]
    public void An_in_range_override_still_applies_after_a_rejected_sibling_field()
    {
        // The guard is per-field: rejecting a nonsense HumanCeiling must not
        // discard a perfectly good BotFloor on the same profile.
        var profile = new SiteProfile
        {
            Id = "partial",
            Thresholds = new SiteThresholdOverrides { BotFloor = 0.55, HumanCeiling = 9.0 }
        };
        var resolver = BuildResolver(("partial.example.com", profile));

        var effective = resolver.ResolveThresholds(ContextFor("partial.example.com"));

        effective.BotFloor.Should().Be(0.55, "the legal field is unaffected by the rejected one");
        effective.HumanCeiling.Should().NotBe(9.0, "the nonsense ceiling is rejected");
    }

    // ------------------------------------------------------------------
    // The announcement: what the operator is told at boot
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_widening_override_is_announced_and_the_direction_is_stated()
    {
        var logger = await StartWithAsync(("wide", ProfileWithFloor("wide", 0.40)));

        var warning = logger.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("wide", "the profile is named");
        warning.Should().Contain("0.40", "the value is stated");
        warning.Should().Contain("0.70", "the global it diverges from is stated");
        warning.Should().Contain("WIDENS", "a count tells the operator nothing; the direction tells them what they did");
    }

    [Fact]
    public async Task A_narrowing_override_is_announced_with_the_opposite_direction()
    {
        var logger = await StartWithAsync(("tight", ProfileWithFloor("tight", 0.95)));

        var warning = logger.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("NARROWS");
        warning.Should().NotContain("WIDENS");
    }

    [Fact]
    public async Task An_override_equal_to_the_global_is_silent()
    {
        // Agreement is not news. A warning here would train operators to ignore the report.
        var logger = await StartWithAsync(("same", ProfileWithFloor("same", GlobalBotFloor)));

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_with_no_floor_override_is_silent()
    {
        var profile = new SiteProfile
        {
            Id = "ceiling-only",
            Thresholds = new SiteThresholdOverrides { HumanCeiling = 0.2 }
        };
        var logger = await StartWithAsync(("ceiling-only", profile));

        logger.Warnings.Should().BeEmpty("nothing about the bot/human cut was overridden");
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(1.5)]
    public async Task An_out_of_range_override_is_announced_as_rejected(double nonsense)
    {
        // The value does nothing. Without this line the operator sees their number
        // vanish with no explanation -- a config knob that appears to work and does not.
        var logger = await StartWithAsync(("broken", ProfileWithFloor("broken", nonsense)));

        var warning = logger.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("broken");
        warning.Should().Contain("IGNORED", "the operator must learn the value did not take effect");
    }

    // ------------------------------------------------------------------

    private static SiteProfile ProfileWithFloor(string id, double floor) => new()
    {
        Id = id,
        Thresholds = new SiteThresholdOverrides { BotFloor = floor }
    };

    private static async Task<CapturingLogger> StartWithAsync(params (string Id, SiteProfile Profile)[] profiles)
    {
        var options = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = GlobalBotFloor }
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISiteProfileCatalog>(new FakeCatalog(profiles));
        var logger = new CapturingLogger();

        await new BotThresholdDivergenceWarningService(
                Options.Create(options), logger, services.BuildServiceProvider())
            .StartAsync(CancellationToken.None);

        return logger;
    }

    private static EffectivePolicyResolver BuildResolver(params (string Host, SiteProfile Profile)[] profiles)
    {
        var catalog = new FakeCatalog(profiles);
        var map = new SiteMapOptions
        {
            DefaultProfile = "",
            Domains = profiles.Select(p => new SiteMapRule { Host = p.Host, Profile = p.Profile.Id }).ToList()
        };
        var normalizer = new DomainNormalizer(
            Options.Create(new DomainNormalizerOptions()),
            PublicSuffixList.LoadEmbedded());
        var siteResolver = new SiteProfileResolver(
            catalog, Options.Create(map), normalizer, NullLogger<SiteProfileResolver>.Instance);
        var opts = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = GlobalBotFloor }
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
        public FakeCatalog(IEnumerable<(string Id, SiteProfile Profile)> profiles) =>
            Profiles = profiles.ToDictionary(p => p.Profile.Id, p => p.Profile, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, SiteProfile> Profiles { get; }

        public SiteProfile? Get(string id) => Profiles.TryGetValue(id, out var p) ? p : null;
    }

    private sealed class TestMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }

    private sealed class CapturingLogger : ILogger<BotThresholdDivergenceWarningService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Warnings =>
            _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }
}
