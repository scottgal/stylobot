namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     A read-only projection of the canonical classification thresholds
///     (<c>BotDetection:Classification</c>) for the dashboard "Apply-policy" control, which needs the
///     plain-language score / confidence cuts to render its guidance. Every value is sourced from the
///     single <c>ClassificationOptions</c> ("the only place these numbers live"); nothing here is
///     computed independently, so the control cannot disagree with detection.
///
///     <para>
///     Carries the <c>IConfiguration</c> keys alongside the values so the commercial edit control can
///     target the exact override key without hardcoding it. <paramref name="CanEdit"/> rides the same
///     read-only gate as the policy rows (FOSS ships read-only by construction).
///     </para>
/// </summary>
/// <param name="BotFloor"><c>bot_probability &gt;= BotFloor</c> ⇒ Bot (also the binary "is a bot" cut).</param>
/// <param name="HumanCeiling"><c>bot_probability &lt; HumanCeiling</c> ⇒ Human.</param>
/// <param name="MinActionConfidence">Act on a verdict only when its confidence is <c>&gt;=</c> this (enforcement is a fast-follow; the value is read now).</param>
/// <param name="BotFloorConfigKey">The <c>IConfiguration</c> key for <paramref name="BotFloor"/>.</param>
/// <param name="HumanCeilingConfigKey">The <c>IConfiguration</c> key for <paramref name="HumanCeiling"/>.</param>
/// <param name="MinActionConfidenceConfigKey">The <c>IConfiguration</c> key for <paramref name="MinActionConfidence"/>.</param>
/// <param name="CanEdit">Whether the caller may edit these thresholds (FOSS: always <c>false</c>).</param>
public sealed record ClassificationThresholdRow(
    double BotFloor,
    double HumanCeiling,
    double MinActionConfidence,
    string BotFloorConfigKey,
    string HumanCeilingConfigKey,
    string MinActionConfidenceConfigKey,
    bool CanEdit);
