namespace Mostlylucid.BotDetection.Extensions;

public sealed class ExtensionOptions
{
    /// <summary>
    ///     Absolute or relative paths to extension assemblies. Relative paths resolve
    ///     against <see cref="System.AppContext.BaseDirectory"/>. Glob patterns are NOT
    ///     supported in v1; list each DLL explicitly.
    /// </summary>
    public List<string> AssemblyPaths { get; set; } = new();

    /// <summary>
    ///     When true (default), an assembly that fails to load logs a warning and is
    ///     skipped; the host continues. When false, load failures throw at startup.
    ///     Default true preserves warn-never-lock for customers whose license lapsed.
    /// </summary>
    public bool ContinueOnLoadFailure { get; set; } = true;
}
