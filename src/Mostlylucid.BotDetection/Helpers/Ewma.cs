namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Exponential-moving-average update used wherever we blend a new observation into
///     a running value. Convention: <c>alpha</c> is the weight on the NEW
///     observation, so the previous value carries (1 - alpha). Higher alpha = faster
///     reaction to the latest observation; alpha = 0 freezes, alpha = 1 replaces.
/// </summary>
public static class Ewma
{
    /// <summary>new = (1 - alpha) * previous + alpha * observation</summary>
    public static double Update(double previous, double observation, double alpha)
        => (1.0 - alpha) * previous + alpha * observation;
}
