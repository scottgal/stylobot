namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Converts the JSON manifest DTO (and operator-supplied manual key config)
///     into validated <see cref="PublicKeyEntry"/> records. Malformed entries
///     (blank id, blank algorithm, un-decodable public key) are skipped so one
///     bad row cannot sink an otherwise-valid refresh.
/// </summary>
public static class PublicKeyManifestParser
{
    /// <summary>Converts every valid entry in <paramref name="manifest"/> into a registry entry.</summary>
    public static IReadOnlyList<PublicKeyEntry> ToEntries(PublicKeyManifest? manifest, string source)
    {
        if (manifest?.Keys is not { Count: > 0 } keys) return [];

        var result = new List<PublicKeyEntry>(keys.Count);
        foreach (var k in keys)
        {
            if (TryToEntry(k.KeyId, k.AgentName, k.PublicKey, k.Algorithm, k.NotAfter, source, out var entry))
                result.Add(entry);
        }

        return result;
    }

    /// <summary>
    ///     Converts a single set of raw fields into a <see cref="PublicKeyEntry"/>,
    ///     or returns <c>false</c> when the fields are malformed. Shared by the
    ///     manifest path and the manual-key (Options) path.
    /// </summary>
    public static bool TryToEntry(
        string? keyId,
        string? agentName,
        string? publicKeyBase64,
        string? algorithm,
        DateTimeOffset? notAfter,
        string source,
        out PublicKeyEntry entry)
    {
        entry = null!;

        if (string.IsNullOrWhiteSpace(keyId)) return false;
        if (string.IsNullOrWhiteSpace(algorithm)) return false;
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) return false;

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (keyBytes.Length == 0) return false;

        var name = string.IsNullOrWhiteSpace(agentName) ? keyId : agentName;
        entry = new PublicKeyEntry(keyId, name, keyBytes, algorithm, notAfter, source);
        return true;
    }
}
