using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.Test.Auth;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.WebBotAuth;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.WebBotAuth;

/// <summary>
///     Integration smoke tests for <see cref="WebBotAuthServiceCollectionExtensions.AddWebBotAuth"/>:
///     the whole foundation resolves from a real container and verifies an actual
///     signed request end-to-end. Guards the wiring the unit tests can't — the
///     internal <see cref="TokenVerifier"/> constructor built via factory, the
///     null-resolving snapshot store, and optional-parameter resolution.
/// </summary>
public sealed class WebBotAuthDiTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHttpClient();
        services.AddSingleton<IScheduleCoordinator, RecordingScheduleCoordinator>();
        services.AddWebBotAuth();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolves_the_token_verifier_and_registry()
    {
        using var sp = Build();

        sp.GetService<ITokenVerifier>().Should().NotBeNull();
        sp.GetService<IPublicKeyRegistry>().Should().BeSameAs(sp.GetService<PublicKeyRegistry>());
        sp.GetService<PublicKeyRegistryRefreshCoordinator>().Should().NotBeNull();
        sp.GetService<PublicKeyRegistryAtom>().Should().NotBeNull();
    }

    [Fact]
    public void Snapshot_store_resolves_null_without_a_configured_path()
    {
        using var sp = Build();
        sp.GetService<IPublicKeySnapshotStore>().Should().BeNull();
    }

    [Fact]
    public void Snapshot_store_resolves_when_a_path_is_configured()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stylobot-di-{Guid.NewGuid():N}.json");
        using var sp = Build(new Dictionary<string, string?>
        {
            [$"{PublicKeyRegistryOptions.SectionName}:SnapshotFilePath"] = path
        });

        sp.GetService<IPublicKeySnapshotStore>().Should().BeOfType<JsonFilePublicKeySnapshotStore>();
    }

    [Fact]
    public void Resolved_verifier_verifies_a_real_rfc9421_signature_end_to_end()
    {
        using var sp = Build();
        var (pub, priv) = CryptoTestHelpers.NewEd25519KeyPair();
        sp.GetRequiredService<PublicKeyRegistry>()
            .SeedManual([new PublicKeyEntry("kid-e2e", "GPTBot", pub, "ed25519", null, "manual")]);

        var signer = new Rfc9421TestSigner
        {
            Components = ["@method", "@path", "@authority"],
            Values = new Dictionary<string, string>
            {
                ["@method"] = "GET", ["@path"] = "/", ["@authority"] = "example.com"
            },
            KeyId = "kid-e2e",
            Algorithm = "ed25519",
            Created = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        };
        var raw = signer.BuildEd25519(priv);
        var input = new TokenInput(TokenKind.Rfc9421HttpSignature, raw,
            new Dictionary<string, string> { ["@authority"] = "example.com" }, "GET", "/");

        sp.GetRequiredService<ITokenVerifier>().Verify(input).Outcome.Should().Be(TokenOutcome.Valid);
    }
}
