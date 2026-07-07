using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     DI wiring for the Web-Bot-Auth foundation: the public-key registry
///     (Coordinator) + its Tick1h refresh coordinator + durability Escalator, and
///     the token verifier (C2) with its Ed25519/ECDSA crypto validator. All ship
///     in FOSS as first-class detection capabilities; each self-gates on
///     <c>BotDetection:PublicKeyRegistry:Enabled</c> at runtime.
/// </summary>
public static class WebBotAuthServiceCollectionExtensions
{
    public static IServiceCollection AddWebBotAuth(this IServiceCollection services)
    {
        // ── Options (C7) ──────────────────────────────────────────────────────
        services.AddOptions<PublicKeyRegistryOptions>()
            .BindConfiguration(PublicKeyRegistryOptions.SectionName);
        services.AddOptions<TokenVerifierOptions>()
            .BindConfiguration(TokenVerifierOptions.SectionName);

        // ── Registry (Coordinator) — one instance, also behind the interface ──
        services.TryAddSingleton<PublicKeyRegistry>();
        services.TryAddSingleton<IPublicKeyRegistry>(sp => sp.GetRequiredService<PublicKeyRegistry>());

        // ── Token verifier (C2) + crypto validator ────────────────────────────
        services.TryAddSingleton<ISignatureValidator, CryptoSignatureValidator>();
        services.AddSingleton<ITokenKindVerifier, Rfc9421SignatureVerifier>();
        services.AddSingleton<ITokenKindVerifier, SignedTokenVerifier>();
        // TokenVerifier's constructor is internal (composite dispatcher); build it
        // via a factory so the container doesn't need a public constructor.
        services.TryAddSingleton<ITokenVerifier>(sp => new TokenVerifier(sp.GetServices<ITokenKindVerifier>()));

        // ── Refresh notification sink ─────────────────────────────────────────
        services.TryAddSingleton<Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>>(_ =>
        {
            var inner = new Mostlylucid.Ephemeral.SignalSink(maxCapacity: 16, maxAge: TimeSpan.FromMinutes(15));
            return new Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>(
                inner, maxCapacity: 16, maxAge: TimeSpan.FromMinutes(15));
        });

        // ── Manifest fetch HttpClient ─────────────────────────────────────────
        services.AddHttpClient(PublicKeyRegistryRefreshCoordinator.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        // ── Refresh coordinator (Tick1h) — eager-resolved by the bootstrap ────
        services.TryAddSingleton<PublicKeyRegistryRefreshCoordinator>();

        // ── Durable snapshot store — resolves to null when no path configured ──
        services.TryAddSingleton<IPublicKeySnapshotStore>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<PublicKeyRegistryOptions>>().Value.SnapshotFilePath;
            return string.IsNullOrWhiteSpace(path)
                ? null!
                : new JsonFilePublicKeySnapshotStore(
                    path, sp.GetRequiredService<ILogger<JsonFilePublicKeySnapshotStore>>());
        });

        // ── Escalator — no-op when the store resolves to null ─────────────────
        services.TryAddSingleton<PublicKeyRegistryAtom>();

        return services;
    }
}
