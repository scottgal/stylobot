namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Builds Microsoft.Data.Sqlite connection strings for the FOSS detection
///     stores. Centralises the file-path vs in-memory branch so adding ephemeral
///     mode didn't need a rewrite of every <c>Sqlite*Store</c>.
///
///     <para>
///         If <c>BotDetectionOptions.DatabasePath</c> resolves to the literal
///         <see cref="MemoryMarker"/> ("<c>:memory:</c>"), every store routes its
///         connection at a uniquely-named shared in-memory database
///         (<c>file:&lt;name&gt;?mode=memory&amp;cache=shared</c>). Multiple
///         connections to the same name see the same data within the process;
///         state evaporates when the process exits. Useful for ephemeral pods,
///         serverless deployments, demos, and integration tests where managing
///         a Docker volume is overkill.
///     </para>
///
///     <para>
///         Otherwise the path is treated as either a file or a directory: if
///         <paramref name="fileName"/> is supplied the path is treated as a
///         directory and the file name is appended; otherwise the path is the
///         file itself.
///     </para>
/// </summary>
public static class SqliteConnectionStrings
{
    /// <summary>
    ///     Magic value for <c>BotDetectionOptions.DatabasePath</c> that switches
    ///     every Sqlite store into in-memory shared mode.
    /// </summary>
    public const string MemoryMarker = ":memory:";

    /// <summary>
    ///     Build a connection string for a single-file store (botdetection.db,
    ///     sessions.db etc.). <paramref name="databasePath"/> is treated as the
    ///     final file path; the <paramref name="memoryName"/> is only used in
    ///     in-memory mode to namespace this store's slot.
    /// </summary>
    public static string ForFile(string? databasePath, string memoryName)
    {
        return databasePath == MemoryMarker
            ? BuildMemory(memoryName)
            : $"Data Source={databasePath};Cache=Shared";
    }

    /// <summary>
    ///     Build a connection string for a sibling file in the same directory as
    ///     the main database (clusters.db sat next to botdetection.db etc.).
    ///     <paramref name="databasePath"/> is the main DB path; the sibling's
    ///     directory is derived from it.
    /// </summary>
    public static string ForSibling(string? databasePath, string siblingFileName)
    {
        if (databasePath == MemoryMarker)
        {
            var memName = System.IO.Path.GetFileNameWithoutExtension(siblingFileName);
            return BuildMemory(string.IsNullOrEmpty(memName) ? "stylobot" : memName);
        }

        var basePath = System.IO.Path.GetDirectoryName(databasePath)
                       ?? System.AppContext.BaseDirectory;
        var full = System.IO.Path.Combine(basePath, siblingFileName);
        return $"Data Source={full};Cache=Shared";
    }

    /// <summary>
    ///     True when the resolved <c>BotDetectionOptions.DatabasePath</c> means
    ///     "in-memory only" — no files on disk for any FOSS store.
    /// </summary>
    public static bool IsInMemory(string? databasePath) => databasePath == MemoryMarker;

    private static string BuildMemory(string name) =>
        $"Data Source=file:{name}?mode=memory&cache=shared";
}
