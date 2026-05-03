using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Generates deterministic canary values for beacon tracking.
///     Same fingerprint + path always produces the same canary.
///     Different fingerprints produce different canaries.
/// </summary>
public sealed class BeaconCanaryGenerator : ICanaryGenerator
{
    private readonly byte[] _keyBytes;
    private readonly int _canaryLength;

    public BeaconCanaryGenerator(string secret, int canaryLength = 8)
    {
        _keyBytes = Encoding.UTF8.GetBytes(secret);
        _canaryLength = canaryLength;
    }

    public string Generate(string fingerprint, string path)
    {
        var input = Encoding.UTF8.GetBytes($"{fingerprint}:{path}");
        var hash = HMACSHA256.HashData(_keyBytes, input);
        return Convert.ToHexStringLower(hash)[.._canaryLength];
    }
}
