using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Forward emit contract for <see cref="IpAtom"/> (second atom slice of task #27):
///     the atom must raise every key its manifest (<c>ip.detector.yaml</c>) declares under
///     <c>emits.on_complete</c>. Validates the per-atom forward pattern on a differently-
///     shaped atom (<c>ip.*</c> + <c>proxy.*</c>, ASN-lookup-gated emits) than HeaderAtom.
///     <para>
///         The <c>ip.asn</c> / <c>ip.asn_org</c> / <c>ip.provider</c> keys only fire when
///         an <see cref="IAsnLookupService"/> resolves the IP, so the stimulus is a public
///         (non-local) datacenter IP plus a stub ASN lookup.
///     </para>
/// </summary>
public sealed class IpAtomContractTests
{
    [Fact]
    public async Task Emits_every_signal_the_manifest_declares_under_on_complete()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8"); // public, non-local → datacenter/ASN path

        var atom = new IpAtom(
            NullLogger<IpAtom>.Instance,
            new StubDetectorConfigProvider(),
            new StaticHttpContextAccessor(http),
            botListDatabase: null,
            asnLookup: new StubAsnLookup(),
            proxyEnvironment: null);

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await atom.DetectAsync(sink, sessionId: "test");

        var raised = sink.Sense()
            .Select(e => e.Signal.Split(':', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        var loader = new DetectorManifestLoader();
        loader.LoadEmbeddedManifests();
        var manifest = loader.GetDetectorManifest("IpContributor")
            ?? throw new InvalidOperationException("Manifest 'IpContributor' not found in embedded resources.");

        var declared = manifest.Emits.OnComplete.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var missing = declared.Except(raised).OrderBy(k => k).ToList();
        missing.Should().BeEmpty(
            "IpAtom must raise every key its manifest declares under emits.on_complete " +
            "(given a datacenter IP + resolving ASN lookup). Declared-but-never-raised: {0}",
            string.Join(", ", missing));
    }

    private sealed class StubAsnLookup : IAsnLookupService
    {
        public Task<AsnInfo?> LookupAsync(string ipAddress, CancellationToken ct = default)
            => Task.FromResult<AsnInfo?>(new AsnInfo
            {
                Asn = 15169,
                OrgName = "Google LLC",
                IsDatacenter = true,
                ProviderName = "Google Cloud",
            });
    }
}
