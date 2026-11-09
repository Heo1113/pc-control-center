namespace WinDiag.Core.Events;

/// <summary>A labelled value read from an event (a fact: it is in the event data, not inferred).</summary>
public sealed record EventFact(string Label, string Value, string? Note = null);

/// <summary>What an interpreter adds to a rule for one concrete event.</summary>
public sealed record Interpretation
{
    public static Interpretation None { get; } = new();

    public string? TitleDetail { get; init; }
    public IReadOnlyList<EventFact> Facts { get; init; } = [];
    public string? Category { get; init; }
    public DiagSeverity? Severity { get; init; }
    public string? Signature { get; init; }
    public string? Meaning { get; init; }
    public string? Caution { get; init; }
}

/// <summary>
/// How one provider's events (all IDs, or specific IDs, optionally limited to channels) are classified.
/// A rule never matches on the event ID alone: the provider name is always part of the match.
/// </summary>
public sealed record EventRule
{
    public required string Provider { get; init; }

    /// <summary>Null: the provider default rule (used when no rule for the specific ID exists).</summary>
    public IReadOnlyList<int>? EventIds { get; init; }

    /// <summary>Null: any channel.</summary>
    public IReadOnlyList<string>? Channels { get; init; }

    public required string Category { get; init; }

    /// <summary>Null: derived from the Windows level of the event.</summary>
    public DiagSeverity? Severity { get; init; }

    /// <summary>Also collect informational (level 0/4) events matching this rule; otherwise only critical/error/warning.</summary>
    public bool CollectInformational { get; init; }

    /// <summary>
    /// A rule makes its provider's warning-level events collected too. False for rules that only explain error events of
    /// providers whose warnings are mostly noise (TLS, shadow copies, search indexer...).
    /// </summary>
    public bool CollectWarnings { get; init; } = true;

    public required string Title { get; init; }
    public string? Meaning { get; init; }
    public string? Caution { get; init; }

    /// <summary>Data fields ("Name" or "Name|position") that identify "the same problem" for grouping.</summary>
    public IReadOnlyList<string> SignatureFields { get; init; } = [];

    public Func<IEventFields, Interpretation>? Interpret { get; init; }

    public bool Matches(IEventFields e)
        => e.IsProvider(Provider)
           && (EventIds is null || EventIds.Contains(e.EventId))
           && (Channels is null || Channels.Any(c => string.Equals(c, e.Channel, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>The classification of one event.</summary>
public sealed record EventClassification(
    string Category,
    DiagSeverity Severity,
    string Title,
    string? Meaning,
    string? Caution,
    string Signature,
    string GroupKey,
    IReadOnlyList<EventFact> Facts,
    bool IsCatalogued);
