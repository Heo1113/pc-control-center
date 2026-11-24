using WindowsAiOperator.Core.Automation;

namespace WindowsAiOperator.Core.Actions;

/// <summary>Which window a step targets. App ids come from the allow list; handles come from the window list given to the model.</summary>
public sealed record WindowSelector
{
    public string? App { get; init; }
    public string? TitleContains { get; init; }
    public long? Handle { get; init; }

    public bool IsEmpty => App is null && TitleContains is null && Handle is null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (App is not null)
        {
            parts.Add($"앱 {App}");
        }

        if (TitleContains is not null)
        {
            parts.Add($"제목에 '{TitleContains}' 포함");
        }

        if (Handle is not null)
        {
            parts.Add($"창 {Handle}");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>Which element a step targets. <see cref="Ref"/> points at an element of an earlier snapshot (e12);
/// the executor re-finds it live and never reuses stale coordinates.</summary>
public sealed record ElementSelector
{
    public string? Ref { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? NameContains { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public int? Index { get; init; }

    public bool IsEmpty => Ref is null && AutomationId is null && Name is null && NameContains is null && ControlType is null && ClassName is null;

    public ElementQuery ToQuery(int maxResults = 20) => new()
    {
        AutomationId = AutomationId,
        Name = Name,
        NameContains = NameContains,
        ControlType = ControlType,
        ClassName = ClassName,
        MaxResults = maxResults,
    };

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ref is not null)
        {
            parts.Add(Ref);
        }

        if (ControlType is not null)
        {
            parts.Add(ControlType);
        }

        if (AutomationId is not null)
        {
            parts.Add($"#{AutomationId}");
        }

        if (Name is not null)
        {
            parts.Add($"'{Name}'");
        }

        if (NameContains is not null)
        {
            parts.Add($"~'{NameContains}'");
        }

        if (ClassName is not null)
        {
            parts.Add($"class={ClassName}");
        }

        if (Index is not null)
        {
            parts.Add($"[{Index}]");
        }

        return string.Join(' ', parts);
    }
}

/// <summary>What must be true after a step. The executor checks it against the live UI.</summary>
public sealed record StepExpectation
{
    public string? ValueEquals { get; init; }
    public string? ValueContains { get; init; }
    public string? NameContains { get; init; }
    public bool? Checked { get; init; }
    public bool? Selected { get; init; }
    public bool? Exists { get; init; }
    public bool? Gone { get; init; }
    public string? WindowTitleContains { get; init; }

    public bool IsEmpty => ValueEquals is null && ValueContains is null && NameContains is null && Checked is null && Selected is null
                           && Exists is null && Gone is null && WindowTitleContains is null;
}

public enum SetTextMode
{
    Replace,
    Append,
}

/// <summary>A point in 0..1000 coordinates relative to a window screenshot (the UI-TARS convention).</summary>
public readonly record struct NormalizedPoint(int X, int Y);

public sealed record PlanStep
{
    public required int Id { get; init; }
    public required ActionType Action { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public WindowSelector? Window { get; init; }
    public ElementSelector? Element { get; init; }
    public string? Text { get; init; }
    public SetTextMode Mode { get; init; } = SetTextMode.Replace;
    public bool? Checked { get; init; }
    public IReadOnlyList<string>? MenuPath { get; init; }
    public ScrollDirection? Direction { get; init; }
    public int Amount { get; init; } = 1;
    public Hotkey? Keys { get; init; }
    public NormalizedPoint? Point { get; init; }
    public double? Confidence { get; init; }
    public string? Path { get; init; }
    public int? TimeoutMs { get; init; }
    public int? DelayMs { get; init; }
    public string? Message { get; init; }
    public StepExpectation? Expect { get; init; }

    public string Summary()
    {
        var what = Action.DisplayName();
        var detail = Action switch
        {
            ActionType.LaunchApp => App,
            ActionType.SetText => $"{Element} ← \"{Security.Redactor.OneLine(Text, 40)}\"{(Mode == SetTextMode.Append ? " (덧붙임)" : string.Empty)}",
            ActionType.Toggle => $"{Element} → {(Checked == true ? "선택" : "해제")}",
            ActionType.SelectMenu => string.Join(" › ", MenuPath ?? []),
            ActionType.Scroll => $"{Direction} ×{Amount}",
            ActionType.Hotkey => Keys?.ToString(),
            ActionType.ClickPoint => Point is { } p ? $"({p.X},{p.Y})/1000" : null,
            ActionType.ListDirectory => Path,
            ActionType.Wait => $"{DelayMs} ms",
            ActionType.AskUser or ActionType.Finish => Security.Redactor.OneLine(Message, 60),
            _ => Element?.ToString() ?? Window?.ToString(),
        };
        var text = string.IsNullOrWhiteSpace(detail) ? what : $"{what}: {detail}";
        return string.IsNullOrWhiteSpace(Description) ? text : $"{text} — {Security.Redactor.OneLine(Description, 80)}";
    }
}

public enum PlanStatus
{
    Ok,
    NeedUserInput,
    CannotDo,
}

public sealed record TaskPlan
{
    public string? Summary { get; init; }
    public string? TargetApp { get; init; }
    public PlanStatus Status { get; init; } = PlanStatus.Ok;
    public string? Message { get; init; }
    public required IReadOnlyList<PlanStep> Steps { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PlanIssue(int? StepNumber, string? Field, string Message)
{
    public override string ToString()
    {
        var where = StepNumber is null ? string.Empty : $"{StepNumber}번째 단계";
        if (Field is not null)
        {
            where = string.IsNullOrEmpty(where) ? $"'{Field}'" : $"{where} '{Field}'";
        }

        return string.IsNullOrEmpty(where) ? Message : $"{where}: {Message}";
    }
}

public sealed record PlanParseResult(TaskPlan? Plan, IReadOnlyList<PlanIssue> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Plan is not null && Errors.Count == 0;
}

public sealed record PlanParserOptions
{
    public int MaxSteps { get; init; } = 30;
    public int MaxTextLength { get; init; } = 2000;
    public bool AllowClickPoint { get; init; }
    public int MaxInputLength { get; init; } = 64 * 1024;
}
