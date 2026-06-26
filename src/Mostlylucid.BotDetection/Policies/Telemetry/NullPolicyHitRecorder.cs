using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Telemetry;

// FOSS default for IPolicyHitRecorder. The dashboard pack registers a real
// implementation (PolicyStackHitAtom) that overrides this; without the
// dashboard the recorder is a swallowed no-op so the gateway runs cleanly
// with no observability cost.
public sealed class NullPolicyHitRecorder : IPolicyHitRecorder
{
    public void Record(string scopeKey, PolicyIntentKind intent)
    {
        // intentionally empty
    }
}