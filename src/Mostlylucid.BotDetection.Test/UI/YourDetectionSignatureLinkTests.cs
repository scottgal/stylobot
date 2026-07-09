using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression guard for the customer-facing "You:" pill 404 (dash- bug report
///     2026-07-08): the header pill linked a base64url entity/session id to
///     <c>/dashboard/signature/{hash}</c>, which resolves only a lowercase-hex
///     primary_signature, so it 404'd. The fix gates the linked signature through
///     <see cref="StyloBotDashboardMiddleware.IsHexSignature"/>; a non-hex id
///     yields a null Signature so the view suppresses the dead link.
/// </summary>
public class YourDetectionSignatureLinkTests
{
    [Theory]
    // Real primary_signatures are Convert.ToHexString(...).ToLowerInvariant().
    [InlineData("003995ecfc0a59ac")] // dash- probed this -> 200
    [InlineData("d8a4acd5945bf41f")] // dash- probed this -> 200
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // 64-char sha256 hex
    public void IsHexSignature_true_for_lowercase_hex_primary_signatures(string sig)
    {
        Assert.True(StyloBotDashboardMiddleware.IsHexSignature(sig));
    }

    [Theory]
    // The base64url entity/session id fallback (PrimarySignature ?? sessionId).
    [InlineData("5vl3E9vAvJwY4BXkoP2AUw")] // the exact value from the live 404 repro
    [InlineData("ABCDEF")]                 // uppercase -> not a lowercase-hex signature
    [InlineData("abc-def_gh")]             // base64url separators
    [InlineData("003995ecfc0a59ag")]       // 'g' is not a hex digit
    [InlineData("")]
    [InlineData(null)]
    public void IsHexSignature_false_for_base64url_ids_and_empty(string? notASignature)
    {
        Assert.False(StyloBotDashboardMiddleware.IsHexSignature(notASignature));
    }
}
