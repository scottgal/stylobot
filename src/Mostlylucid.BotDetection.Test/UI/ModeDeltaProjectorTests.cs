using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Covers the per-mode shift projector that backs the signature detail
///     "Shift from baseline" column (plan task 15). The projection is pure;
///     these tests pin the contract surfaced to the dashboard:
///     <list type="bullet">
///         <item>zero in, zero out (equal centroids)</item>
///         <item>differing centroids produce a non-zero magnitude + at least one labelled slot</item>
///         <item>top-K ordering picks the slot with the largest per-slot L2, not a stray dim</item>
///         <item>length mismatch fails fast</item>
///     </list>
/// </summary>
public class ModeDeltaProjectorTests
{
    [Fact]
    public void Project_returns_zero_magnitude_when_centroids_equal()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var v = new float[layout.Dimension];

        var delta = ModeDeltaProjector.Project(v, v, layout, new StubSignalCatalog());

        delta.Magnitude.Should().Be(0);
        delta.TopSlotLabels.Should().BeEmpty();
    }

    [Fact]
    public void Project_returns_nonzero_magnitude_when_mode_differs()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var identity = new float[layout.Dimension];
        var mode = new float[layout.Dimension];
        mode[0] = 1f; // differ in the first dim of the first slot

        var delta = ModeDeltaProjector.Project(identity, mode, layout, new StubSignalCatalog());

        delta.Magnitude.Should().BeGreaterThan(0);
        delta.TopSlotLabels.Should().NotBeEmpty();
    }

    [Fact]
    public void Project_top_slot_label_matches_largest_per_slot_l2_contribution()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var identity = new float[layout.Dimension];
        var mode = new float[layout.Dimension];

        // Put the biggest delta into a slot we can name -- transport.tls_ja4
        // is 4-dim HashLsh starting at a stable offset. Drive every dim hard
        // so its L2 dominates any single-dim noise we add elsewhere.
        var target = layout.FindSlot("transport.tls_ja4");
        target.Should().NotBeNull("the default layout includes transport.tls_ja4");
        for (var i = 0; i < target!.Width; i++)
            mode[target.Offset + i] = 0.9f;

        // Smaller delta on a single other dim so we can prove the projector
        // ordered by slot L2 and not just by raw dim magnitude.
        var noiseSlot = layout.FindSlot("network.is_datacenter");
        noiseSlot.Should().NotBeNull();
        mode[noiseSlot!.Offset] = 0.1f;

        var delta = ModeDeltaProjector.Project(identity, mode, layout, new StubSignalCatalog(), topK: 2);

        delta.TopSlotLabels.Should().NotBeEmpty();
        delta.TopSlotLabels[0].Should().Be("transport.tls_ja4");
    }

    [Fact]
    public void Project_topK_zero_returns_no_labels_but_still_reports_magnitude()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var identity = new float[layout.Dimension];
        var mode = new float[layout.Dimension];
        mode[0] = 0.5f;

        var delta = ModeDeltaProjector.Project(identity, mode, layout, new StubSignalCatalog(), topK: 0);

        delta.Magnitude.Should().BeGreaterThan(0);
        delta.TopSlotLabels.Should().BeEmpty();
    }

    [Fact]
    public void Project_throws_on_centroid_length_mismatch()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var identity = new float[layout.Dimension];
        var mode = new float[layout.Dimension + 1];

        var act = () => ModeDeltaProjector.Project(identity, mode, layout, new StubSignalCatalog());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Project_falls_back_to_key_when_descriptor_short_is_empty()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var identity = new float[layout.Dimension];
        var mode = new float[layout.Dimension];
        mode[0] = 1f;

        // Catalog has the slot but with empty Short copy -- the projector
        // must surface the slot key rather than render a blank label.
        var blankShortCatalog = new StubSignalCatalog(returnEmptyShort: true);
        var delta = ModeDeltaProjector.Project(identity, mode, layout, blankShortCatalog);

        delta.TopSlotLabels.Should().NotBeEmpty();
        delta.TopSlotLabels[0].Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    ///     Minimal ISignalCatalog stub that returns the slot key verbatim as
    ///     the Short copy (or empty when configured), and short-circuits the
    ///     editor-facing surface area the projector never touches.
    /// </summary>
    private sealed class StubSignalCatalog : ISignalCatalog
    {
        private readonly bool _returnEmptyShort;

        public StubSignalCatalog(bool returnEmptyShort = false)
        {
            _returnEmptyShort = returnEmptyShort;
        }

        public IReadOnlyList<SignalDescriptor> All { get; } = Array.Empty<SignalDescriptor>();
        public IReadOnlyList<string> Namespaces { get; } = Array.Empty<string>();

        public SignalDescriptor? TryGet(string key) => new SignalDescriptor(
            Key: key,
            Kind: SignalKind.String,
            Short: _returnEmptyShort ? string.Empty : key,
            Long: key,
            DeclaringType: typeof(StubSignalCatalog).FullName ?? string.Empty,
            Operators: Array.Empty<string>(),
            Examples: Array.Empty<SignalExample>(),
            Related: Array.Empty<string>(),
            EmittedBy: Array.Empty<string>());

        public SignalDescriptor Require(string key) => TryGet(key)!;

        public IEnumerable<SignalDescriptor> Search(string query, int max = 20) =>
            Enumerable.Empty<SignalDescriptor>();

        public IReadOnlyList<string> SuggestForUnknown(string key, int max = 3) =>
            Array.Empty<string>();
    }
}
