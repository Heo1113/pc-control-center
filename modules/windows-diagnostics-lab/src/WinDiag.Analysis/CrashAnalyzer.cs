using System.Globalization;
using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Storage;

namespace WinDiag.Analysis;

/// <summary>
/// Groups the records Windows writes at the boot after an unexpected restart (Kernel-Power 41, EventLog 6008,
/// BugCheck 1001, fatal WHEA 18, crash dump records) into one incident, and adds what is known about the moments
/// before: the last time the system was seen alive, events shortly before, sensor values and dump header metadata.
/// The incident lists facts; it does not name a cause.
/// </summary>
public static class CrashAnalyzer
{
    private static readonly TimeSpan SeedWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BeforeWindow = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<CrashIncident> Analyze(DiagnosticsStore store, AnalysisRange range, TimeSpan? sensorWindow = null)
    {
        // Seeds are logged at the next boot, so look a little past the range end as well.
        var seeds = store.QueryEvents(new EventQuery
        {
            From = range.From,
            To = range.To,
            Categories = [EventCategories.UnexpectedShutdown, EventCategories.BugCheck, EventCategories.CrashDump],
            Limit = 5000,
            Descending = false,
        }).ToList();
        seeds.AddRange(store.QueryEvents(new EventQuery
        {
            From = range.From, To = range.To, Providers = [EventCatalog.WheaLogger], EventId = 18, Limit = 1000, Descending = false,
        }));
        seeds = seeds.DistinctBy(e => e.Id).OrderBy(e => e.TimeCreated).ThenBy(e => e.Id).ToList();
        if (seeds.Count == 0)
        {
            return [];
        }

        var clusters = new List<List<StoredEvent>>();
        foreach (var e in seeds)
        {
            // A dump record alone (for example a successful volmgr 162 after a manual dump) does not make an incident.
            if (clusters.Count > 0 && e.TimeCreated - clusters[^1][0].TimeCreated <= SeedWindow)
            {
                clusters[^1].Add(e);
            }
            else
            {
                clusters.Add([e]);
            }
        }

        clusters = clusters.Where(c => c.Any(e => e.Category is EventCategories.UnexpectedShutdown or EventCategories.BugCheck || e.EventId == 18)).ToList();
        var dumps = store.ListDumpFiles();
        var incidents = new List<CrashIncident>();
        foreach (var cluster in clusters)
        {
            incidents.Add(Build(store, cluster, dumps, sensorWindow ?? TimeSpan.FromMinutes(10)));
        }

        return incidents.Select(i => i with
        {
            SameCodeCount = i.BugCheckCode is uint code ? incidents.Count(x => x.BugCheckCode == code) : incidents.Count(x => x.Kind == i.Kind),
        }).ToList();
    }

