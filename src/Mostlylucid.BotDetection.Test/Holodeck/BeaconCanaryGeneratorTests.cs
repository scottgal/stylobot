using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class BeaconCanaryGeneratorTests
{
    private readonly BeaconCanaryGenerator _generator = new("test-secret-key-for-hmac");

    [Fact]
    public void Generate_ReturnsDeterministicCanary()
    {
        var canary1 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        var canary2 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        Assert.Equal(canary1, canary2);
    }

    [Fact]
    public void Generate_DifferentFingerprintsDifferentCanaries()
    {
        var canary1 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        var canary2 = _generator.Generate("fingerprint-xyz", "/wp-login.php");
        Assert.NotEqual(canary1, canary2);
    }

    [Fact]
    public void Generate_DifferentPathsDifferentCanaries()
    {
        var canary1 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        var canary2 = _generator.Generate("fingerprint-abc", "/.env");
        Assert.NotEqual(canary1, canary2);
    }

    [Fact]
    public void Generate_ReturnsCorrectLength()
    {
        var canary = _generator.Generate("fingerprint-abc", "/wp-login.php");
        Assert.Equal(8, canary.Length);
    }

    [Fact]
    public void Generate_CustomLength()
    {
        var generator = new BeaconCanaryGenerator("test-secret", canaryLength: 12);
        var canary = generator.Generate("fp", "/path");
        Assert.Equal(12, canary.Length);
    }

    [Fact]
    public void Generate_OnlyHexCharacters()
    {
        var canary = _generator.Generate("fingerprint-abc", "/wp-login.php");
        Assert.Matches("^[0-9a-f]+$", canary);
    }
}
