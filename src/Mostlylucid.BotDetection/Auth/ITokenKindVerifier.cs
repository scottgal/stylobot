namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Internal strategy behind <see cref="ITokenVerifier"/>: one implementation
///     per <see cref="TokenKind"/>. The public <see cref="TokenVerifier"/> composite
///     dispatches an incoming <see cref="TokenInput"/> to the strategy whose
///     <see cref="Kind"/> matches. Taxonomy: Molecule (stateless, pure).
/// </summary>
internal interface ITokenKindVerifier
{
    TokenKind Kind { get; }

    TokenVerdict Verify(TokenInput input);
}
