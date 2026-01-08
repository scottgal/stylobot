using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Mostlylucid.Common.Telemetry;

/// <summary>
///     Provides a standard ActivitySource for Mostlylucid packages.
///     Activities are only recorded when listeners are registered (e.g., by OpenTelemetry).
/// </summary>
public class TelemetryActivitySource
{
    private readonly TelemetryOptions _options;

    /// <summary>
    ///     Creates a new TelemetryActivitySource
    /// </summary>
    /// <param name="name">The name of the activity source (typically the package name)</param>
    /// <param name="version">The version of the activity source</param>
    /// <param name="options">Optional telemetry options</param>
    public TelemetryActivitySource(string name, string? version = null, TelemetryOptions? options = null)
    {
        ActivitySource = new ActivitySource(name, version);
        _options = options ?? new TelemetryOptions();
    }

    /// <summary>
    ///     Gets the name of the ActivitySource
    /// </summary>
    public string Name => ActivitySource.Name;

    /// <summary>
    ///     Gets the version of the ActivitySource
    /// </summary>
    public string? Version => ActivitySource.Version;

    /// <summary>
    ///     Gets the underlying ActivitySource for advanced scenarios
    /// </summary>
    public ActivitySource ActivitySource { get; }

    /// <summary>
    ///     Starts a new activity if telemetry is enabled and there are listeners
    /// </summary>
    /// <param name="operationName">Name of the operation</param>
    /// <param name="kind">The activity kind</param>
    /// <param name="tags">Initial tags to add</param>
    /// <returns>The activity, or null if telemetry is disabled or no listeners</returns>
    public Activity? StartActivity(
        [CallerMemberName] string operationName = "",
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        if (!_options.Enabled)
            return null;

        var activity = ActivitySource.StartActivity(operationName, kind);

        if (activity != null && tags != null && _options.RecordDetailedAttributes)
            foreach (var tag in tags)
                activity.SetTag(tag.Key, tag.Value);

        return activity;
    }

    /// <summary>
    ///     Starts a new activity with parent context
    /// </summary>
    public Activity? StartActivity(
        string operationName,
        ActivityKind kind,
        ActivityContext parentContext,
        IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        if (!_options.Enabled)
            return null;

        var activity = ActivitySource.StartActivity(operationName, kind, parentContext);

        if (activity != null && tags != null && _options.RecordDetailedAttributes)
            foreach (var tag in tags)
                activity.SetTag(tag.Key, tag.Value);

        return activity;
    }

    /// <summary>
    ///     Checks if there are any listeners for this activity source
    /// </summary>
    public bool HasListeners()
    {
        return ActivitySource.HasListeners();
    }
}

/// <summary>
///     Extension methods for Activity
/// </summary>
public static class ActivityExtensions
{
    /// <summary>
    ///     Sets a tag on the activity if it's not null
    /// </summary>
    public static Activity? SetTagIfNotNull(this Activity? activity, string key, object? value)
    {
        if (activity != null && value != null) activity.SetTag(key, value);
        return activity;
    }

    /// <summary>
    ///     Records an exception on the activity
    /// </summary>
    public static Activity? RecordException(this Activity? activity, Exception exception)
    {
        if (activity == null)
            return null;

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag("exception.type", exception.GetType().FullName);
        activity.SetTag("exception.message", exception.Message);
        activity.SetTag("exception.stacktrace", exception.StackTrace);

        return activity;
    }

    /// <summary>
    ///     Sets the activity status to OK
    /// </summary>
    public static Activity? SetSuccess(this Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Ok);
        return activity;
    }

    /// <summary>
    ///     Sets the activity status to Error
    /// </summary>
    public static Activity? SetError(this Activity? activity, string? description = null)
    {
        activity?.SetStatus(ActivityStatusCode.Error, description);
        return activity;
    }
}