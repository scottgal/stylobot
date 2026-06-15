using System.Net;
using System.Net.Sockets;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Forward-DNS resolver abstraction. Wraps <see cref="Dns.GetHostAddressesAsync(string,CancellationToken)"/>
///     so detectors that need A/AAAA lookups can be unit-tested with a fake
///     resolver instead of hitting the real DNS recursor.
///
///     <para>
///     Introduced for the 2026-06-15 claim-verify-trust gap-analysis fix to
///     <see cref="Mostlylucid.BotDetection.Orchestration.ContributingDetectors.FediverseDomainContributor"/>:
///     NodeInfo lookup proves a claimed fediverse instance domain hosts
///     ActivityPub software, but the client IP was never bound to the claim.
///     Forward DNS closes that gap by resolving the instance domain's published
///     IP addresses and comparing the request's client IP against the result set.
///     </para>
///
///     <para>
///     Default implementation (<see cref="SystemDnsResolver"/>) wraps the system
///     resolver. Other contributors that need similar lookups should depend on
///     this interface so they share the same swap point.
///     </para>
/// </summary>
public interface IDnsResolver
{
    /// <summary>
    ///     Resolve the A / AAAA records for <paramref name="host"/>. Returns an
    ///     empty list when the host resolves to zero addresses (e.g. NXDOMAIN
    ///     surfaces as a thrown <see cref="SocketException"/>, which callers
    ///     should catch and translate into a no-signal outcome).
    /// </summary>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="IDnsResolver"/> backed by <see cref="Dns.GetHostAddressesAsync(string,CancellationToken)"/>.
///     Sealed, allocation-light: returns the raw array as an <see cref="IReadOnlyList{T}"/>.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses;
    }
}