namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Public entry point for <see cref="ITokenVerifier"/> (contract C2). One
///     verifier, two <see cref="TokenKind"/>s: dispatches each input to the
///     registered <see cref="ITokenKindVerifier"/> whose <see cref="ITokenKindVerifier.Kind"/>
///     matches. Singleton; stateless.
/// </summary>
public sealed class TokenVerifier : ITokenVerifier
{
    private readonly Dictionary<TokenKind, ITokenKindVerifier> _byKind;

    internal TokenVerifier(IEnumerable<ITokenKindVerifier> verifiers)
    {
        _byKind = new Dictionary<TokenKind, ITokenKindVerifier>();
        foreach (var v in verifiers) _byKind[v.Kind] = v;
    }

    public TokenVerdict Verify(TokenInput input)
    {
        if (_byKind.TryGetValue(input.Kind, out var verifier))
            return verifier.Verify(input);

        // No strategy registered for this kind — a configuration gap, not a token
        // problem. Fail closed with Malformed rather than throwing on the hot path.
        return new TokenVerdict(TokenOutcome.Malformed, null, null, null, TimeSpan.Zero);
    }
}
