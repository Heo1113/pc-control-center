using System.Security.Cryptography;
using System.Text;

namespace LocalAiPerf.Core.Runtime;

/// <summary>
/// The runtime facts that define a measurement condition (engine, version, model, context, slots, settings).
/// Two sessions with different conditions can still be compared, but the comparison must say so.
/// </summary>
public sealed record RuntimeConditions
{
    public string? RuntimeKind { get; init; }
    public string? Service { get; init; }
    public string? EngineVersion { get; init; }
    public string? InstanceId { get; init; }
    public string? ModelId { get; init; }
    public string? ModelFileName { get; init; }
    public long? ContextTokens { get; init; }
    public int? Slots { get; init; }
    public IReadOnlyList<RuntimeSetting> Settings { get; init; } = [];

    public static RuntimeConditions? FromSnapshot(RuntimeSnapshot snapshot)
    {
        if (!snapshot.IsReachable)
        {
            return null;
        }

        return new RuntimeConditions
        {
            RuntimeKind = snapshot.RuntimeKind,
            Service = snapshot.Server?.Service,
            EngineVersion = snapshot.Server?.EngineVersion,
            InstanceId = snapshot.Server?.InstanceId,
            ModelId = snapshot.Model?.Id,
            ModelFileName = snapshot.Model?.ModelFileName,
            ContextTokens = snapshot.Model?.ContextTokens,
            Slots = snapshot.Work?.SlotsEffective,
            Settings = snapshot.Settings,
        };
    }

    /// <summary>Stable digest of the condition fields (instance id excluded: a restart alone is not a new condition).</summary>
    public string Fingerprint
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(RuntimeKind).Append('|').Append(Service).Append('|').Append(EngineVersion).Append('|')
              .Append(ModelId).Append('|').Append(ModelFileName).Append('|').Append(ContextTokens).Append('|').Append(Slots);
            foreach (var setting in Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(setting.Key).Append('=').Append(setting.Value);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexStringLower(hash.AsSpan(0, 8));
        }
    }

    /// <summary>
    /// True when a field that was known before now has a different known value.
    /// A value that was unknown and became known (for example the engine version after authentication) is not a change.
    /// Settings count only when both sides reported settings.
    /// </summary>
    public static bool IsMaterialChange(RuntimeConditions? previous, RuntimeConditions? current)
    {
        if (previous is null || current is null)
        {
            return false;
        }

        static bool Changed<T>(T? a, T? b) => a is not null && b is not null && !EqualityComparer<T>.Default.Equals(a, b);

        return Changed(previous.RuntimeKind, current.RuntimeKind)
            || Changed(previous.EngineVersion, current.EngineVersion)
            || Changed(previous.ModelId, current.ModelId)
            || Changed(previous.ModelFileName, current.ModelFileName)
            || Changed(previous.ContextTokens, current.ContextTokens)
            || Changed(previous.Slots, current.Slots)
            || (previous.Settings.Count > 0 && current.Settings.Count > 0 && !SameSettings(previous.Settings, current.Settings));
    }

    private static bool SameSettings(IReadOnlyList<RuntimeSetting> a, IReadOnlyList<RuntimeSetting> b)
        => a.OrderBy(s => s.Key, StringComparer.Ordinal).SequenceEqual(b.OrderBy(s => s.Key, StringComparer.Ordinal));

    /// <summary>
    /// Human readable list of differing fields, for comparison warnings. With <paramref name="includeUnknown"/> false,
    /// a field that is unknown on one side is not reported as a difference.
    /// </summary>
    public static IReadOnlyList<string> Describe(RuntimeConditions? a, RuntimeConditions? b, bool includeUnknown = false)
    {
        var differences = new List<string>();
        if (a is null || b is null)
        {
            return differences;
        }

        void Check(string label, object? x, object? y)
        {
            if (!includeUnknown && (x is null || y is null))
            {
                return;
            }

            if (!Equals(x, y))
            {
                differences.Add($"{label}: {x ?? "알 수 없음"} → {y ?? "알 수 없음"}");
            }
        }

        Check("AI 엔진", a.RuntimeKind, b.RuntimeKind);
        Check("엔진 버전", a.EngineVersion, b.EngineVersion);
        Check("모델", a.ModelId, b.ModelId);
        Check("모델 파일", a.ModelFileName, b.ModelFileName);
        Check("컨텍스트", a.ContextTokens, b.ContextTokens);
        Check("병렬 슬롯", a.Slots, b.Slots);
        if (!includeUnknown && (a.Settings.Count == 0 || b.Settings.Count == 0))
        {
            return differences;
        }

        var aSettings = a.Settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        var bSettings = b.Settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        foreach (var key in aSettings.Keys.Union(bSettings.Keys).Order(StringComparer.Ordinal))
        {
            aSettings.TryGetValue(key, out var x);
            bSettings.TryGetValue(key, out var y);
            Check($"설정 {key}", x, y);
        }

        return differences;
    }
}
