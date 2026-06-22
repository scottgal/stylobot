namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     One row fed into the drift-metrics computation in
///     <c>IdentityWeightCalibrationService.RunOnceAsync</c>. The calibration
///     pass groups these by <c>(ArchetypeId, UaFamily)</c> and folds the L2
///     distances against each archetype's centroid into mean / variance / p90
///     statistics. See <see cref="ArchetypeDriftMetric"/>.
/// </summary>
/// <param name="ArchetypeId">The fingerprint's <c>InferredClientType</c> at observation time. Empty when the fingerprint has not yet matched an archetype.</param>
/// <param name="UaFamily">UA family captured on the observation row. Empty string when null.</param>
/// <param name="ObservationVector">The raw observation vector. Length matches the layout dimension.</param>
public readonly record struct DriftObservationRow(
    string ArchetypeId,
    string UaFamily,
    float[] ObservationVector);
