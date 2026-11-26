using Iron.ProcessIdentity;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.GpuMemory;
using Iron.Stage2.HwInfo;

namespace Iron.Stage2;

/// <summary>
/// Builds one <see cref="Stage2Frame"/> from the newest reading of each source. Pure (no I/O): tested on Linux with
/// hand-made readings. Rules kept from Stage 1 and RC3: no value is invented (missing = null = N/A), NVML and the WDDM
/// counters are never mixed, HWiNFO only fills what NVML/Windows cannot read, a process's command line is only ever the
/// redacted one from the identity module.
/// </summary>
public static class Composer
{
    private const double Gb = 1024.0 * 1024 * 1024;

    public static Stage2Frame Compose(DateTimeOffset now, HardwareSnapshot? hardware, bool gpuReaderAvailable, GpuMemorySnapshot? gpuMemory,
        HwInfoValues? hwinfo, double? diskReadBytesPerSec, Func<int, ProcessIdentityInfo?> identityOf, IReadOnlyList<SourceStatus> sources)
    {
        var phase = hardware is null ? Stage2Phase.Initializing : gpuReaderAvailable && hardware.Gpus.Count > 0 ? Stage2Phase.Ready : Stage2Phase.NoDevice;
        var gpus = hardware?.Gpus.OrderBy(g => g.Index).Select(g => new Stage2Gpu(
            g.Index,
            ShortGpuName(g.Name) ?? $"GPU {g.Index}",
            g.TemperatureC,
            g.PowerW,
            g.UtilizationPercent,
            g.MemoryUsedBytes is long used ? Math.Round(used / Gb, 2) : null,
            g.MemoryTotalBytes is long total ? Math.Round(total / Gb, 1) : null,
            g.GraphicsClockMhz)).ToList() ?? [];
        var system = hardware?.System;
        return new Stage2Frame
        {
            Timestamp = now,
            Phase = phase,
            Gpus = gpus,
            Cpu = new Stage2Cpu(system?.CpuUtilizationPercent, hwinfo?.CpuTctlC, null, null),
            RamUsedGb = system?.MemoryUsedBytes is long ru ? Math.Round(ru / Gb, 1) : null,
            RamTotalGb = system?.MemoryTotalBytes is long rt ? Math.Round(rt / Gb, 0) : null,
            SsdTemperatureC = hwinfo?.NvmeMaxTempC,
            SsdReadMBps = diskReadBytesPerSec is double b ? Math.Round(b / (1024 * 1024), 1) : null,
            WireView = hwinfo?.WireView ?? Stage2WireView.None,
            Processes = gpuMemory is null ? [] : Processes(gpuMemory, identityOf),
            Sources = sources,
        };
    }

    /// <summary>"NVIDIA GeForce RTX 5080" → "RTX 5080" (the card label of Stage 1).</summary>
    public static string? ShortGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var n = name.Trim();
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) " })
        {
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return n[prefix.Length..];
            }
        }

        return n;
    }

    /// <summary>
    /// One row per process (Stage 1's list is keyed by PID): the adapter where it holds the most dedicated memory. Values are
    /// that adapter's WDDM counters only (never summed with NVML). Command line: the identity module's redacted text only.
    /// </summary>
    internal static List<Stage2Process> Processes(GpuMemorySnapshot s, Func<int, ProcessIdentityInfo?> identityOf)
    {
        var adapters = s.Adapters.Where(a => a.Info is not null).ToDictionary(a => a.Luid, a => a.Info!);
        var rows = new List<Stage2Process>();
        foreach (var group in s.Processes.Where(GpuMemoryRules.IsListed).GroupBy(p => p.ProcessId))
        {
            var p = group.OrderByDescending(x => x.DedicatedBytes ?? 0).ThenByDescending(x => x.SharedBytes ?? 0).First();
            adapters.TryGetValue(p.Luid, out var info);
            var name = s.ProcessTable.TryGetValue(p.ProcessId, out var entry) ? entry.Name : "PID " + p.ProcessId;
            var image = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PID ", StringComparison.Ordinal) ? name : name + ".exe";
            var (util, engine) = TopEngine(p.Engines);
            var identity = identityOf(p.ProcessId);
            rows.Add(new Stage2Process(
                p.ProcessId,
                identity?.ImageName ?? image,
                ShortGpuName(info?.Name) ?? GpuLuid.Format(p.Luid),
                Math.Round((p.DedicatedBytes ?? 0) / Gb, 2),
                Math.Round((p.SharedBytes ?? 0) / Gb, 2),
                util,
                engine,
                identity?.RedactedCommandLine ?? "",
                info?.IsSoftware ?? false,
                identity));
        }

        return rows;
    }

    private static (double Util, string Engine) TopEngine(EngineUsage? e)
    {
        if (e is null)
        {
            return (0, "");
        }

        var all = new[] { ("3D", e.ThreeD), ("Compute", e.Compute), ("Copy", e.Copy), ("Video", e.Video), ("기타", e.Other) };
        var top = all.MaxBy(x => x.Item2);
        return (Math.Round(top.Item2, 1), top.Item2 > 0 ? top.Item1 : "");
    }
}
