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
    public void No_UA_with_cloud_provider_yields_Missing_UA_provider_not_opaque_Unknown()
    {
        // A no-UA hosting-provider scanner (Azure, etc.) must be named by WHAT it is, not an
        // opaque "Unknown <fp8>". Provider wins over the fingerprint-id / ASN terminals.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpProvider] = "Azure",
            [SignalKeys.IpAsn] = "8075",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "e91adac5526fc9c5", userAgent: null);

        Assert.Equal("Missing UA Azure", name);
    }

    [Fact]
    public void No_UA_with_asn_org_but_no_provider_yields_Missing_UA_asn_org()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpAsnOrg] = "MICROSOFT-CORP-MSN-AS-BLOCK",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Missing UA MICROSOFT-CORP-MSN-AS-BLOCK", name);
    }

    [Fact]
    public void Missing_UA_name_is_recognised_as_a_fallback_so_it_upgrades_when_a_UA_appears()
    {
        // "Missing UA Azure" is not a real name -- it must still be overwritten by a
        // UA-derived name if the actor later sends one (the wrote-once-and-never-again guard).
        Assert.True(FingerprintNameComposer.IsFallback("Missing UA Azure"));
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

    // REMOVED: the 6 "Win Chrome 146" / "Mac Safari 17" tests pinned the
    // {OS-short} {family} {major-version} shape from commit 760fd0e1. That
    // shape was reverted in 3ca80b92 ("re-apply T5") per the user directive:
    // "Mac Chrome 149 w/ uBlock -- multi-word descriptive synthetics, even if
    // accurate, fight the bot-name / browser-family / unknown trichotomy.
    // Drop. (Task #121 was the wrong direction.)"
    //
    // Priority 3 now returns the UA family UNCHANGED (plain "Chrome" / "Safari" /
    // "Firefox"). Uniqueness comes from the existing BuildDistinctiveModifier
    // path (ASN / country / IP /16) appended by FingerprintMatchContributor as
    // "{family} ({mod})" -- and ONLY on collision (when a different fingerprint
    // already owns the bare-family name). See spec at
    //   docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md
    // and FingerprintNameComposerContract.IsAllowedShape (which rejects names
    // with parens / slashes / " w/ " on the composer-output side; the matcher's
    // collision-suffix is the only sanctioned `(...)` form).

    // The matcher-side Priority 1.5 branch was removed -- verdict-honest naming
    // is now enforced at DetectionLedgerExtensions.BuildEvidenceFromLedger where
    // ua.bot_name (catalog claim) and archetype name+kind are all reliably present.
    // The matcher cannot make this call safely because the UA contributor runs
    // at priority 10 AFTER the matcher at priority 6 -- ua.bot_name is empty at
    // compose time, so "no catalog claim" cannot be distinguished from "catalog
    // entry not yet looked up." See FingerprintNameComposerVerdictHonestTests for
    // the new shape (lives next to the ledger extension that owns the override).

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