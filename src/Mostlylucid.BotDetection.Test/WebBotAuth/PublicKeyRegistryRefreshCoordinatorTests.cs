using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.WebBotAuth;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.WebBotAuth;

/// <summary>
///     Unit tests for <see cref="PublicKeyRegistryRefreshCoordinator"/> — the
///     Tick1h coordinator that fetches the JSON manifest and atomically replaces
///     the registry's fetched layer. Mirrors the well-known-bots refresh-service
///     test template (subscription shape, empty-Url no-op, download path, interval
///     guard, dispose).
/// </summary>
public sealed class PublicKeyRegistryRefreshCoordinatorTests
{
    private const string ValidKeyB64 = "AAECAwQFBgcICQoLDA0ODw==";

    private sealed class StubOptionsMonitor(PublicKeyRegistryOptions value) : IOptions<PublicKeyRegistryOptions>
    {
        public PublicKeyRegistryOptions Value { get; } = value;
    }

    private sealed class ManifestHttpClientFactory(string json, HttpStatusCode status = HttpStatusCode.OK)
        : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(this, json, status));

        private sealed class Handler(ManifestHttpClientFactory owner, string json, HttpStatusCode status)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Calls++;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }
    }

    private static string ManifestJson(params (string keyId, string agent)[] keys)
    {
        var manifest = new PublicKeyManifest
        {
            Version = 1,
            Keys = keys.Select(k => new PublicKeyManifestEntry
            {
                KeyId = k.keyId, AgentName = k.agent, PublicKey = ValidKeyB64, Algorithm = "ed25519"
            }).ToList()
        };
        return JsonSerializer.Serialize(manifest);
    }

    private static PublicKeyRegistryOptions Enabled(string url = "https://example.com/keys.json") => new()
    {
        Enabled = true, ManifestUrl = url
    };

    private static PublicKeyRegistryRefreshCoordinator Make(
        PublicKeyRegistryOptions opts, PublicKeyRegistry registry, IHttpClientFactory http, RecordingScheduleCoordinator coord)
        => new(new StubOptionsMonitor(opts), registry, http, NullLogger<PublicKeyRegistryRefreshCoordinator>.Instance, coord);

    // ── Subscription ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_subscribes_to_Tick1h()
    {
        var coord = new RecordingScheduleCoordinator();
        using var sut = Make(Enabled(), new PublicKeyRegistry(),
            new ManifestHttpClientFactory(ManifestJson()), coord);

        var sub = Assert.Single(coord.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1h);
        sub.Name.Should().Be(nameof(PublicKeyRegistryRefreshCoordinator));
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public void Constructor_seeds_manual_keys_when_enabled()
    {
        var opts = Enabled(url: "");
        opts.ManualKeys.Add(new ManualPublicKeyOptions
        {
            KeyId = "manual-1", AgentName = "OperatorBot", PublicKey = ValidKeyB64, Algorithm = "ed25519"
        });
        var registry = new PublicKeyRegistry();

        using var sut = Make(opts, registry, new ManifestHttpClientFactory(ManifestJson()), new RecordingScheduleCoordinator());

        registry.TryResolve("manual-1", out var e).Should().BeTrue();
        e.AgentName.Should().Be("OperatorBot");
    }

    [Fact]
    public void Constructor_does_not_seed_manual_keys_when_disabled()
    {
        var opts = new PublicKeyRegistryOptions { Enabled = false };
        opts.ManualKeys.Add(new ManualPublicKeyOptions
        {
            KeyId = "manual-1", PublicKey = ValidKeyB64, Algorithm = "ed25519"
        });
        var registry = new PublicKeyRegistry();

        using var sut = Make(opts, registry, new ManifestHttpClientFactory(ManifestJson()), new RecordingScheduleCoordinator());

        registry.TryResolve("manual-1", out _).Should().BeFalse();
    }

    // ── OnTick / Refresh ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnTickAsync_is_noop_when_disabled()
    {
        var http = new ManifestHttpClientFactory(ManifestJson(("k", "A")));
        var coord = new RecordingScheduleCoordinator();
        using var sut = Make(new PublicKeyRegistryOptions { Enabled = false, ManifestUrl = "https://x/keys.json" },
            new PublicKeyRegistry(), http, coord);

        await coord.Subscriptions.Single().Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        http.Calls.Should().Be(0, "disabled -> no fetch");
    }

    [Fact]
    public async Task OnTickAsync_is_noop_when_url_empty()
    {
        var http = new ManifestHttpClientFactory(ManifestJson(("k", "A")));
        var coord = new RecordingScheduleCoordinator();
        using var sut = Make(Enabled(url: ""), new PublicKeyRegistry(), http, coord);

        await coord.Subscriptions.Single().Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        http.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshOnceAsync_loads_manifest_into_registry()
    {
        var registry = new PublicKeyRegistry();
        using var sut = Make(Enabled(), registry,
            new ManifestHttpClientFactory(ManifestJson(("gptbot-1", "GPTBot"), ("perplexity-1", "PerplexityBot"))),
            new RecordingScheduleCoordinator());

        var ok = await sut.RefreshOnceAsync();

        ok.Should().BeTrue();
        registry.TryResolve("gptbot-1", out var e).Should().BeTrue();
        e.AgentName.Should().Be("GPTBot");
        registry.Snapshot().Should().HaveCount(2);
        registry.LastRefreshedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshOnceAsync_false_when_disabled()
    {
        using var sut = Make(new PublicKeyRegistryOptions { Enabled = false, ManifestUrl = "https://x/keys.json" },
            new PublicKeyRegistry(), new ManifestHttpClientFactory(ManifestJson(("k", "A"))), new RecordingScheduleCoordinator());

        (await sut.RefreshOnceAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshOnceAsync_false_and_registry_intact_when_download_fails()
    {
        var registry = new PublicKeyRegistry();
        using var seed = Make(Enabled(), registry,
            new ManifestHttpClientFactory(ManifestJson(("k", "A"))), new RecordingScheduleCoordinator());
        await seed.RefreshOnceAsync();
        registry.Snapshot().Should().HaveCount(1);

        using var sut = Make(Enabled(), registry,
            new ManifestHttpClientFactory("", HttpStatusCode.ServiceUnavailable), new RecordingScheduleCoordinator());

        (await sut.RefreshOnceAsync()).Should().BeFalse();
        registry.Snapshot().Should().HaveCount(1, "a failed refresh must not clear the registry");
    }

    [Fact]
    public async Task RefreshOnceAsync_raises_refreshed_signal_on_success()
    {
        var sink = new Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>(
            new Mostlylucid.Ephemeral.SignalSink(maxCapacity: 8, maxAge: TimeSpan.FromMinutes(5)),
            maxCapacity: 8, maxAge: TimeSpan.FromMinutes(5));
        PublicKeyRegistryRefreshedSignal? received = null;
        sink.TypedSignalRaised += evt => received = evt.Payload;

        var sut = new PublicKeyRegistryRefreshCoordinator(
            new StubOptionsMonitor(Enabled()), new PublicKeyRegistry(),
            new ManifestHttpClientFactory(ManifestJson(("k", "A"))),
            NullLogger<PublicKeyRegistryRefreshCoordinator>.Instance,
            new RecordingScheduleCoordinator(), sink);
        using var _ = sut;

        await sut.RefreshOnceAsync();

        received.Should().NotBeNull();
        received!.KeyCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_unsubscribes()
    {
        var coord = new RecordingScheduleCoordinator();
        var sut = Make(Enabled(), new PublicKeyRegistry(), new ManifestHttpClientFactory(ManifestJson()), coord);

        sut.Dispose();

        coord.Subscriptions.Single().Disposed.Should().BeTrue();
    }
}
