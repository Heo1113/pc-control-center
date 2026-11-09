using System.Text;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Security;

namespace WindowsAiOperator.Core.Planning;

/// <summary>What an element looked like when the model saw it. A ref resolves to this, then to a live element.</summary>
public sealed record ElementFingerprint(string RuntimeId, long WindowHandle, string? AutomationId, string Name, string ControlType, string? ClassName, string Path)
{
    public static ElementFingerprint From(ElementInfo e) => new(e.RuntimeId, e.WindowHandle, e.AutomationId, e.Name, e.ControlType, e.ClassName, e.Path);

    /// <summary>Same kind of element: control type equal and the automation id (or, without one, the name) equal.</summary>
    public bool IsCompatible(ElementInfo live)
    {
        if (!string.Equals(ControlType, live.ControlType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(AutomationId))
        {
            return string.Equals(AutomationId, live.AutomationId, StringComparison.Ordinal);
        }

        return string.Equals(Name, live.Name, StringComparison.Ordinal);
    }
}

/// <summary>Element references (e1, e2, ...) handed to the model. Numbers keep growing within a task, so an old ref
/// never silently points at a different element.</summary>
public sealed class ElementRegistry
{
    private readonly Dictionary<string, ElementFingerprint> _byRef = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _next = 1;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byRef.Count;
            }
        }
    }

    public string Register(ElementInfo element)
    {
        lock (_gate)
        {
            var reference = "e" + _next++;
            _byRef[reference] = ElementFingerprint.From(element);
            return reference;
        }
    }

    public ElementFingerprint? Get(string reference)
    {
        lock (_gate)
        {
            return _byRef.GetValueOrDefault(reference);
        }
    }
}

public sealed record FormattedSnapshot(string Text, int ElementCount, bool InjectionSuspected, IReadOnlyList<string> SuspiciousSamples);

/// <summary>
/// Compact text form of a UIA tree for a text-only model: one line per useful element with a ref, its role, name,
/// automation id, supported actions and state (UFO²-style labelled control list). Values are redacted and shortened,
/// password values are never present.
/// </summary>
public static class SnapshotFormatter
{
    public static FormattedSnapshot Format(UiSnapshot snapshot, ElementRegistry registry, int maxLines = 250, int maxValueLength = 80)
    {
        var builder = new StringBuilder();
        var suspicious = new List<string>();
        var lines = 0;
        builder.Append("window: ").Append(Redactor.OneLine(snapshot.Window.Title, 120))
               .Append(" | process: ").Append(snapshot.Window.ProcessName)
               .Append(" | handle: ").Append(snapshot.Window.Handle)
               .Append(" | structure: ").Append(snapshot.Status);
        if (!string.IsNullOrEmpty(snapshot.StatusReason))
        {
            builder.Append(" (").Append(snapshot.StatusReason).Append(')');
        }

        builder.AppendLine();
        Check(snapshot.Window.Title);

        if (snapshot.Root is not null)
        {
            Walk(snapshot.Root, 0);
        }

        if (snapshot.Truncated || lines >= maxLines)
        {
            builder.AppendLine("(트리가 길어 일부만 표시)");
        }

        return new FormattedSnapshot(builder.ToString(), lines, suspicious.Count > 0, suspicious);

        void Walk(UiNode node, int depth)
        {
            if (lines >= maxLines)
            {
                return;
            }

            var e = node.Element;
            var include = depth == 0 || ControlTypes.Interesting.Contains(e.ControlType) && (!string.IsNullOrWhiteSpace(e.Name) || !string.IsNullOrEmpty(e.AutomationId)
                          || e.Patterns != UiPatterns.None);
            if (e.IsOffscreen && depth > 0)
            {
                include = false;
            }

            var childDepth = depth;
            if (include)
            {
                var reference = registry.Register(e);
                builder.Append(' ', Math.Min(depth, 12) * 2).Append('[').Append(reference).Append("] ").Append(e.ControlType);
                if (!string.IsNullOrWhiteSpace(e.Name))
                {
                    builder.Append(" \"").Append(Redactor.OneLine(e.Name, 80)).Append('"');
                    Check(e.Name);
                }

                if (!string.IsNullOrEmpty(e.AutomationId))
                {
                    builder.Append(" id=").Append(Redactor.OneLine(e.AutomationId, 60));
                }

                var actions = Actions(e);
                if (actions.Length > 0)
                {
                    builder.Append(" {").Append(actions).Append('}');
                }

                if (!e.IsEnabled)
                {
                    builder.Append(" disabled");
                }

                if (e.HasKeyboardFocus)
                {
                    builder.Append(" focused");
                }

                if (e.Toggle is { } toggle)
                {
                    builder.Append(" checked=").Append(toggle == ToggleValue.On ? "true" : toggle == ToggleValue.Off ? "false" : "mixed");
                }

                if (e.IsSelected == true)
                {
                    builder.Append(" selected");
                }

                if (e.Expand is ExpandValue.Expanded or ExpandValue.PartiallyExpanded)
                {
                    builder.Append(" expanded");
                }

                if (e.IsPassword)
                {
                    builder.Append(" password");
                }
                else if (e.Value is not null)
                {
                    builder.Append(" value=\"").Append(Redactor.OneLine(e.Value, maxValueLength)).Append('"');
                    if (e.Value.Length > maxValueLength)
                    {
                        builder.Append($" ({e.Value.Length}자)");
                    }

                    Check(e.Value);
                }

                if (e.IsReadOnly == true)
                {
                    builder.Append(" readonly");
                }

                builder.AppendLine();
                lines++;
                childDepth = depth + 1;
            }

            foreach (var child in node.Children)
            {
                Walk(child, childDepth);
            }
        }

        void Check(string? text)
        {
            if (UntrustedText.LooksLikeInstruction(text) && suspicious.Count < 5)
            {
                suspicious.Add(Redactor.OneLine(text, 120));
            }
        }
    }

    private static string Actions(ElementInfo e)
    {
        var list = new List<string>(4);
        if (e.Supports(UiPatterns.Invoke))
        {
            list.Add("click");
        }

        if (e.Supports(UiPatterns.Value) && e.IsReadOnly != true || e.Supports(UiPatterns.Text) && e.ControlType is "Document" or "Edit")
        {
            list.Add("set_text");
        }

        if (e.Supports(UiPatterns.Toggle))
        {
            list.Add("toggle");
        }

        if (e.Supports(UiPatterns.SelectionItem))
        {
            list.Add("select");
        }

        if (e.Supports(UiPatterns.ExpandCollapse))
        {
            list.Add("expand");
        }

        if (e.Supports(UiPatterns.Scroll))
        {
            list.Add("scroll");
        }

        return string.Join(',', list);
    }
}
