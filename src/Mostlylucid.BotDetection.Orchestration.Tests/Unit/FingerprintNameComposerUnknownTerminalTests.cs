using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Pins the "Unknown" terminal branch of FingerprintNameComposer. The composer must
///     never emit "Unknown 00000000" -- the all-zero placeholder is a lying name that
///     pretends a fingerprint exists when it does not. When fingerprintId is missing,
///     fall back to ASN / country / bare "Unknown" in that order.
///
///     Bug repro (2026-06-23, prod): visitor list showed "Unknown 000000…" rows because
///     the prior implementation hardcoded "00000000" as the no-id terminal suffix. Fix
///     uses real-signal discriminators that are present on the first request (network
///     contributors run before UA parsing).
/// </summary>
public class FingerprintNameComposerUnknownTerminalTests
{
    [Fact]
    public void No_UA_no_fingerprintId_no_signals_yields_bare_Unknown()
    {
        var signals = new Dictionary<string, object>();

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Unknown", name);
        Assert.DoesNotContain("00000000", name);
        Assert.DoesNotContain("000000", name);
    }

    [Fact]
    public void No_UA_short_fingerprintId_no_signals_yields_bare_Unknown()
    {
        // Short fingerprintId (< 8 chars) used to fall through to "Unknown 00000000".
        // Now it falls through to the same network-signal cascade as the null case.
        var signals = new Dictionary<string, object>();

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abc", userAgent: null);

        Assert.Equal("Unknown", name);
    }

