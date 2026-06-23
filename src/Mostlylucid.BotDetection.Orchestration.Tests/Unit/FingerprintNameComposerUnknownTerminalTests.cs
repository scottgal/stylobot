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