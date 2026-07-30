using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Pins the terminal branch of FingerprintNameComposer under the operator directive
///     (2026-07-30): <b>"UNKNOWN IS NOT A VALID STATE"</b>. When Priority 1-3 produce no name
///     the composer must NEVER emit "Unknown" / "Unknown &lt;x&gt;" / "Unknown Bot" / a bare
///     opaque hash. It must instead synthesise <c>{what it is behaving like} · {who/where}</c>
///     from signals already on the blackboard (intent / attack-surface / bot-type +
///     org / ASN / country / self-declared domain), so every row is meaningful and the name
///     re-derives from current behaviour on each compose (updates as the fingerprint drifts).
///
///     Regression context: a Cortex-Xpanse scanner (UA "Hello from Palo Alto Networks, find
///     out more about our scans in https://docs-cortex.paloaltonetworks.com/...") showed as
///     "Unknown" on prod/staging because the arcjet catalog pattern for that vendor is the bare
///     literal "Expanse", which the "...Cortex-Xpanse..." UA does not contain. The fix is NOT a
///     new catalog pattern (whack-a-mole) but a generic never-Unknown synthesis.
/// </summary>
public class FingerprintNameComposerUnknownTerminalTests
{
    // The load-bearing invariant. Every name the composer can emit for an unrecognised actor
    // must be a real, non-empty label that never contains the word "Unknown".
    private static void AssertNeverUnknown(string? name, string because)
    {
        Assert.False(string.IsNullOrWhiteSpace(name), $"{because}: name was null/empty");
        Assert.DoesNotContain("Unknown", name!, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00000000", name!);
    }

    [Fact]
    public void No_UA_no_fingerprintId_no_signals_never_yields_Unknown()
    {
        var signals = new Dictionary<string, object>();

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        AssertNeverUnknown(name, "empty request");
        Assert.Equal("Unclassified Client", name);
    }

    [Fact]
    public void No_UA_short_fingerprintId_no_signals_never_yields_Unknown()
    {
        var signals = new Dictionary<string, object>();

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "abc", userAgent: null);

        AssertNeverUnknown(name, "short fingerprint id");
        Assert.Equal("Unclassified Client", name);
    }

    [Fact]
    public void Asn_only_names_by_network_identity_not_Unknown()
    {
        var signals = new Dictionary<string, object> { [SignalKeys.IpAsn] = "15169" };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        AssertNeverUnknown(name, "asn only");
        Assert.Equal("AS15169", name);
    }

    [Fact]
    public void Country_only_names_by_country_not_Unknown()
    {
        var signals = new Dictionary<string, object> { [SignalKeys.GeoCountryCode] = "US" };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        AssertNeverUnknown(name, "country only");
        Assert.Equal("US", name);
    }

