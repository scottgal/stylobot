namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Low-level detached-signature verification over raw bytes, dispatched by
///     RFC 9421 algorithm name. This is the single crypto seam used by both the
///     RFC 9421 signature verifier and the license-capability verifier — one home
///     for the primitives so there are no parallel crypto pipelines.
///     <para>
///         The shape doc (C2) references a <c>StyloFlow.Licensing.ISignatureValidator</c>;
///         that interface never existed. This is the FOSS-side seam that fills the
///         role, so a commercial impl can later be substituted with a vendor trust
///         root without changing the verifiers. See channel:
///         <c>overview-wba-foundation-signaturevalidator-gap.md</c>.
///     </para>
/// </summary>
public interface ISignatureValidator
{
    /// <summary>True if this validator can verify signatures for <paramref name="algorithm"/>.</summary>
    bool Supports(string algorithm);

    /// <summary>
    ///     Verifies <paramref name="signature"/> over <paramref name="data"/> with
    ///     <paramref name="publicKey"/> under <paramref name="algorithm"/>. Never
    ///     throws — a malformed key, wrong-length signature, or unknown algorithm
    ///     all return <c>false</c>.
    /// </summary>
    bool Verify(string algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);
}
