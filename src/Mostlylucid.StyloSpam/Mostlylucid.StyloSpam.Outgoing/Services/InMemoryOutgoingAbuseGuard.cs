using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Outgoing.Configuration;

namespace Mostlylucid.StyloSpam.Outgoing.Services;

public sealed class InMemoryOutgoingAbuseGuard : IOutgoingAbuseGuard
{
    private sealed class UserState
    {
        public ConcurrentQueue<DateTimeOffset> Strikes { get; } = new();
        public DateTimeOffset? BlockedUntilUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, UserState> _states = new();
    private readonly OutgoingAbuseGuardOptions _options;

    public InMemoryOutgoingAbuseGuard(IOptions<StyloSpamOutgoingOptions> options)
    {
        _options = options.Value.AbuseGuard;
    }

    public AbuseGuardEvaluation EvaluateAndRecord(string tenantId, string userId, SpamVerdict verdict, DateTimeOffset atUtc)
    {
        if (!_options.Enabled)
        {
            return new AbuseGuardEvaluation(false, "Abuse guard disabled", 0, null);
        }

        var key = $"{tenantId}:{userId}";
        var state = _states.GetOrAdd(key, _ => new UserState());

        if (state.BlockedUntilUtc is { } blockedUntil && blockedUntil > atUtc)
        {
            return new AbuseGuardEvaluation(true, "User is temporarily blocked for repeated spam activity", state.Strikes.Count, blockedUntil);
        }

        Trim(state.Strikes, atUtc);

        if (verdict is SpamVerdict.Warn or SpamVerdict.Quarantine or SpamVerdict.Block)
        {
            state.Strikes.Enqueue(atUtc);
            Trim(state.Strikes, atUtc);

            if (state.Strikes.Count >= Math.Max(1, _options.StrikeThreshold))
            {
                state.BlockedUntilUtc = atUtc.AddMinutes(Math.Max(1, _options.BlockDurationMinutes));
                return new AbuseGuardEvaluation(
                    true,
                    $"User exceeded spam strike threshold ({state.Strikes.Count}/{_options.StrikeThreshold})",
                    state.Strikes.Count,
                    state.BlockedUntilUtc);
            }

            return new AbuseGuardEvaluation(false, "Strike recorded", state.Strikes.Count, null);
        }

        return new AbuseGuardEvaluation(false, "No strike recorded", state.Strikes.Count, null);
    }

    private void Trim(ConcurrentQueue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var threshold = now.AddMinutes(-Math.Max(1, _options.StrikeWindowMinutes));
        while (queue.TryPeek(out var ts) && ts < threshold)
        {
            queue.TryDequeue(out _);
        }
    }
}