    [Fact]
    public void No_UA_no_fingerprintId_with_asn_yields_Unknown_AS_prefix()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpAsn] = "15169"
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Unknown AS15169", name);
    }

    [Fact]
    public void No_UA_no_fingerprintId_with_country_only_yields_Unknown_country()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.GeoCountryCode] = "US"
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Unknown US", name);
    }

    [Fact]
    public void Asn_wins_over_country_when_both_present()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpAsn] = "15169",
            [SignalKeys.GeoCountryCode] = "US"
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Unknown AS15169", name);
    }

    [Fact]
    public void Long_fingerprintId_still_uses_eight_char_hex_prefix()
    {
        // The prior contract (Unknown <8-hex>) is preserved when fingerprintId is real
        // and long enough. Only the all-zero fallback is removed.
        var signals = new Dictionary<string, object>();

        var name = FingerprintNameComposer.Compose(
            signals, fingerprintId: "abcdef1234567890", userAgent: null);

        Assert.Equal("Unknown abcdef12", name);
    }

    [Fact]
    public void Priority3_with_full_signals_yields_os_family_version()
    {
        // Single source of truth for the displayed name. When UA gives us OS +
        // family + version, the name carries all three so the row uniquifies
        // ("Win Chrome 146" not bare "Chrome").
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentFamily]        = "Chrome",
            [SignalKeys.UserAgentFamilyVersion] = "146.0.6261.94",
            [SignalKeys.UserAgentOs]            = "Windows",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Win Chrome 146", name);
    }

    [Fact]
    public void Priority3_mac_safari_short_form()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentFamily]        = "Safari",
            [SignalKeys.UserAgentFamilyVersion] = "17.5",
            [SignalKeys.UserAgentOs]            = "macOS",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Mac Safari 17", name);
    }

    [Fact]
    public void Priority3_ios_passes_through_unchanged()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentFamily]        = "Mobile Safari",
            [SignalKeys.UserAgentFamilyVersion] = "13",
            [SignalKeys.UserAgentOs]            = "iOS",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("iOS Mobile Safari 13", name);
    }

    [Fact]
    public void Priority3_falls_back_when_version_missing()
    {
        // Version not in signals and UA can't supply it -> name drops version,
        // keeps OS+family so it's still informative.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentFamily] = "Chrome",
            [SignalKeys.UserAgentOs]     = "Windows",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Win Chrome", name);
    }

    [Fact]
    public void Priority3_falls_back_when_os_missing()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentFamily]        = "Chrome",
            [SignalKeys.UserAgentFamilyVersion] = "146",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Chrome 146", name);
    }

    [Fact]
    public void Priority3_parses_version_from_raw_UA_when_signals_missing()
    {
        // Cold path: signals dict doesn't yet carry family/version because the
        // matcher fires before UA contributor. UA string is enough -- composer
        // self-rescues via UserAgentParser.Parse.
        var signals = new Dictionary<string, object>();
        var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                 "(KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: ua);

        Assert.Equal("Win Chrome 146", name);
    }

    // ---------------- verdict-honest bot branch (Priority 1.5) ----------------

    [Fact]
    public void High_bot_probability_with_no_claim_yields_Unknown_Bot_family()
    {
        // Repro from prod fingerprint fA_cI4MGbTxkwr9-4UsUEg: archetype matcher
        // landed on chrome-xhr (human-browser kind) but the bot classifier said
        // 90% Scraper. Composer must NOT trust the human archetype name here;
        // the name has to reflect the verdict.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.9,
            [SignalKeys.IdentityArchetypeName]        = "chrome-xhr",
            [SignalKeys.IdentityArchetypeKind]        = "human-browser",
            [SignalKeys.UserAgentFamily]              = "Chrome",
            [SignalKeys.UserAgentFamilyVersion]       = "149.0.0.0",
            [SignalKeys.UserAgentOs]                  = "Windows",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Unknown Bot Win Chrome 149", name);
    }

    [Fact]
    public void Bot_probability_below_threshold_keeps_human_archetype_name()
    {
        // Boundary check: under the threshold, the matcher's human-browser
        // archetype name is allowed to win. A 40% bot probability is below the
        // bot-naming line so a real human visitor still shows "chrome-xhr".
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.4,
            [SignalKeys.IdentityArchetypeName]        = "chrome-xhr",
            [SignalKeys.IdentityArchetypeKind]        = "human-browser",
            [SignalKeys.UserAgentFamily]              = "Chrome",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("chrome-xhr", name);
    }

    [Fact]
    public void Bot_probability_with_no_UA_signals_yields_bare_Unknown_Bot()
    {
        // Bot verdict but UA contributor hasn't written family yet AND no UA
        // string was passed in -> emit bare "Unknown Bot" rather than fabricating
        // a family.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.9,
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Unknown Bot", name);
    }

    [Fact]
    public void Bot_probability_with_only_family_signal_yields_Unknown_Bot_family()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.75,
            [SignalKeys.UserAgentFamily]              = "Chrome",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.Equal("Unknown Bot Chrome", name);
    }

    [Fact]
    public void Bot_probability_self_rescues_family_from_UA_when_signals_missing()
    {
        // Matcher fires before the UA contributor: family signal is absent at
        // compose time. The composer parses the raw UA itself so a high-bot-
        // probability fingerprint still produces "Unknown Bot Win Chrome 149"
        // instead of bare "Unknown Bot".
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.9,
        };
        var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                 "(KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: ua);

        Assert.Equal("Unknown Bot Win Chrome 149", name);
    }

    [Fact]
    public void Claimed_bot_name_wins_over_bot_probability_branch()
    {
        // Priority 1 still fires for self-declared bots (Googlebot etc.) even
        // when bot probability is high. The Priority 1.5 branch is only for the
        // "bot verdict + no claim" case.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.95,
            [SignalKeys.UserAgent]                    = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abcdef12", userAgent: null);

        Assert.StartsWith("Googlebot", name);
    }

    [Fact]
    public void Unknown_Bot_name_is_recognised_as_fallback_for_hysteresis_override()
    {
        // IsFallback returns true for "Unknown ..." names (the existing rule).
        // That means once Priority 1 starts producing a real bot name -- e.g.,
        // the UA contributor catches up and ua.bot_name lands "AhrefsBot" -- the
        // Priority-1.5 placeholder is overridable by hysteresis. Anchor that
        // contract.
        Assert.True(FingerprintNameComposer.IsFallback("Unknown Bot Win Chrome 149"));
        Assert.True(FingerprintNameComposer.IsFallback("Unknown Bot Chrome"));
        Assert.True(FingerprintNameComposer.IsFallback("Unknown Bot"));
    }

    [Fact]
    public void Unknown_terminal_never_contains_zero_padded_suffix()
    {
        // Defensive: across every combination of (no fingerprintId, no UA, with/without
        // ASN, with/without country), the result must NEVER be the all-zero placeholder.
        var combos = new (string? asn, string? country)[]
        {
            (null, null),
            (null, "US"),
            ("15169", null),
            ("15169", "US"),
        };

        foreach (var (asn, country) in combos)
        {
            var signals = new Dictionary<string, object>();
            if (asn is not null) signals[SignalKeys.IpAsn] = asn;
            if (country is not null) signals[SignalKeys.GeoCountryCode] = country;

            var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

            Assert.False(
                name?.Contains("00000000") ?? false,
                $"combo asn={asn} country={country} produced \"{name}\" with zero-padded suffix");
        }
    }
}