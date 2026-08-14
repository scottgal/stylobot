namespace Mostlylucid.BotDetection.Policies.Rules;

public enum PolicyIntentKind
{
    Block,
    Challenge,
    Throttle,
    Allow,
    Tag,
    Observe,
    Lockdown,
    Redirect,
    RouteSwap,
}