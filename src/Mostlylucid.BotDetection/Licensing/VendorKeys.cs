namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Compile-time vendor public key(s) for Ed25519 license signature verification.
///     A license is just a signed JWT - the customer's install only ever needs the public key
///     to verify it (per <c>stylobot-commercial/docs/licensing-simplified.md §key-custody</c>).
///
///     <para>
///     Two keys are exposed: <see cref="PrimaryPublicKey"/> is the active vendor key, and
///     <see cref="PreviousPublicKey"/> is non-empty during a key rotation window so licenses
///     signed under the previous key still verify until customers refresh. After the rotation
///     window closes, set <see cref="PreviousPublicKey"/> back to <see cref="string.Empty"/>.
///     </para>
///
///     <para><b>Rotation procedure:</b></para>
///     <list type="number">
///       <item><description>Mint a new keypair offline (never inside this repo).</description></item>
///       <item><description>Stash the new private key in the vendor portal's KMS / Vault and update <c>Portal__License__PrivateKeyBase64</c>.</description></item>
///       <item><description>Move the old <see cref="PrimaryPublicKey"/> value into <see cref="PreviousPublicKey"/>, paste the new public key into <see cref="PrimaryPublicKey"/>, and ship a new release.</description></item>
///       <item><description>After all customers have updated (or after a stated EOL window), set <see cref="PreviousPublicKey"/> to <see cref="string.Empty"/> in a follow-up release.</description></item>
///     </list>
///
///     <para><b>Override at runtime</b> for development / staging by setting either:</para>
///     <list type="bullet">
///       <item><description>Environment variable <c>STYLOBOT_VENDOR_PUBLIC_KEY</c> (base64), or</description></item>
///       <item><description>Configuration key <c>BotDetection:License:VendorPublicKey</c>.</description></item>
///     </list>
///     The runtime override takes precedence over <see cref="PrimaryPublicKey"/> but does not
///     replace <see cref="PreviousPublicKey"/>. Use <see cref="ResolveActiveKeys"/> to combine.
/// </summary>
public static class VendorKeys
{
    /// <summary>
    ///     Active vendor Ed25519 public key, base64-encoded raw 32-byte format
    ///     (<c>NSec</c> <c>RawPublicKey</c>). Empty until baked for the first public release -
    ///     installs without a baked key fall through to runtime override or treat licenses as
    ///     "no license" (FOSS tier, dashboard shows the unconfigured-card; nothing locks).
    /// </summary>
    /// <remarks>
    ///     Generate the matching private key with <c>StyloFlow.Licensing.Cryptography.Ed25519Signer.GenerateKeyPair()</c>
    ///     on a workstation that can be wiped, then immediately store the private half in the
    ///     vendor portal's KMS and the public half here.
    /// </remarks>
    public const string PrimaryPublicKey = "";

    /// <summary>
    ///     Previous vendor public key, non-empty only during a key rotation window so licenses
    ///     signed under the old key keep verifying. Empty under steady state.
    /// </summary>
    public const string PreviousPublicKey = "";

    /// <summary>
    ///     Environment variable name for the runtime public-key override. Honoured by
    ///     <see cref="ResolveActiveKeys"/> when <see cref="PrimaryPublicKey"/> is empty or when
    ///     a dev/staging install needs to verify against a non-production keypair.
    /// </summary>
    public const string OverrideEnvironmentVariable = "STYLOBOT_VENDOR_PUBLIC_KEY";

    /// <summary>True if at least one verification key is available (compile-time or runtime).</summary>
    public static bool IsConfigured => ResolveActiveKeys().Count > 0;

    /// <summary>
    ///     Returns the de-duplicated, non-empty list of public keys to try when verifying a
    ///     license signature. Order: runtime override → primary → previous. A verifier should
    ///     short-circuit on the first successful Ed25519 verify.
    /// </summary>
    public static IReadOnlyList<string> ResolveActiveKeys()
    {
        var keys = new List<string>(3);

        var envOverride = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        AddIfNew(keys, envOverride);
        AddIfNew(keys, PrimaryPublicKey);
        AddIfNew(keys, PreviousPublicKey);

        return keys;
    }

    private static void AddIfNew(List<string> bag, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var trimmed = key.Trim();
        foreach (var existing in bag)
            if (string.Equals(existing, trimmed, StringComparison.Ordinal))
                return;
        bag.Add(trimmed);
    }
}