using FluentAssertions;
using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Unit tests for the <see cref="TokenVerifier"/> composite — it dispatches a
///     <see cref="TokenInput"/> to the strategy whose <see cref="TokenKind"/>
///     matches, and returns a Malformed verdict when no strategy is registered for
///     the kind.
/// </summary>
public sealed class TokenVerifierTests
{
    private sealed class FakeKindVerifier(TokenKind kind, TokenOutcome outcome) : ITokenKindVerifier
    {
        public int Calls { get; private set; }
        public TokenKind Kind { get; } = kind;

        public TokenVerdict Verify(TokenInput input)
        {
            Calls++;
            return new TokenVerdict(outcome, "kid", "subject", null, TimeSpan.Zero);
        }
    }

    private static TokenInput Input(TokenKind kind)
        => new(kind, "raw", new Dictionary<string, string>(), "GET", "/");

    [Fact]
    public void Dispatches_to_the_strategy_matching_the_input_kind()
    {
        var rfc = new FakeKindVerifier(TokenKind.Rfc9421HttpSignature, TokenOutcome.Valid);
        var cap = new FakeKindVerifier(TokenKind.SignedBearerToken, TokenOutcome.Expired);
        var sut = new TokenVerifier([rfc, cap]);

        sut.Verify(Input(TokenKind.Rfc9421HttpSignature)).Outcome.Should().Be(TokenOutcome.Valid);
        rfc.Calls.Should().Be(1);
        cap.Calls.Should().Be(0);

        sut.Verify(Input(TokenKind.SignedBearerToken)).Outcome.Should().Be(TokenOutcome.Expired);
        cap.Calls.Should().Be(1);
    }

    [Fact]
    public void No_strategy_for_the_kind_returns_Malformed()
    {
        var sut = new TokenVerifier([]);

        sut.Verify(Input(TokenKind.Rfc9421HttpSignature)).Outcome.Should().Be(TokenOutcome.Malformed);
    }
}
