namespace Mostlylucid.BotDetection.Definitions.TlsReference;

/// <summary>
///     Read API for the JA3/JA4 reference corpus. Backs the
///     <c>tls.cipher_subset_of_real_chrome</c> and <c>tls.version_delta_from_ua</c>
///     checks in <c>TlsFingerprintContributor</c>.
/// </summary>
public interface IJa3ReferenceIndex
{
    /// <summary>
    ///     The most-recent <see cref="Ja3Reference"/> known for the given
    ///     browser family at or below <paramref name="maxVersion"/>, choosing
    ///     desktop or mobile per <paramref name="mobile"/>. Returns null when
    ///     the corpus has no matching entry.
    ///     <para>
    ///     Used by the subset check: the contributor knows the UA-claimed
    ///     browser+version and asks "what should a real Chrome 145 mobile JA3
    ///     look like?". The match falls back to the highest version in the
    ///     corpus at or below the claimed version, so the check still works
    ///     when the corpus lags slightly behind Chrome stable.
    ///     </para>
    /// </summary>
    Ja3Reference? GetReference(string browser, int maxVersion, bool mobile);

    /// <summary>
    ///     The first <see cref="Ja3Reference"/> whose JA3 hash equals
    ///     <paramref name="observedJa3Hash"/>. Returns null when no entry
    ///     matches.
    ///     <para>
    ///     Used by the version-delta check: the contributor takes the
    ///     observed JA3 hash, looks up which (browser, version) it corresponds
    ///     to in the corpus, and compares to the UA-claimed version. A lower
    ///     reference-version vs UA-claimed version = anti-detect-browser-with-
    ///     stale-TLS pattern (Multilogin Mimic, Kameleo Chroma).
    ///     </para>
    /// </summary>
    Ja3Reference? MatchAnyVersion(string observedJa3Hash);

    /// <summary>Count of references in the index. Zero when the corpus failed to load.</summary>
    int Count { get; }
}
