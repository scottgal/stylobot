namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Marks a class as the registration entry point for a StyloBot pack.
///     Build-time composition (xcaddy-style) only - this attribute is not used
///     for runtime discovery via reflection because NativeAOT cannot JIT
///     dynamically-loaded assemblies.
///
///     Each variant binary (FOSS base, stylobot-aspnet-monitoring, etc.) has
///     its own <c>Program.cs</c> that calls every included pack's static
///     <c>Register(IServiceCollection, IConfiguration)</c> method explicitly.
///     The attribute is documentation + a hook for future source-generated
///     "_PackRegistrar.g.cs" tooling that would emit those calls automatically.
///
///     Recommended pack shape:
///     <code>
///     [ModuleEntryPoint(
///         Id = "monitoring.aspnet.hot-reload",
///         Version = "1.0.0",
///         Capability = "stylobot.monitoring.aspnet.hot-reload")]
///     public static class MonitoringAspNetModule
///     {
///         public static void Register(IServiceCollection services, IConfiguration configuration)
///         {
///             // services.AddCommercialMonitoringPacks(...);
///         }
///     }
///     </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModuleEntryPointAttribute : Attribute
{
    public ModuleEntryPointAttribute(string id, string version)
    {
        Id = id;
        Version = version;
    }

    /// <summary>Stable identifier for the pack (e.g. "monitoring.aspnet.hot-reload").</summary>
    public string Id { get; }

    /// <summary>Pack semver. Bumped independently of the FOSS host version.</summary>
    public string Version { get; }

    /// <summary>
    ///     License capability claim required at startup (e.g.
    ///     <c>stylobot.monitoring.aspnet.hot-reload</c>). Null means the pack
    ///     is free and needs no per-pack entitlement. Set on commercial packs
    ///     so the variant binary's startup check can short-circuit gracefully
    ///     when the customer's JWT doesn't grant this capability.
    /// </summary>
    public string? Capability { get; init; }

    /// <summary>Human-readable description shown in the startup banner and license dashboard.</summary>
    public string? Description { get; init; }
}
