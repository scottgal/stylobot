using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     The operator's 2026-09-09 architecture ruling, pinned: "Full name MUST eventually
///     replace it, so the id is fixed but the name changes as we learn more."
///     <para>
///         The signature id is the stable identity; the name is a PROJECTION of current
///         knowledge. So a provisional synthesis must be COMPUTED at the display layer and
///         never stored — storing it makes a provisional label a value the full name then has
///         to displace, which is how a provisional label sticks forever. `Unclassified` was
///         exactly that failure: the terminal value was persisted into the induced-name slot.
///     </para>
///     <para>
///         These tests pin the three properties the ruling demands: (1) every provisional shape
///         is recognised by <see cref="FingerprintNameComposer.IsFallback"/>, which is what makes
///         the writer's persist gate skip it and what makes a real name win later; (2) the
///         projection is stable for a fixed knowledge state and discriminating between
///         fingerprints; (3) nothing stored blocks a later real name.
///     </para>
/// </summary>
public sealed class ProvisionalNameIsProjectedNotStoredTests
{
    private static Dictionary<string, object> Signals(params (string Key, object Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Every_provisional_shape_is_recognised_by_IsFallback_so_the_writer_never_persists_it()
    {
        // The writer's gate is `!IsFallback(freshName)` (FingerprintMatchAtom.EmitInducedNameSignal).
        // If a provisional shape were NOT a fallback, it would be persisted and the whole ruling
        // would be defeated -- so this is the load-bearing assertion, not a nicety.
        var shapes = new[]
        {
            FingerprintNameComposer.ComposeProvisional(Signals(), signatureId: null),
            FingerprintNameComposer.ComposeProvisional(Signals(), signatureId: "6TyG2z5IQguu37X"),
            FingerprintNameComposer.ComposeProvisional(
                Signals((SignalKeys.GeoCountryCode, "GB")), signatureId: "6TyG2z5IQguu37X"),
            FingerprintNameComposer.ComposeProvisional(
                Signals(("request.protocol", "HTTP/2")), signatureId: null),
        };

        foreach (var shape in shapes)
            FingerprintNameComposer.IsFallback(shape).Should().BeTrue(
                $"'{shape}' is provisional, so the induced-name writer must refuse to persist it");

        // The LINE, stated explicitly: a name carrying a specific behavioural role is NOT
        // provisional -- it is a real (informative) description and IS persisted, which is how
        // the fingerprint "earns" a name before the full identity lands. Only the shapes that
        // carry no specific behaviour are projections.
        FingerprintNameComposer
            .IsFallback(FingerprintNameComposer.ComposeProvisional(
                Signals((SignalKeys.IntentCategory, "scanning"), (SignalKeys.GeoCountryCode, "GB")),
                signatureId: "6TyG2z5IQguu37X"))
            .Should().BeFalse("a specific behavioural role is a real name, not a projection");
    }

    [Fact]
    public void No_provisional_shape_contains_a_variant_of_unknown_or_unclassified()
    {
        // Operator directive: "no variant of unknown". Checked over the shapes the composer can
        // actually produce, including the total terminal for a caller that passes no id.
        var banned = new[] { "unknown", "unclassified", "unresolved", "n/a" };

        var shapes = new[]
        {
            FingerprintNameComposer.ComposeProvisional(Signals()),
            FingerprintNameComposer.ComposeProvisional(Signals(), signatureId: "6TyG2z5IQguu37X"),
            FingerprintNameComposer.ComposeProvisional(
                Signals((SignalKeys.GeoCountryCode, "GB")), signatureId: "6TyG2z5IQguu37X"),
            FingerprintNameComposer.ComposeProvisional(
                Signals(("request.protocol", "HTTP/2"))),
            FingerprintNameComposer.ComposeProvisional(
                Signals((SignalKeys.IntentCategory, "scanning"))),
        };

        foreach (var shape in shapes)
        {
            shape.Should().NotBeNullOrWhiteSpace("the projection is total");
            foreach (var word in banned)
                shape.Should().NotContainEquivalentOf(word);
        }
    }

    [Fact]
    public void The_projection_is_stable_for_a_fixed_knowledge_state_and_discriminating_across_fingerprints()
    {
        var signals = Signals((SignalKeys.GeoCountryCode, "GB"), ("request.protocol", "HTTP/2"));

        var first = FingerprintNameComposer.ComposeProvisional(signals, signatureId: "6TyG2z5IQguu37X");
        var again = FingerprintNameComposer.ComposeProvisional(signals, signatureId: "6TyG2z5IQguu37X");
        var other = FingerprintNameComposer.ComposeProvisional(signals, signatureId: "AAAAAAAAAAAAAAAA");

        first.Should().Be(again, "the same fingerprint + the same signals must render the same name");
        first.Should().NotBe(other,
            "two unresolved fingerprints with identical signals must still be distinguishable — " +
            "the discriminator is what answers the operator's 'somewhat unique' requirement");
    }

    [Fact]
    public void Knowledge_changes_the_projection_which_is_the_intended_behaviour()
    {
        // "the id is fixed but the name changes as we learn more" -- a new signal must be
        // reflected. Same fingerprint, one extra fact (the country), a different name.
        var before = FingerprintNameComposer.ComposeProvisional(Signals(), signatureId: "6TyG2z5IQguu37X");
        var after = FingerprintNameComposer.ComposeProvisional(
            Signals((SignalKeys.GeoCountryCode, "GB")), signatureId: "6TyG2z5IQguu37X");

        before.Should().NotBe(after);
    }

    [Fact]
    public void A_real_stored_name_wins_over_the_provisional_projection()
    {
        // THE REPLACEMENT PROPERTY. Nothing stored blocks a later real name: because no
        // provisional value is ever persisted, a real name lands in its slot uncontested and
        // the display tier prefers it. The composer's hysteresis expresses the same guarantee
        // for the compose path: a previous REAL name beats a fresh provisional one.
        var freshProvisional = FingerprintNameComposer.Compose(
            Signals(), previousName: "Config Scanner · paloaltonetworks.com");

        freshProvisional.Should().Be("Config Scanner · paloaltonetworks.com",
            "a stored real name must not be displaced by a provisional synthesis");
        FingerprintNameComposer.IsFallback("Config Scanner · paloaltonetworks.com").Should().BeFalse(
            "a real behavioural name is not a fallback, so the writer persists it and it wins the tier");
    }
}
