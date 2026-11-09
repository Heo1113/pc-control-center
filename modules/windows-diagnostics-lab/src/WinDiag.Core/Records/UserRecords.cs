namespace WinDiag.Core.Records;

/// <summary>Kinds of changes the user records by hand (values the app cannot read, such as BIOS settings).</summary>
public static class UserRecordKinds
{
    public const string BiosUpdate = "bios_update";
    public const string BiosSetting = "bios_setting";
    public const string MemoryExpo = "memory_expo";
    public const string MemoryVoltage = "memory_voltage";
    public const string GpuDriver = "gpu_driver";
    public const string GpuSetting = "gpu_setting";
    public const string WindowsUpdate = "windows_update";
    public const string Hardware = "hardware";
    public const string Other = "other";

    public static IReadOnlyList<(string Key, string Name)> All { get; } =
    [
        (BiosUpdate, "BIOS/UEFI 업데이트"),
        (BiosSetting, "BIOS 설정 변경"),
        (MemoryExpo, "메모리 EXPO/XMP 설정"),
        (MemoryVoltage, "메모리 전압 변경"),
        (GpuDriver, "GPU 드라이버 변경"),
        (GpuSetting, "GPU 설정(클럭·전력) 변경"),
        (WindowsUpdate, "Windows 업데이트"),
        (Hardware, "하드웨어 교체·추가"),
        (Other, "기타"),
    ];

    public static string Name(string key) => All.FirstOrDefault(k => k.Key == key).Name ?? key;
}

/// <summary>A value change entered by the user. Always shown as "사용자 입력", never as a measurement.</summary>
public sealed record UserValueChange(string Name, string? Before, string? After, string? Unit = null);

public sealed record UserRecord
{
    public long Id { get; init; }
    public required DateTimeOffset Time { get; init; }
    public DateTimeOffset Created { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<UserValueChange> Values { get; init; } = [];
}

/// <summary>A point in time the user compares before/after (for example "BIOS 3.20 update").</summary>
public sealed record Baseline
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset Time { get; init; }
    public DateTimeOffset Created { get; init; }
    public string? Description { get; init; }
    public long? UserRecordId { get; init; }
}

public static class NoteTargets
{
    public const string Event = "event";
    public const string Group = "group";
    public const string Incident = "incident";
    public const string General = "general";
}

public sealed record UserNote
{
    public long Id { get; init; }
    public required string TargetKind { get; init; }
    public required string TargetKey { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
    public required string Text { get; init; }
}
