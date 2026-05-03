using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Llm.Orchestration;

/// <summary>
///     Tracks LLM usage: request counts, estimated costs, and budget enforcement.
///     Thread-safe. Counters use sliding windows (hourly/daily).
/// </summary>
public sealed class LlmUsageTracker
{
    private readonly ConcurrentQueue<DateTime> _hourlyRequests = new();
    private readonly ConcurrentQueue<(DateTime Time, double Cost)> _dailyCosts = new();
    private long _totalRequests;
    private double _totalEstimatedCost;

    /// <summary>Record a completed LLM request with estimated cost.</summary>
    public void RecordRequest(string provider, int inputTokens, int outputTokens)
    {
        var now = DateTime.UtcNow;
        _hourlyRequests.Enqueue(now);
        Interlocked.Increment(ref _totalRequests);

        var cost = EstimateCost(provider, inputTokens, outputTokens);
        _dailyCosts.Enqueue((now, cost));
        Interlocked.Exchange(ref _totalEstimatedCost,
            Volatile.Read(ref _totalEstimatedCost) + cost);

        Trim();
    }

    /// <summary>Check if budget allows another request.</summary>
    public bool IsWithinBudget(LlmBudgetOptions budget)
    {
        if (budget.MaxRequestsPerHour > 0 && RequestsThisHour >= budget.MaxRequestsPerHour)
            return false;
        if (budget.MaxCostPerDay > 0 && CostToday >= budget.MaxCostPerDay)
            return false;
        return true;
    }

    /// <summary>Requests in the last hour.</summary>
    public int RequestsThisHour
    {
        get
        {
            Trim();
            return _hourlyRequests.Count;
        }
    }

    /// <summary>Estimated cost today (UTC).</summary>
    public double CostToday
    {
        get
        {
            Trim();
            return _dailyCosts.Sum(c => c.Cost);
        }
    }

    /// <summary>Total lifetime requests.</summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>Total lifetime estimated cost.</summary>
    public double TotalEstimatedCost => Volatile.Read(ref _totalEstimatedCost);

    private void Trim()
    {
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        while (_hourlyRequests.TryPeek(out var oldest) && oldest < hourAgo)
            _hourlyRequests.TryDequeue(out _);

        var todayStart = DateTime.UtcNow.Date;
        while (_dailyCosts.TryPeek(out var oldest) && oldest.Time < todayStart)
            _dailyCosts.TryDequeue(out _);
    }

    /// <summary>Rough cost estimate per provider (USD per 1M tokens, input/output averaged).</summary>
    private static double EstimateCost(string provider, int inputTokens, int outputTokens)
    {
        var rate = provider.ToLowerInvariant() switch
        {
            "openai" => 0.15,        // gpt-4o-mini: $0.15/1M input
            "anthropic" => 0.25,     // claude-haiku: $0.25/1M input
            "gemini" => 0.0,         // free tier
            "groq" => 0.0,           // free tier
            "mistral" => 0.10,       // mistral-small
            "deepseek" => 0.07,      // deepseek-chat
            "together" => 0.10,
            "fireworks" => 0.10,
            "ollama" => 0.0,         // local
            _ => 0.15                // assume OpenAI-like pricing
        };

        return (inputTokens + outputTokens) / 1_000_000.0 * rate;
    }
}
