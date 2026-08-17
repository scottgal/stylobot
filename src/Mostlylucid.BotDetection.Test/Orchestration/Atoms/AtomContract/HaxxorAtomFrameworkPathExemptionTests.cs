using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Regression for operator P0 2026-08-17: a real browser hitting <c>/wp-login.php</c> on a
///     stock gateway with the WordPress site profile set was classified <c>BotType.Scraper</c>
///     and throttled ~28s. Root cause: <see cref="HaxxorAtom"/>'s <c>path_probes</c> category
///     ("someone is probing for a stack that might not be here") fires on the mere existence of
///     a request to <c>/wp-login.php</c>/<c>/wp-admin*</c>, unconditionally -- it never consulted
///     the site profile the honeypot tagger already respects for exactly this reason. These tests
///     pin: (1) the pre-fix behaviour still exists when no exempt store is wired (FOSS default,
///     back-compat), (2) the framework-path exemption suppresses ONLY the path-probe signal, and
///     (3) a real attack payload on the SAME path is never suppressed -- the login page stays
///     defended against actual brute-force / injection, per the operator's explicit requirement.
/// </summary>
public class HaxxorAtomFrameworkPathExemptionTests
{
    private sealed class StubExemptStore : IHoneypotExemptStore
    {
        private readonly bool _exempt;
        public StubExemptStore(bool exempt) => _exempt = exempt;
        public IReadOnlyCollection<string> GetExemptPaths() => [];
        public bool IsExempt(string normalizedPath, HttpContext? context = null) => _exempt;
    }

    /// <summary>
    ///     HaxxorAtom's path/regex category lists come from the detector manifest via
    ///     IDetectorConfigProvider.GetDefaults(...).Parameters, not a hardcoded list --
    ///     AtomContractTestStubs.StubDetectorConfigProvider returns an empty Parameters dict
    ///     (it's shared across unrelated atom tests), so this seeds just the two categories
    ///     these tests exercise, mirroring haxxor.detector.yaml's real path_probes/sqli_patterns.
    /// </summary>
    private sealed class HaxxorConfigProvider : IDetectorConfigProvider
    {
        public DetectorManifest? GetManifest(string detectorName) => null;
        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests() => new Dictionary<string, DetectorManifest>();
        public void InvalidateCache(string? detectorName = null) { }
        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;
        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(defaultValue);

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Parameters = new Dictionary<string, object>
            {
                ["path_probes"] = new object[] { "/wp-admin*", "/wp-login.php" },
                ["sqli_patterns"] = new object[]
                {
                    "(?i)('\\s*(or|and)\\s+['\"]?\\d.*?=|sleep\\s*\\(\\d|waitfor\\s+delay)"
                }
            }
        };
    }

    private static HaxxorAtom NewAtom(HttpContext http, IHoneypotExemptStore? exemptStore = null) => new(
        NullLogger<HaxxorAtom>.Instance,
        new HaxxorConfigProvider(),
        new StaticHttpContextAccessor(http),
        exemptStore);

    private static HttpContext ContextFor(string path, string queryString = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (!string.IsNullOrEmpty(queryString))
            http.Request.QueryString = new QueryString(queryString);
        return http;
    }

    [Fact]
    public async Task Wp_login_flags_Scraper_when_no_exempt_store_is_wired()
    {
        // FOSS default / back-compat: without IHoneypotExemptStore in DI, behaviour is unchanged
        // from before this fix.
        var atom = NewAtom(ContextFor("/wp-login.php"));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        var hit = contributions.Should().ContainSingle().Subject;
        hit.BotType.Should().Be(nameof(BotType.Scraper));
    }

    [Fact]
    public async Task Wp_login_is_not_flagged_when_the_site_profile_exempts_it()
    {
        var atom = NewAtom(ContextFor("/wp-login.php"), new StubExemptStore(exempt: true));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        contributions.Should().BeEmpty(
            "a real browser visiting the normal login page of a site whose profile declares " +
            "/wp-login.php a legitimate framework path must not be classified Scraper");
    }

    [Fact]
    public async Task Wp_login_is_still_flagged_when_the_exempt_store_says_no()
    {
        var atom = NewAtom(ContextFor("/wp-login.php"), new StubExemptStore(exempt: false));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        contributions.Should().ContainSingle(
            "a site NOT running the matching profile still gets the normal path-probe signal");
    }

    [Fact]
    public async Task Sqli_payload_on_wp_login_still_flags_MaliciousBot_even_when_path_probe_exempt()
    {
        // The exemption covers ONLY "this URL existing is suspicious". A real attack payload
        // against the same, legitimately-served path must still be caught -- the operator's
        // explicit requirement that a login page stays defended against actual attacks.
        var atom = NewAtom(
            ContextFor("/wp-login.php", "?id=1' OR '1'='1"),
            new StubExemptStore(exempt: true));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        var hit = contributions.Should().ContainSingle().Subject;
        hit.BotType.Should().Be(nameof(BotType.MaliciousBot),
            "a SQLi payload must still be caught on an exempted framework path -- the exemption " +
            "only suppresses the path-probe signal, never actual attack-payload detection");
    }
}
