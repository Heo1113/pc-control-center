using System.Globalization;
using WinDiag.Core.Snapshots;
using WinDiag.Storage;

namespace WinDiag.Analysis;

/// <summary>
/// Compares configuration snapshots. A part that could not be read in either snapshot is not compared (it is not a
/// change). Changes found between two consecutive snapshots are reported with the time window they happened in.
/// </summary>
public static class SnapshotDiff
{
    public static IReadOnlyList<ConfigChange> Compare(SystemSnapshot before, SystemSnapshot after)
    {
        var changes = new List<ConfigChange>();
        if (before.Os is { } os1 && after.Os is { } os2)
        {
            Field(changes, "Windows", "제품", os1.DisplayName, os2.DisplayName);
            Field(changes, "Windows", "버전", os1.DisplayVersion, os2.DisplayVersion);
            Field(changes, "Windows", "빌드", os1.BuildText, os2.BuildText);
            Field(changes, "Windows", "에디션", os1.EditionId, os2.EditionId);
        }

        if (before.Bios is { } b1 && after.Bios is { } b2)
        {
            Field(changes, "BIOS/UEFI", "제조사", b1.Vendor, b2.Vendor);
            Field(changes, "BIOS/UEFI", "버전", b1.Version, b2.Version);
            Field(changes, "BIOS/UEFI", "날짜", b1.ReleaseDate, b2.ReleaseDate);
            Field(changes, "메인보드", "모델", Join(b1.BoardManufacturer, b1.BoardProduct), Join(b2.BoardManufacturer, b2.BoardProduct));
            Field(changes, "메인보드", "리비전", b1.BoardVersion, b2.BoardVersion);
        }

        if (before.Cpu is { } c1 && after.Cpu is { } c2)
        {
            Field(changes, "CPU", "이름", c1.Name, c2.Name);
            Field(changes, "CPU", "논리 프로세서", c1.LogicalProcessors.ToString(CultureInfo.InvariantCulture), c2.LogicalProcessors.ToString(CultureInfo.InvariantCulture));
        }

        if (before.Memory is { } m1 && after.Memory is { } m2)
        {
            Field(changes, "메모리", "전체 용량", Gib(m1.TotalBytes), Gib(m2.TotalBytes));
            if (m1.Modules.Count > 0 && m2.Modules.Count > 0)
            {
                Keyed(changes, "메모리 모듈", m1.Modules, m2.Modules, m => m.Slot ?? m.Bank ?? "?", ModuleText);
            }
        }

        if (before.Gpus.Count > 0 && after.Gpus.Count > 0)
        {
            Keyed(changes, "GPU", before.Gpus, after.Gpus, g => g.Name ?? "?", g => Join(g.DriverVersion, g.DriverDate is null ? null : $"({g.DriverDate})"));
        }

        Field(changes, "GPU 드라이버", "NVIDIA (NVML 보고)", before.NvidiaDriverVersion, after.NvidiaDriverVersion);
        if (before.Disks.Count > 0 && after.Disks.Count > 0)
        {
            Keyed(changes, "저장장치", before.Disks, after.Disks, d => d.Model ?? "?",
                d => Join(d.FirmwareRevision is null ? null : $"펌웨어 {d.FirmwareRevision}", Gb(d.SizeBytes), d.Status));
        }

        if (before.Drivers.Count > 0 && after.Drivers.Count > 0)
        {
            Keyed(changes, "드라이버", before.Drivers, after.Drivers, d => $"{d.DeviceName} [{d.DeviceClass}]",
                d => Join(d.DriverVersion, d.DriverDate is null ? null : $"({d.DriverDate})"));
        }

        if (before.Hotfixes.Count > 0 && after.Hotfixes.Count > 0)
        {
            var b = before.Hotfixes.Select(h => h.HotFixId).Where(h => h is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var a = after.Hotfixes.Select(h => h.HotFixId).Where(h => h is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var added in a.Except(b, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new ConfigChange("업데이트", added!, null, "설치됨", ChangeKind.Added));
            }

            foreach (var removed in b.Except(a, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new ConfigChange("업데이트", removed!, "설치됨", null, ChangeKind.Removed));
            }
        }

        return changes;
    }

    /// <summary>Changes between each pair of consecutive snapshots (mock and real snapshots are never compared with each other).</summary>
    public static IReadOnlyList<DetectedChange> Detect(IReadOnlyList<StoredSnapshot> snapshots)
    {
        var result = new List<DetectedChange>();
        foreach (var series in snapshots.GroupBy(s => s.IsMock))
        {
            var ordered = series.OrderBy(s => s.FirstSeen).ThenBy(s => s.Id).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1];
                var current = ordered[i];
                var changes = Compare(SystemSnapshot.FromJson(previous.Json), SystemSnapshot.FromJson(current.Json));
                if (changes.Count > 0)
                {
                    result.Add(new DetectedChange(previous.LastConfirmed, current.FirstSeen, previous.Id, current.Id, changes, series.Key));
                }
            }
        }

        return result.OrderBy(c => c.WindowTo).ToList();
    }

    private static void Field(List<ConfigChange> changes, string area, string item, string? before, string? after)
    {
        if (before is null || after is null)
        {
            return; // not readable in one of the snapshots: not a known change
        }

        if (!string.Equals(before.Trim(), after.Trim(), StringComparison.Ordinal))
        {
            changes.Add(new ConfigChange(area, item, before, after, ChangeKind.Changed));
        }
    }

    private static void Keyed<T>(List<ConfigChange> changes, string area, IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> key, Func<T, string?> text)
    {
        var b = before.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(text)), StringComparer.OrdinalIgnoreCase);
        var a = after.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(text)), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, value) in a.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!b.TryGetValue(k, out var old))
            {
                changes.Add(new ConfigChange(area, k, null, value, ChangeKind.Added));
            }
            else if (!string.Equals(old, value, StringComparison.Ordinal))
            {
                changes.Add(new ConfigChange(area, k, old, value, ChangeKind.Changed));
            }
        }

        foreach (var (k, value) in b.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!a.ContainsKey(k))
            {
                changes.Add(new ConfigChange(area, k, value, null, ChangeKind.Removed));
            }
        }
    }

    private static string ModuleText(MemoryModuleInfo m) => Join(
        Gib(m.CapacityBytes),
        m.ConfiguredSpeedMts is int cs ? $"{cs} MT/s(구성)" : m.SpeedMts is int s ? $"{s} MT/s" : null,
        m.ConfiguredVoltageMv is int mv ? string.Create(CultureInfo.InvariantCulture, $"{mv / 1000.0:0.###} V(SMBIOS 보고)") : null,
        m.PartNumber) ?? "?";

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return present.Count == 0 ? null : string.Join(" ", present);
    }

    private static string? Gib(long? bytes) => bytes is long b ? (b / 1024.0 / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " GiB" : null;

    private static string? Gb(long? bytes) => bytes is long b ? (b / 1e9).ToString("0", CultureInfo.InvariantCulture) + " GB" : null;
}
