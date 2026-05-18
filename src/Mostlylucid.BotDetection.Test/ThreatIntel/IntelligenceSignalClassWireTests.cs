using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

/// <summary>
///     Pins the wire-string mapping for <see cref="IntelligenceSignalClass"/>.
///     If anyone renames an enum member, the test for the renamed entry will
///     still pass (because the wire string is fixed), but the matching test
///     here forces a conscious choice when ADDING a new enum value — bumping
///     the count check below catches "I added a class but didn't add a wire
///     mapping".
/// </summary>
public class IntelligenceSignalClassWireTests
{
    [Theory]
    [InlineData(IntelligenceSignalClass.Unknown, "unknown")]
    [InlineData(IntelligenceSignalClass.Reputation, "reputation")]
    [InlineData(IntelligenceSignalClass.Vulnerability, "vulnerability")]
    [InlineData(IntelligenceSignalClass.ScannerInfrastructure, "scanner_infrastructure")]
    [InlineData(IntelligenceSignalClass.ExploitCampaign, "exploit_campaign")]
    [InlineData(IntelligenceSignalClass.KnownBadAutomation, "known_bad_automation")]
    [InlineData(IntelligenceSignalClass.SuspiciousNetworkRange, "suspicious_network_range")]
    [InlineData(IntelligenceSignalClass.CloudInfrastructure, "cloud_infrastructure")]
    [InlineData(IntelligenceSignalClass.TorExit, "tor_exit")]
    public void ToWireString_PinsEnumValueToWireString(IntelligenceSignalClass cls, string expected)
    {
        Assert.Equal(expected, cls.ToWireString());
    }

    [Fact]
    public void ToWireString_AllEnumValuesCovered()
    {
        // If a new IntelligenceSignalClass value is added, this test fails until the
        // wire mapping is updated AND the InlineData above is extended.
        var values = Enum.GetValues<IntelligenceSignalClass>();
        Assert.Equal(9, values.Length);
        // None should map to "unknown" except Unknown itself (else the new member
        // is silently falling into the default arm).
        foreach (var v in values)
        {
            if (v == IntelligenceSignalClass.Unknown) continue;
            Assert.NotEqual("unknown", v.ToWireString());
        }
    }
}
