using FluentAssertions;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Test.WebBotAuth;

/// <summary>
///     Unit tests for <see cref="PublicKeyManifestParser"/> — converts the JSON
///     manifest DTO into <see cref="PublicKeyEntry"/> records, base64-decoding the
///     public key and skipping malformed entries rather than throwing (a single
///     bad row must not sink the whole refresh).
/// </summary>
public sealed class PublicKeyManifestParserTests
{
    private const string SampleKeyB64 = "AAECAwQF"; // 6 bytes: 0..5
    private const string Source = "https://example/keys.json";

    private static PublicKeyManifestEntry ValidEntry() => new()
    {
        KeyId = "kid-1",
        AgentName = "GPTBot",
        PublicKey = SampleKeyB64,
        Algorithm = "ed25519",
        NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public void Parses_a_valid_entry_and_decodes_the_public_key()
    {
        var manifest = new PublicKeyManifest { Keys = [ValidEntry()] };

        var entries = PublicKeyManifestParser.ToEntries(manifest, Source);

        entries.Should().ContainSingle();
        var e = entries[0];
        e.KeyId.Should().Be("kid-1");
        e.AgentName.Should().Be("GPTBot");
        e.Algorithm.Should().Be("ed25519");
        e.Source.Should().Be(Source);
        e.NotAfter.Should().Be(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        e.PublicKey.ToArray().Should().Equal(new byte[] { 0, 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void Skips_entry_with_blank_key_id()
    {
        var bad = ValidEntry();
        bad.KeyId = "  ";
        var good = ValidEntry();
        good.KeyId = "kid-ok";
        var manifest = new PublicKeyManifest { Keys = [bad, good] };

        var entries = PublicKeyManifestParser.ToEntries(manifest, Source);

        entries.Should().ContainSingle().Which.KeyId.Should().Be("kid-ok");
    }

    [Fact]
    public void Skips_entry_with_invalid_base64_public_key()
    {
        var bad = ValidEntry();
        bad.KeyId = "kid-bad";
        bad.PublicKey = "not valid base64!!!";
        var manifest = new PublicKeyManifest { Keys = [bad, ValidEntry()] };

        var entries = PublicKeyManifestParser.ToEntries(manifest, Source);

        entries.Should().ContainSingle().Which.KeyId.Should().Be("kid-1");
    }

    [Fact]
    public void Skips_entry_with_blank_algorithm()
    {
        var bad = ValidEntry();
        bad.KeyId = "kid-bad";
        bad.Algorithm = "";
        var manifest = new PublicKeyManifest { Keys = [bad, ValidEntry()] };

        var entries = PublicKeyManifestParser.ToEntries(manifest, Source);

        entries.Should().ContainSingle().Which.KeyId.Should().Be("kid-1");
    }

    [Fact]
    public void Missing_agent_name_falls_back_to_key_id()
    {
        var e = ValidEntry();
        e.AgentName = null;
        var manifest = new PublicKeyManifest { Keys = [e] };

        var entries = PublicKeyManifestParser.ToEntries(manifest, Source);

        entries.Should().ContainSingle().Which.AgentName.Should().Be("kid-1");
    }

    [Fact]
    public void Null_manifest_returns_empty()
    {
        PublicKeyManifestParser.ToEntries(null, Source).Should().BeEmpty();
    }

    [Fact]
    public void Empty_key_list_returns_empty()
    {
        PublicKeyManifestParser.ToEntries(new PublicKeyManifest(), Source).Should().BeEmpty();
    }
}