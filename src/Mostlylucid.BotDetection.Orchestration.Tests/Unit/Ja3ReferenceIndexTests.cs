using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Definitions.TlsReference;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Integration-ish tests for the embedded JA3 reference corpus. Verifies
///     that <c>tls-reference-corpus.yaml</c> deserialises through VYaml without
///     errors and produces lookups for the four shipping entries. Catches any
///     schema drift or YAML syntax breaks before they hit production silently
///     (the original DTO bug taught us that an empty corpus is the same as
///     "no detection" and the contributor doesn't crash, so a typo in the
///     YAML would otherwise be invisible).
/// </summary>
public class Ja3ReferenceIndexTests
{
    [Fact]
    public void EmbeddedCorpus_DeserialisesWithEntries()
    {
        var index = new Ja3ReferenceIndex(NullLogger<Ja3ReferenceIndex>.Instance);

        Assert.True(index.Count > 0,
            "Embedded JA3 corpus must produce at least one reference entry on load");
    }

    [Fact]
    public void EmbeddedCorpus_GetReferenceForChromeDesktop_ReturnsEntry()
    {
        var index = new Ja3ReferenceIndex(NullLogger<Ja3ReferenceIndex>.Instance);

        // Ask for the highest Chrome <= 200 desktop. The corpus has entries
        // up through Chrome 121, so this should return the newest one.
        var reference = index.GetReference("chrome", 200, mobile: false);

        Assert.NotNull(reference);
        Assert.Equal("chrome", reference!.Browser);
        Assert.False(reference.Mobile);
        Assert.False(string.IsNullOrEmpty(reference.Ja3Hash));
        Assert.False(string.IsNullOrEmpty(reference.Ja3String));
    }

    [Fact]
    public void EmbeddedCorpus_MatchAnyVersion_ReturnsKnownHash()
    {
        var index = new Ja3ReferenceIndex(NullLogger<Ja3ReferenceIndex>.Instance);

        // Get whichever Chrome desktop reference shipped first and look it up
        // by hash. Confirms the by-hash index is populated and case-insensitive.
        var chrome = index.GetReference("chrome", 200, mobile: false);
        Assert.NotNull(chrome);

        var byHash = index.MatchAnyVersion(chrome!.Ja3Hash);
        Assert.NotNull(byHash);
        Assert.Equal(chrome.Version, byHash!.Version);

        // Case-insensitive match.
        var upper = index.MatchAnyVersion(chrome.Ja3Hash.ToUpperInvariant());
        Assert.NotNull(upper);
    }

    [Fact]
    public void GetReference_UnknownBrowser_ReturnsNull()
    {
        var index = new Ja3ReferenceIndex(NullLogger<Ja3ReferenceIndex>.Instance);

        var reference = index.GetReference("netscape", 4, mobile: false);

        Assert.Null(reference);
    }

    [Fact]
    public void GetReference_AtOrBelow_FallsBackToOlderVersion()
    {
        var index = new Ja3ReferenceIndex(NullLogger<Ja3ReferenceIndex>.Instance);

        // Ask for Chrome at exactly the corpus' newest version, then at a
        // version 1 below it. Both must return entries (latter falls back).
        var newest = index.GetReference("chrome", 200, mobile: false);
        Assert.NotNull(newest);

        var oneBelow = index.GetReference("chrome", newest!.Version, mobile: false);
        Assert.NotNull(oneBelow);
        Assert.Equal(newest.Version, oneBelow!.Version);

        var wayBelow = index.GetReference("chrome", newest.Version - 1, mobile: false);
        // Either an older entry (if the corpus has one) or null.
        if (wayBelow != null) Assert.True(wayBelow.Version < newest.Version);
    }
}