    private static CrashIncident Build(DiagnosticsStore store, List<StoredEvent> cluster, IReadOnlyList<DumpFileInfo> dumps, TimeSpan sensorWindow)
    {
        var first = cluster[0];
        var notes = new List<string>();
        var boot = store.QueryEvents(new EventQuery
        {
            From = first.TimeCreated - SeedWindow,
            To = first.TimeCreated + TimeSpan.FromMinutes(1),
            Categories = [EventCategories.Boot],
            Limit = 20,
        }).Where(e => e.Provider == EventCatalog.EventLogProvider && e.EventId == 6005 || e.Provider == EventCatalog.KernelGeneral && e.EventId == 12)
            .OrderBy(e => Math.Abs((e.TimeCreated - first.TimeCreated).Ticks)).FirstOrDefault();
        var bootTime = boot?.TimeCreated;

        uint? code = null;
        IReadOnlyList<string> parameters = [];
        string? dumpPath = null;
        var bugcheck = cluster.FirstOrDefault(e => e.Category == EventCategories.BugCheck);
        if (bugcheck is not null && EventInterpreters.TryParseBugCheckText(bugcheck.Field("param1|0"), out var parsed, out var ps))
        {
            code = parsed;
            parameters = ps;
            dumpPath = bugcheck.Field("param2|1");
        }

        var kernelPower = cluster.FirstOrDefault(e => e.Provider == EventCatalog.KernelPower && e.EventId == 41);
        var powerButton = false;
        if (kernelPower is not null)
        {
            if (BugCheckCatalog.TryParseCode(kernelPower.Get("BugcheckCode"), decimalByDefault: true, out var kpCode) && kpCode != 0)
            {
                if (code is uint known && known != kpCode)
                {
                    notes.Add($"BugCheck 기록(0x{known:X8})과 Kernel-Power 41의 BugcheckCode(0x{kpCode:X8})가 다릅니다. 두 값을 모두 표시합니다.");
                }

                code ??= kpCode;
                if (parameters.Count == 0)
                {
                    parameters = Enumerable.Range(1, 4).Select(i => kernelPower.Get($"BugcheckParameter{i}")).OfType<string>().ToList();
                }
            }

            var pb = kernelPower.Get("PowerButtonTimestamp");
            powerButton = pb is not null && pb.Trim() is not ("0" or "0x0" or "");
        }

        var kind = code is uint c && c != 0 ? CrashKind.BugCheck : powerButton ? CrashKind.PowerButton : CrashKind.NoBugCheckInfo;

        // The dump file Windows says it wrote, matched by name, and its header time if it could be read.
        DumpFileInfo? dump = null;
        if (dumpPath is not null)
        {
            var name = Path.GetFileName(dumpPath.Replace('\\', Path.DirectorySeparatorChar));
            dump = dumps.FirstOrDefault(d => string.Equals(d.FileName, name, StringComparison.OrdinalIgnoreCase));
            if (dump is null)
            {
                notes.Add("Windows가 기록한 덤프 파일을 현재 찾을 수 없거나 읽을 권한이 없습니다 (정리됐거나 관리자 권한 필요).");
            }
        }
        else if (bootTime is { } b)
        {
            dump = dumps.FirstOrDefault(d => d.DumpTime is { } t && t <= b && t >= b - TimeSpan.FromHours(6));
        }

        // Last known alive: the last sensor sample or the last recorded event before the boot.
        var anchor = bootTime ?? first.TimeCreated;
        DateTimeOffset? lastAlive = null;
        string? lastAliveSource = null;
        var lastSensor = store.QuerySensors(anchor - TimeSpan.FromDays(2), anchor - TimeSpan.FromSeconds(1), maxPoints: 1_000_000).System.LastOrDefault();
        if (lastSensor is not null)
        {
            lastAlive = DateTimeOffset.FromUnixTimeMilliseconds(lastSensor.TimeMs);
            lastAliveSource = "이 앱의 마지막 센서 기록";
        }

        var lastEvent = store.QueryEvents(new EventQuery { From = anchor - TimeSpan.FromDays(2), To = anchor - TimeSpan.FromSeconds(30), Limit = 1 })
            .FirstOrDefault(e => !cluster.Contains(e));
        if (lastEvent is not null && (lastAlive is null || lastEvent.TimeCreated > lastAlive))
        {
            lastAlive = lastEvent.TimeCreated;
            lastAliveSource = $"직전에 기록된 이벤트 ({lastEvent.Provider} {lastEvent.EventId})";
        }

        // Crash time: the dump header when it is plausible, otherwise unknown (between last alive and boot).
        DateTimeOffset? crashTime = null;
        var crashTimeSource = "알 수 없음 (마지막 확인 시각과 재부팅 사이)";
        if (dump?.DumpTime is { } dumpTime && dumpTime <= anchor && (lastAlive is null || dumpTime >= lastAlive.Value - TimeSpan.FromMinutes(5)))
        {
            crashTime = dumpTime;
            crashTimeSource = "덤프 파일 헤더의 시각";
        }
        else if (cluster.FirstOrDefault(e => e.Provider == EventCatalog.EventLogProvider && e.EventId == 6008) is { } unexpected
                 && TryParse6008(unexpected, anchor, out var reported))
        {
            crashTime = reported;
            crashTimeSource = "EventLog 6008에 기록된 이전 종료 시각 (현지 시각 해석)";
        }

        var reference = crashTime ?? lastAlive ?? anchor;
        var before = store.QueryEvents(new EventQuery
        {
            From = reference - BeforeWindow,
            To = reference + TimeSpan.FromSeconds(30),
            MinSeverity = DiagSeverity.Warning,
            Limit = 200,
            Descending = false,
        }).Where(e => !cluster.Any(s => s.Id == e.Id)
                      && e.Category != EventCategories.Boot
                      && e.Severity >= (e.Category == EventCategories.Other ? DiagSeverity.Error : DiagSeverity.Warning))
            .ToList();

        EventSensorContext? sensors = null;
        if (lastSensor is not null && reference - DateTimeOffset.FromUnixTimeMilliseconds(lastSensor.TimeMs) <= TimeSpan.FromMinutes(30))
        {
            sensors = SensorContext.Build(store, reference, sensorWindow);
        }
        else
        {
            notes.Add("문제 직전 시각에 이 앱의 센서 기록이 없습니다 (앱이 실행 중이 아니었음). 하드웨어 상태는 확인할 수 없습니다.");
        }

        if (kind == CrashKind.NoBugCheckInfo)
        {
            notes.Add("BugCheck 정보가 없습니다. 전원 차단, 시스템 완전 정지 후 강제 재시작 등에서 이렇게 기록될 수 있으며, 이것만으로 파워서플라이 고장이 입증되지는 않습니다.");
        }

        notes.Add(BugCheckCatalog.Disclaimer);
        return new CrashIncident
        {
            Id = $"crash-{first.TimeCreated.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-{first.Id.ToString(CultureInfo.InvariantCulture)}",
            DetectedAt = first.TimeCreated,
            BootTime = bootTime,
            EstimatedCrashTime = crashTime,
            CrashTimeSource = crashTimeSource,
            LastKnownAlive = lastAlive,
            LastKnownAliveSource = lastAliveSource,
            Kind = kind,
            BugCheckCode = code is 0 ? null : code,
            BugCheckParameters = parameters,
            DumpPathRecorded = dumpPath,
            Dump = dump,
            Evidence = cluster,
            EventsBefore = before,
            Sensors = sensors,
            IsVirtual = cluster.All(e => e.IsVirtual),
            Notes = notes,
        };
    }

    /// <summary>
    /// EventLog 6008 stores the previous shutdown time as localized text ("오후 10:41:10", "2026-10-30"). It is used only
    /// when it parses unambiguously in the current or invariant culture and lies within two days before the boot.
    /// </summary>
    internal static bool TryParse6008(StoredEvent e, DateTimeOffset boot, out DateTimeOffset time)
    {
        time = default;
        var timeText = Clean(e.GetAt(0));
        var dateText = Clean(e.GetAt(1));
        if (timeText is null || dateText is null)
        {
            return false;
        }

        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.GetCultureInfo("ko-KR"), CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse($"{dateText} {timeText}", culture, DateTimeStyles.AssumeLocal, out var local))
            {
                var candidate = new DateTimeOffset(local);
                if (candidate <= boot && boot - candidate <= TimeSpan.FromDays(2))
                {
                    time = candidate.ToUniversalTime();
                    return true;
                }
            }
        }

        return false;
    }

    private static string? Clean(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : new string(text.Where(ch => ch is not ('‎' or '‏')).ToArray()).Trim();
}
