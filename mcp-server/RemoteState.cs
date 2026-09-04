namespace OnAirMcp;

/// <summary>
/// Mirrors OnAirNative.Services.RemoteState (OnAirNative/Services/RemoteControlService.cs) —
/// the authoritative definition lives there. Keep this record in sync whenever a field is added
/// on the onAIr side.
/// </summary>
public sealed record RemoteState(
    bool TpOpen,
    bool TpLocked,
    bool TpHiddenInShare,
    bool ControllerHiddenInShare,
    bool Recording,
    string ChatProvider,
    bool WhisperLocalLoaded,
    string WhisperModelStatus,
    double Opacity,
    int FontSize,
    int ScrollSpeed,
    int VoiceScrollSpeed,
    int ScrollStep,
    double VoiceThreshold,
    string ScrollMode,
    string FontFamily,
    string LoadedScriptName,
    string LastQuestion,
    string LastAnswer,
    int QaTurnCount,
    string PacingSummary,
    string PacingLevel,
    List<string> FollowUpSuggestions,
    bool QaSessionActive,
    string InsightText,
    // ── AI Insights window's 4 independent section show/hide toggles — mirror
    // OnAirNative.Services.RemoteState's fields of the same name exactly. Read via
    // onair_get_state, flipped via the onair_toggle_insights_show_* tools (see OnAirTools.cs).
    bool ShowFollowUpsInInsights,
    bool ShowExternalInsightsInInsights,
    bool ShowPacingInInsights,
    bool ShowTokenUsageInInsights,
    bool InsightsOpen,
    bool InsightsLocked,
    bool InsightsHiddenInShare,
    int InsightFontSize,
    double InsightOpacity,
    string InsightFontFamily);
