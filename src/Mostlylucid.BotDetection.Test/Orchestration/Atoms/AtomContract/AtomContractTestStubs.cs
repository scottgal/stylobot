using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>Holds one <see cref="HttpContext"/> for an atom under isolated test.</summary>
internal sealed class StaticHttpContextAccessor(HttpContext context) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = context;
}

/// <summary>
///     Returns the caller-supplied default for every lookup — an atom under isolated
///     contract test exercises its code-default behaviour, which is all the emit contract
///     cares about (the emitted signal keys don't depend on tuned parameter values).
/// </summary>
internal sealed class StubDetectorConfigProvider : IDetectorConfigProvider
{
    public DetectorManifest? GetManifest(string detectorName) => null;
    public DetectorDefaults GetDefaults(string detectorName) => new()
    {
        Parameters = new()
        {
            // Mirrors the ip.detector.yaml seeds so IpAtomContractTests can
            // exercise the ip.is_vpn emit path (ASN 9009 = M247, the canonical
            // consumer-VPN egress ASN). Other atoms ignore this parameter.
            ["vpn_egress_asns"] = new object[] { 9009 }
        }
    };
    public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;

    public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
        ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
        => Task.FromResult(defaultValue);

    public void InvalidateCache(string? detectorName = null) { }

    public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
        => new Dictionary<string, DetectorManifest>();
}
