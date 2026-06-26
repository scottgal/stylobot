using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Telemetry;

// Hot-path counter the policy dispatcher calls after picking a winning rule.
// Lives in FOSS so the dispatcher can depend on it without referencing UI; the
// dashboard's PolicyStackHitAtom implements it from the UI assembly and the
// posture view component reads the resulting snapshot. The FOSS default is a
// no-op (NullPolicyHitRecorder) so a gateway built without the dashboard pack
// keeps compiling and runs at zero cost.
public interface IPolicyHitRecorder
{
    void Record(string scopeKey, PolicyIntentKind intent);
}