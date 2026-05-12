namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Discoverable name for the honeypot/simulation pack concept.
///     The actual type is <see cref="SimulationPack"/>; this static class exists so new
///     code searching for "honeypot" finds the right entry point. The "honeypot" name
///     better describes the responsibility (fake response content served to bots) and
///     distinguishes it from <c>ReactionPack</c> (adaptive policy escalation),
///     <c>CompliancePack</c> (data lifecycle), and <c>MonitoringPack</c> (metrics spec).
/// </summary>
/// <remarks>
///     Existing code that uses <see cref="SimulationPack"/> directly continues to work
///     unchanged. Packs are normally loaded from embedded YAML by
///     <see cref="SimulationPackLoader"/>; the <see cref="Create"/> factory below is a
///     convenience for code-first construction (tests, in-process fixtures).
///     The <c>SimulationPack</c> type may be renamed in a future major release.
/// </remarks>
public static class HoneypotPack
{
    /// <summary>
    ///     Convenience factory that returns a new <see cref="SimulationPack"/> with the
    ///     four required identity properties populated. Equivalent to constructing
    ///     <c>new SimulationPack { Id = id, Name = name, Framework = framework, Version = version }</c>.
    /// </summary>
    /// <param name="id">Stable pack identifier (e.g., <c>wordpress-5.9</c>).</param>
    /// <param name="name">Human-readable display name.</param>
    /// <param name="framework">Simulated product/framework (e.g., <c>wordpress</c>, <c>laravel</c>).</param>
    /// <param name="version">Simulated product version string.</param>
    public static SimulationPack Create(string id, string name, string framework, string version) =>
        new()
        {
            Id = id,
            Name = name,
            Framework = framework,
            Version = version
        };
}
