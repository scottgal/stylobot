using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Wave 2 Cat-C* paired regression coverage for the three absorption
///     services -- <see cref="FingerprintAbsorptionService"/>,
///     <see cref="FingerprintModeAbsorptionService"/>, and
///     <see cref="FingerprintRollupRecomputeService"/>. Were
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>s with
///     <c>while + Task.Delay</c> loops; now subscribe to
///     <see cref="TickCadence.Tick5m"/> at construction. The paired-window
///     invariant -- all three on the SAME cadence so a rollup recompute sees
///     consistent state -- is asserted by
///     <see cref="AbsorptionPair_SubscribesToSameWaveWindow"/> per
///     <c>project_absorption_services_migration</c>.
///     <para>
///         Nine facts: 4-fact regression (subscribes, tick runs, dispose
///         releases, side-effect) for each of <see cref="FingerprintAbsorptionService"/>
///         and <see cref="FingerprintModeAbsorptionService"/>, plus the
///         paired-window invariant.
///     </para>
/// </summary>
public sealed class AbsorptionPairTickTests : IDisposable
{
    private readonly string _tempDir;

    public AbsorptionPairTickTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-absorb-pair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(SqliteFingerprintStore Store, SqliteFingerprintBrowserModeStore ModeStore,
        IdentityArchetypeRegistry Archetypes, IOptions<BotDetectionOptions> Options)>
        BuildEnvAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 1,
                    AbsorptionAgeDays = 30,
                    ActiveWindowDays = 90,
                    SubscriptionDebounceMs = 50
                },
                BrowserMode = new BrowserModeOptions
                {
                    Enabled = true,
                    RollupEnabled = false,
                    DrainMaxRowsPerTick = 1_000,
                    RollupMaxFingerprintsPerTick = 100
                }
            }
        });

        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();

        var modeStore = new SqliteFingerprintBrowserModeStore(
            store, options, NullLogger<SqliteFingerprintBrowserModeStore>.Instance);

        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        return (store, modeStore, archetypes, options);
    }

    private async Task<FingerprintAbsorptionService> NewParentAsync(
        RecordingScheduleCoordinator coord)
    {
        var env = await BuildEnvAsync();
        return new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            env.Store, env.Archetypes, env.Options, coord);
    }

    // -- Parent absorption service: 4 facts -------------------------------

    [Fact]
    public async Task Parent_Constructor_SubscribesToTick5m_WithServiceName()
    {
        var coord = new RecordingScheduleCoordinator();
        using var sut = await NewParentAsync(coord);

        var sub = Assert.Single(coord.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick5m);
        sub.Name.Should().Be("FingerprintAbsorptionService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task Parent_OnTickAsync_RunsAgainstEmptyStore_WithoutThrowing()
    {
        var coord = new RecordingScheduleCoordinator();
        using var sut = await NewParentAsync(coord);

        var sub = Assert.Single(coord.Subscriptions);
        await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        sub.Disposed.Should().BeFalse();
        sut.BackstopSweepCount.Should().Be(1);
    }

    [Fact]
    public async Task Parent_Dispose_ReleasesSubscription_AndObservationAppendedHandler()
    {
        var coord = new RecordingScheduleCoordinator();
        var env = await BuildEnvAsync();
        var sut = new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            env.Store, env.Archetypes, env.Options, coord);

        var sub = Assert.Single(coord.Subscriptions);

        // Seed a fingerprint so RecordObservationAsync can fire ObservationAppended.
        await SeedFingerprintAsync(env.Store, "fp-dispose-1");

        sut.Dispose();
        sub.Disposed.Should().BeTrue();

        // B10: after dispose, ObservationAppended must NOT enqueue more work.
        // Drain any pending debounce work first by waiting a short window, then
        // confirm a post-dispose observation doesn't increment the counter.
        var before = sut.EventDrivenAbsorptionCount;
        await env.Store.RecordObservationAsync(
            "fp-dispose-1", new float[env.Store.Layout.Dimension], ct: CancellationToken.None);
        await Task.Delay(300);
        sut.EventDrivenAbsorptionCount.Should().Be(before, "Dispose unhooks ObservationAppended");

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task Parent_OnTickAsync_FoldsAbsorbableObservations()
    {
        // Seed an observation in the store, then drive a single tick handler.
        // The backstop sweep should absorb it.
        var coord = new RecordingScheduleCoordinator();
        var env = await BuildEnvAsync();
        // Use a long debounce so the event-driven fast-path doesn't beat us.
        env.Options.Value.Identity.Vector.SubscriptionDebounceMs = 60_000;

        using var sut = new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            env.Store, env.Archetypes, env.Options, coord);

        await SeedFingerprintAsync(env.Store, "fp-tick-1");
        await env.Store.RecordObservationAsync(
            "fp-tick-1", new float[env.Store.Layout.Dimension], ct: CancellationToken.None);

        var pendingBefore = await env.Store.GetUnabsorbedObservationCountAsync(
            "fp-tick-1", CancellationToken.None);
        pendingBefore.Should().Be(1);

        var sub = Assert.Single(coord.Subscriptions);
        await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Tick-driven backstop absorbed the row.
        var pendingAfter = await env.Store.GetUnabsorbedObservationCountAsync(
            "fp-tick-1", CancellationToken.None);
        pendingAfter.Should().Be(0);
        sut.BackstopSweepCount.Should().Be(1);
    }

    // -- Mode absorption service: 4 facts ---------------------------------

    private async Task<FingerprintModeAbsorptionService> NewModeAsync(
        RecordingScheduleCoordinator coord)
    {
        var env = await BuildEnvAsync();
        return new FingerprintModeAbsorptionService(
            NullLogger<FingerprintModeAbsorptionService>.Instance,
            env.ModeStore, env.Archetypes, env.Options, coord);
    }

    [Fact]
    public async Task Mode_Constructor_SubscribesToTick5m_WithServiceName()
    {
        var coord = new RecordingScheduleCoordinator();
        using var sut = await NewModeAsync(coord);

        var sub = Assert.Single(coord.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick5m);
        sub.Name.Should().Be("FingerprintModeAbsorptionService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task Mode_OnTickAsync_RunsAgainstEmptyStore_WithoutThrowing()
    {
        var coord = new RecordingScheduleCoordinator();
        using var sut = await NewModeAsync(coord);

        var sub = Assert.Single(coord.Subscriptions);
        await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        sub.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Mode_Dispose_ReleasesSubscription()
    {
        var coord = new RecordingScheduleCoordinator();
        var sut = await NewModeAsync(coord);

        var sub = Assert.Single(coord.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task Mode_OnTickAsync_DrainsUnabsorbedObservations()
    {
        var coord = new RecordingScheduleCoordinator();
        var env = await BuildEnvAsync();
        using var sut = new FingerprintModeAbsorptionService(
            NullLogger<FingerprintModeAbsorptionService>.Instance,
            env.ModeStore, env.Archetypes, env.Options, coord);

        // Seed a fingerprint + an unabsorbed mode observation.
        await SeedFingerprintAsync(env.Store, "fp-mode-1");
        var dim = env.Store.Layout.Dimension;
        await env.ModeStore.RecordModeObservationAsync(
            "fp-mode-1", "navigation", new float[dim], ct: CancellationToken.None);

        var before = await env.ModeStore.ListUnabsorbedModeObservationsAsync(1000, CancellationToken.None);
        before.Count.Should().Be(1);

        var sub = Assert.Single(coord.Subscriptions);
        await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        var after = await env.ModeStore.ListUnabsorbedModeObservationsAsync(1000, CancellationToken.None);
        after.Count.Should().Be(0, "tick-driven drain absorbed the row");
    }

    // -- Paired-window invariant (the load-bearing assertion) -------------

    [Fact]
    public async Task AbsorptionPair_SubscribesToSameWaveWindow_AsRollupService()
    {
        // The memory directive: "share the same tick window as the rollup
        // service so a rollup recompute sees consistent parent + per-mode
        // state". All three services must subscribe to the same TickCadence.
        var coord = new RecordingScheduleCoordinator();
        var env = await BuildEnvAsync();

        using var parent = new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance,
            env.Store, env.Archetypes, env.Options, coord);
        using var mode = new FingerprintModeAbsorptionService(
            NullLogger<FingerprintModeAbsorptionService>.Instance,
            env.ModeStore, env.Archetypes, env.Options, coord);
        using var rollup = new FingerprintRollupRecomputeService(
            NullLogger<FingerprintRollupRecomputeService>.Instance,
            env.Store, env.ModeStore, env.Options, coord);

        coord.Subscriptions.Should().HaveCount(3);
        var parentSub = coord.Subscriptions.Single(s => s.Name == "FingerprintAbsorptionService");
        var modeSub = coord.Subscriptions.Single(s => s.Name == "FingerprintModeAbsorptionService");
        var rollupSub = coord.Subscriptions.Single(s => s.Name == "FingerprintRollupRecomputeService");

        // The load-bearing invariant: all three on the SAME cadence.
        parentSub.Cadence.Should().Be(modeSub.Cadence);
        modeSub.Cadence.Should().Be(rollupSub.Cadence);
        parentSub.Cadence.Should().Be(TickCadence.Tick5m,
            "memory project_absorption_services_migration pins the wave window to Tick5m");
    }

    // -- Helpers ----------------------------------------------------------

    private static async Task SeedFingerprintAsync(SqliteFingerprintStore store, string fpId)
    {
        var dim = store.Layout.Dimension;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
    }
}