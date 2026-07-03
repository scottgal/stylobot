using FluentAssertions;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Pin tests for the StyloBot.Internal User-Agent contract. Three pieces
///     have to stay aligned or the gateway's identity archetypes misfire on
///     traffic from the marketing site / AspNetPack remote sink / OtelMesh /
///     Prometheus scraper:
///
///       1. <see cref="StyloBotInternalUserAgent.Value"/> formats the canonical
///          token internal HttpClients ship.
///       2. <c>uap-regexes.yaml</c> recognises that token and emits family
///          <c>StyloBot.Internal</c>.
///       3. The identity archetype <c>stylobot-internal.yaml</c> pins on
///          family <c>StyloBot.Internal</c> so the centroid matcher lands the
///          shape on the right archetype instead of the wget archetype.
///
///     If any of these drift the regression is silent at first -- internal
///     traffic just gets misclassified -- and only surfaces once the gateway
///     rate-limits the upstream's identity. These tests trip at build time
///     so the drift can't ship.
/// </summary>
public class StyloBotInternalUserAgentTests
{
    [Fact]
    public void Value_uses_canonical_format_with_version_and_contact_url()
    {
        var ua = StyloBotInternalUserAgent.Value;

        ua.Should().StartWith("StyloBot.Internal/");
        ua.Should().Contain("(+https://stylo.bot)");
    }

    [Fact]
    public void UserAgentParser_classifies_the_canonical_UA_as_family_StyloBot_Internal()
    {
        var (family, _) = UserAgentParser.Parse(StyloBotInternalUserAgent.Value);

        family.Should().Be("StyloBot.Internal",
            "the identity archetype 'stylobot-internal' keys on this family value; " +
            "any drift in either the UA token or the uap-regex makes the archetype miss");
    }

    /// <summary>
    ///     Guard rail: the UA must NOT contain the literal "Wget" token. Real
    ///     wget UAs match the SimpleToolRegex contributor at +0.80 risk; if a
    ///     future rename accidentally includes "Wget" anywhere in the StyloBot
    ///     UA string the detector would self-trigger on its own internal
    ///     traffic.
    /// </summary>
    [Fact]
    public void Value_does_not_collide_with_known_bot_token_substrings()
    {
        var ua = StyloBotInternalUserAgent.Value;

        ua.Should().NotContainEquivalentOf("wget");
        ua.Should().NotContainEquivalentOf("curl");
        ua.Should().NotContainEquivalentOf("python-requests");
    }
}