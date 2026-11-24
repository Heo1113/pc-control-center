using LocalAiPerf.Core.Sessions;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>A labelled value with where it came from (실측 / 엔진 보고 / 계산값).</summary>
public sealed record ValueRow(string Name, string Value, string Origin = "", string Note = "");

public sealed record SensorRow(string Name, string Status, string Source, bool RequiresElevation, string Detail, bool IsOk);

public sealed record StatRow(string Metric, string Unit, string Origin, string Count, string Mean, string Min, string Max, string P95, string Change, string Slope, string Source);

public sealed record EventRow(string Time, string Severity, string Category, string Message, int Level);

public sealed record RequestDisplayRow(string Time, string Duration, string Finish, string PromptTokens, string Reused, string OutputTokens,
    string PrefillMs, string DecodeMs, string DecodeTokS, string FirstToken, bool IsError);

public sealed record SlotRow(string Slot, string State, string Held, string Fill, string TokS);

public sealed record ActivityRow(string Time, string RequestId, string Event, string Outcome);

public sealed record DegradationRow(string Metric, string Start, string Duration, string Drop, string Values, string Observations);

public sealed record ThrottleRow(string Start, string Duration, string Reason);

public sealed record ConditionRow(string Time, string Engine, string Model, string ModelFile, string Context, string Slots, string Settings);

public sealed record WarningRow(string Kind, string Message);

/// <summary>One metric across compared sessions; Cells align with the selected sessions.</summary>
public sealed record CompareRow(string Metric, string Unit, string Origin, IReadOnlyList<CompareCell> Cells, string Note);

/// <summary>Direction: 1 = better than baseline, -1 = worse, 0 = neutral or not applicable.</summary>
public sealed record CompareCell(string Value, string Delta, int Direction, bool IsBaseline);

public static class SessionText
{
    public static string State(SessionInfo s) => s.IsActive ? "기록 중" : SessionEndReasons.ToKorean(s.EndReason);

    public static string Kind(SessionInfo s) => s.Kind == SessionKind.Manual ? "수동" : "자동";
}
