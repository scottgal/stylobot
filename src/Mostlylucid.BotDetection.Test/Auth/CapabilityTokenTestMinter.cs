using System.Text;
using System.Text.Json;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Mints StyloFlow-style license capability tokens for the tests. Replicates
///     the canonical-content scheme of <c>StyloFlow.Licensing.LicenseSigningService</c>
///     (sorted keys, signature field excluded, non-indented) so a token this
///     produces would also verify under the real signer — and vice-versa. The wire
///     form is base64 of the signed JSON (the value of <c>Authorization: License</c>).
/// </summary>
internal static class CapabilityTokenTestMinter
{
    /// <summary>Builds the canonical signable content: sorted string fields, signature excluded.</summary>
    public static string Canonical(IReadOnlyDictionary<string, string> fields)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (var kv in fields.Where(f => f.Key != "signature").OrderBy(f => f.Key, StringComparer.Ordinal))
                w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Returns the signed JSON (not yet base64) for a set of string fields.</summary>
    public static string SignedJson(IReadOnlyDictionary<string, string> fields, byte[] rawPrivateKey)
    {
        var signature = Convert.ToBase64String(
            CryptoTestHelpers.SignEd25519(Encoding.UTF8.GetBytes(Canonical(fields)), rawPrivateKey));

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (var kv in fields) w.WriteString(kv.Key, kv.Value);
            w.WriteString("signature", signature);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The full <c>Authorization: License</c> value — base64 of the signed JSON.</summary>
    public static string Mint(IReadOnlyDictionary<string, string> fields, byte[] rawPrivateKey)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(SignedJson(fields, rawPrivateKey)));

    public static string ToBase64(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
}
