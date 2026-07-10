namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Shared helper for the per-store SQLite ctors that derive their file location
///     from the configured <c>DatabasePath</c>'s directory. Guards the one edge that
///     bit us: a bare / relative <c>DatabasePath</c> (for example <c>"botdetection.db"</c>)
///     has <see cref="System.IO.Path.GetDirectoryName(string)"/> == <c>""</c> (empty,
///     NOT null), so the common <c>GetDirectoryName(x) ?? AppContext.BaseDirectory</c>
///     fallback does not catch it and <c>Directory.CreateDirectory("")</c> throws
///     <see cref="System.ArgumentException"/> at construction, failing the host closed
///     at boot. Route every store's directory-create through here so the empty case is
///     a no-op (the file just lives in the current directory) and no store can
///     reintroduce the crash.
/// </summary>
internal static class StoreDbDirectory
{
    /// <summary>
    ///     Creates <paramref name="directory"/> if it is a real path; a null or empty
    ///     directory (a bare relative filename resolves against the current directory)
    ///     is a no-op rather than a throw.
    /// </summary>
    public static void EnsureExists(string? directory)
    {
        if (!string.IsNullOrEmpty(directory))
            System.IO.Directory.CreateDirectory(directory);
    }
}
