using Mostlylucid.BotDetection.Enforcement;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     Locks the field-length contract on <c>PostDetectionActionGate</c>'s fast-path
///     <c>TriggeredActionPolicyName</c> stamps. Regression for a prod data-loss incident
///     (2026-08-17): two of these literals were 26 characters and silently overflowed a
///     downstream fixed-length field on every request that hit either fast path, corrupting
///     an entire batch write. FOSS cannot see every consumer's schema, so this test is the
///     durable, FOSS-side half of the fix -- it pins the promise ("every fast-path name stays
///     at or under <see cref="PostDetectionActionGate.MaxFastPathActionPolicyNameLength"/>
///     characters") independently of what any specific downstream store does with the value.
/// </summary>
public class PostDetectionActionGateFastPathNameLengthTests
{
    public static TheoryData<string, string> FastPathNames => new()
    {
        { nameof(PostDetectionActionGate.VerifiedCrawlerFastPathName), PostDetectionActionGate.VerifiedCrawlerFastPathName },
        { nameof(PostDetectionActionGate.RegistryClientFastPathName), PostDetectionActionGate.RegistryClientFastPathName },
        { nameof(PostDetectionActionGate.WebhookRecognizedFastPathName), PostDetectionActionGate.WebhookRecognizedFastPathName },
    };

    [Theory]
    [MemberData(nameof(FastPathNames))]
    public void Fast_path_name_fits_the_documented_length_contract(string constantName, string value)
    {
        Assert.True(
            value.Length <= PostDetectionActionGate.MaxFastPathActionPolicyNameLength,
            $"{constantName} = \"{value}\" is {value.Length} chars, over the " +
            $"{PostDetectionActionGate.MaxFastPathActionPolicyNameLength}-char fast-path contract -- " +
            "shorten it rather than widening the contract; a downstream consumer's fixed-length " +
            "field is what this protects.");
    }
}
