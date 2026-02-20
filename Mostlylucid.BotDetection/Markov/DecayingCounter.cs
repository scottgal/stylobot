namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     A counter that decays exponentially over time.
///     Thread-safe for reads; mutations should be externally synchronized.
/// </summary>
public struct DecayingCounter
{
    public double Value;
    public DateTime LastUpdate;

    public DecayingCounter(double value, DateTime timestamp)
    {
        Value = value;
        LastUpdate = timestamp;
    }

    /// <summary>
    ///     Returns the decayed value at the given time.
    /// </summary>
    public readonly double Decayed(DateTime now, TimeSpan halfLife)
    {
        if (halfLife <= TimeSpan.Zero) return Value;
        var elapsed = (now - LastUpdate).TotalSeconds;
        if (elapsed <= 0) return Value;
        return Value * Math.Pow(2.0, -elapsed / halfLife.TotalSeconds);
    }

    /// <summary>
    ///     Increment and apply decay in one step.
    /// </summary>
    public void IncrementWithDecay(double amount, DateTime now, TimeSpan halfLife)
    {
        Value = Decayed(now, halfLife) + amount;
        LastUpdate = now;
    }
}