    [Fact]
    public void No_UA_hosting_provider_names_by_provider_not_opaque_hash()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpProvider] = "Azure",
            [SignalKeys.IpAsn] = "8075",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: "e91adac5526fc9c5", userAgent: null);

        AssertNeverUnknown(name, "hosting provider");
        Assert.Equal("Azure", name);
    }

    [Fact]
    public void Asn_org_is_sanitised_of_contract_forbidden_chars()
    {
        // A provider/org string carrying parens or slashes ("Amazon (AWS)") must not leak a
        // contract-invalid name; it is sanitised before use.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpAsnOrg] = "Amazon (AWS) / EC2",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        AssertNeverUnknown(name, "asn org with parens/slash");
        Assert.True(FingerprintNameComposerContract.IsAllowedShape(name),
            $"synthesised name \"{name}\" must pass the shape contract");
        Assert.DoesNotContain("(", name!);
        Assert.DoesNotContain("/", name!);
    }

    [Fact]
    public void Long_fingerprintId_no_signals_names_by_client_id_not_Unknown()
    {
        var signals = new Dictionary<string, object>();

        var name = FingerprintNameComposer.Compose(
            signals, fingerprintId: "abcdef1234567890", userAgent: null);

        AssertNeverUnknown(name, "fingerprint id only");
        Assert.Equal("Client abcdef12", name);
    }

    [Fact]
    public void Cortex_Xpanse_scanner_named_by_behaviour_and_self_declared_domain()
    {
        // The prod/staging repro. config_exposure fires (it hit /.well-known/openid-configuration)
        // and the UA self-declares a URL. Result must be a behavioural role qualified by the
        // org domain, NEVER "Unknown".
        const string ua = "Hello from Palo Alto Networks, find out more about our scans in " +
                          "https://docs-cortex.paloaltonetworks.com/r/1/Cortex-Xpanse/Scanning-activity";
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.AttackConfigExposure] = true,
            [SignalKeys.GeoCountryCode] = "GB",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: ua);

        AssertNeverUnknown(name, "cortex xpanse scanner");
        Assert.Equal("Config Scanner · paloaltonetworks.com", name);
    }

    [Fact]
    public void Cortex_Xpanse_names_by_self_declared_domain_even_without_behaviour_signal()
    {
        // Even if the attack-surface signal is absent (config_exposure not projected on this
        // request), the self-declared domain still names it -- never "Unknown".
        const string ua = "Hello from Palo Alto Networks, find out more about our scans in " +
                          "https://docs-cortex.paloaltonetworks.com/r/1/Cortex-Xpanse/Scanning-activity";
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IdentityCachedBotProbability] = 0.9,
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: ua);

        AssertNeverUnknown(name, "cortex xpanse, no behaviour signal");
        Assert.Equal("Automated Client · paloaltonetworks.com", name);
    }

    [Theory]
    [InlineData("scanning", "Scanner")]
    [InlineData("reconnaissance", "Recon Bot")]
    [InlineData("attacking", "Attacker")]
    [InlineData("ad_fraud", "Click Fraud")]
    public void Intent_category_drives_the_behavioural_role(string intent, string expectedRolePrefix)
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IntentCategory] = intent,
            [SignalKeys.IpAsn] = "14061",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        AssertNeverUnknown(name, $"intent {intent}");
        Assert.Equal($"{expectedRolePrefix} · AS14061", name);
    }

    [Fact]
    public void Attack_flag_read_tolerantly_as_string_true()
    {
        // Sink hints project into the signal dict as bool OR the string "true" depending on the
        // composition path. Both must drive the role (the sink->evidence typing seam).
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.CveProbeDetected] = "true",
            [SignalKeys.GeoCountryCode] = "RU",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Vuln Scanner · RU", name);
    }

    [Fact]
    public void Bot_type_taxonomy_drives_role_when_no_finer_signal()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentBotType] = nameof(BotType.Scraper),
            [SignalKeys.IpProvider] = "DigitalOcean",
        };

        var name = FingerprintNameComposer.Compose(signals, fingerprintId: null, userAgent: null);

        Assert.Equal("Scraper · DigitalOcean", name);
    }

    [Fact]
    public void Synthesised_generic_residual_is_a_fallback_so_a_real_name_still_overrides_it()
    {
        // "Automated Client · X" and "Client <id>" are overridable placeholders; a later
        // specific role / browser / catalog name must win via hysteresis.
        Assert.True(FingerprintNameComposer.IsFallback("Automated Client · Azure"));
        Assert.True(FingerprintNameComposer.IsFallback("Client abcdef12"));
        Assert.True(FingerprintNameComposer.IsFallback("Unclassified Client"));

        // A specific behavioural role is NOT a fallback -- it is a real, informative name.
        Assert.False(FingerprintNameComposer.IsFallback("Config Scanner · paloaltonetworks.com"));
        Assert.False(FingerprintNameComposer.IsFallback("Scraper · DigitalOcean"));
    }

    [Fact]
    public void Legacy_persisted_Unknown_and_MissingUA_names_still_read_as_fallback()
    {
        // Back-compat: names persisted before this change must still be overridable so a fresh
        // synthesised name replaces them on the next compose.
        Assert.True(FingerprintNameComposer.IsFallback("Unknown AS15169"));
        Assert.True(FingerprintNameComposer.IsFallback("Unknown US"));
        Assert.True(FingerprintNameComposer.IsFallback("Unknown"));
        Assert.True(FingerprintNameComposer.IsFallback("Missing UA Azure"));
        Assert.True(FingerprintNameComposer.IsFallback("Unknown Bot Chrome"));
    }

    [Fact]
    public void ComposeUnknownBot_override_never_emits_the_word_Unknown()
    {
        // The ledger-side verdict-honest override (DetectionLedgerExtensions) calls
        // ComposeUnknownBot directly. It too must never say "Unknown Bot".
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IntentCategory] = "scanning",
            [SignalKeys.IpAsnOrg] = "OVH SAS",
        };

        var name = FingerprintNameComposer.ComposeUnknownBot(signals);

        AssertNeverUnknown(name, "ComposeUnknownBot override");
        Assert.Equal("Scanner · OVH SAS", name);
    }

    [Fact]
    public void Invariant_corpus_never_produces_Unknown_across_signal_combinations()
    {
        // Exhaustive-ish guard so this class of regression ("fingerprint shows Unknown") cannot
        // silently return. Every combination of {UA present?} × {fingerprint id?} × network
        // identity × behaviour must yield a real, non-"Unknown" name.
        string?[] uas =
        {
            null,
            "",
            "curl/8.4.0",
            "Mozilla/5.0 (compatible; SomeUnknownScanner/1.0)",
            "Hello from Palo Alto Networks, find out more about our scans in " +
                "https://docs-cortex.paloaltonetworks.com/r/1/Cortex-Xpanse/Scanning-activity",
        };
        string?[] fpIds = { null, "abc", "abcdef1234567890" };
        (string key, object val)?[] identities =
        {
            null,
            (SignalKeys.IpAsn, "15169"),
            (SignalKeys.GeoCountryCode, "US"),
            (SignalKeys.IpProvider, "Azure"),
            (SignalKeys.IpAsnOrg, "Amazon (AWS) / EC2"),
        };
        (string key, object val)?[] behaviours =
        {
            null,
            (SignalKeys.AttackConfigExposure, true),
            (SignalKeys.IntentCategory, "reconnaissance"),
            (SignalKeys.UserAgentBotType, nameof(BotType.Tool)),
            (SignalKeys.IdentityCachedBotProbability, (object)0.95),
        };

        foreach (var ua in uas)
        foreach (var fp in fpIds)
        foreach (var id in identities)
        foreach (var beh in behaviours)
        {
            var signals = new Dictionary<string, object>();
            if (id is { } i) signals[i.key] = i.val;
            if (beh is { } b) signals[b.key] = b.val;

            var name = FingerprintNameComposer.Compose(signals, fingerprintId: fp, userAgent: ua);

            AssertNeverUnknown(name,
                $"ua={ua ?? "<null>"} fp={fp ?? "<null>"} id={id?.key ?? "-"} beh={beh?.key ?? "-"}");
            Assert.True(FingerprintNameComposerContract.IsAllowedShape(name),
                $"name \"{name}\" failed shape contract for ua={ua} id={id?.key} beh={beh?.key}");
        }
    }
}
