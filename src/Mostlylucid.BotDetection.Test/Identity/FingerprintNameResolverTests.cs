using System;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

public class FingerprintNameResolverTests
{
    private static Fingerprint Build(string? given = null, string? llm = null, string? induced = null) =>
        new Fingerprint
        {
            FingerprintId = "x",
            Centroid = new float[] { 0f },
            CentroidMaturity = 0,
            Weights = new float[] { 1f },
            MemberCount = 0,
            ObservationCount = 0,
            CorrectionCount = 0,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Quality = 0.0,
            InferredClientType = "unknown",
            InferredTypeConfidence = 0.0,
            InferredTypeChangedAt = DateTime.UtcNow,
            GivenName = given,
            LlmName = llm,
            InducedName = induced,
        };

    [Theory]
    [InlineData("g", "l", "i", "g")]
    [InlineData(null, "l", "i", "l")]
    [InlineData(null, null, "i", "i")]
    [InlineData(null, null, null, null)]
    [InlineData("g", null, "i", "g")]
    [InlineData("g", "l", null, "g")]
    [InlineData(null, "l", null, "l")]
    [InlineData("g", null, null, "g")]
    public void Resolves_given_then_llm_then_induced(string? given, string? llm, string? induced, string? expected)
    {
        var fp = Build(given, llm, induced);
        Assert.Equal(expected, FingerprintNameResolver.Resolve(fp));
    }

    [Fact]
    public void DisplayedSlot_returns_topmost_non_null_kind()
    {
        var withAll = Build(given: "g", llm: "l", induced: "i");
        Assert.Equal(FingerprintNameKind.Given, FingerprintNameResolver.DisplayedSlot(withAll));

        var llmOnly = Build(llm: "l", induced: "i");
        Assert.Equal(FingerprintNameKind.Llm, FingerprintNameResolver.DisplayedSlot(llmOnly));

        var inducedOnly = Build(induced: "i");
        Assert.Equal(FingerprintNameKind.Induced, FingerprintNameResolver.DisplayedSlot(inducedOnly));

        var empty = Build();
        Assert.Equal(FingerprintNameKind.None, FingerprintNameResolver.DisplayedSlot(empty));
    }
}
