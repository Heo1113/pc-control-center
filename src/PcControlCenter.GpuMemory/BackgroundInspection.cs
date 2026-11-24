using LocalAiPerf.Core.Hardware;

namespace PcControlCenter.GpuMemory;

/// <summary>
/// Runs the process inspection (address-space scans, file lookups, GGUF headers) on a worker of its own, so the GPU counters
/// are never held up by it: a slow disk, a large model header or a stuck file only delays the details. A reading starts an
/// inspection when none is running and takes the newest finished result, keeping only details whose process (PID and start
/// time) is still the one in this reading. Called from the reader's thread only; one inspection runs at a time.
/// </summary>
public sealed class BackgroundInspection(
    Func<IReadOnlyList<GpuProcessMemory>, IReadOnlyDictionary<int, ProcessEntry>, IReadOnlyDictionary<int, ProcessDetail>> inspect,
    TimeProvider? time = null)
{
    /// <summary>An inspection running longer than this is reported (it cannot be cancelled while a file read blocks).</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(15);

    private const string Source = "프로세스 검사";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyDictionary<int, ProcessDetail> _latest = new Dictionary<int, ProcessDetail>();
    private Task? _running;
    private DateTimeOffset _startedAt;

    /// <summary>The inspection in progress, if any (tests wait on it).</summary>
    internal Task? Running => _running;

    /// <summary>
    /// Starts an inspection of this reading's processes unless one is running, and returns the newest finished details that
    /// still belong to the same processes. A failed or overdue inspection adds an issue; the memory reading is unaffected.
    /// </summary>
    public IReadOnlyDictionary<int, ProcessDetail> Next(IReadOnlyList<GpuProcessMemory> processes, IReadOnlyDictionary<int, ProcessEntry> table,
        ICollection<SensorIssue> issues)
    {
        if (_running is { IsCompleted: true } done)
        {
            if (done.Exception?.GetBaseException() is { } error)
            {
                issues.Add(new SensorIssue(Source, "modules", SensorStatus.Error, $"불러온 라이브러리·모델 파일을 확인하지 못했습니다: {error.Message}"));
            }

            _running = null;
        }

        if (_running is null)
        {
            _startedAt = _time.GetUtcNow();
            _running = Task.Run(() => Volatile.Write(ref _latest, inspect(processes, table)));
        }
        else if (_time.GetUtcNow() - _startedAt is var age && age > StuckAfter)
        {
            issues.Add(new SensorIssue(Source, "modules", SensorStatus.Error,
                $"프로세스 검사가 {age.TotalSeconds:0}초째 끝나지 않았습니다 (느린 디스크나 네트워크 파일). 마지막으로 확인한 내용을 보여 줍니다."));
        }

        var latest = Volatile.Read(ref _latest);
        var result = new Dictionary<int, ProcessDetail>();
        foreach (var (pid, detail) in latest)
        {
            if (table.TryGetValue(pid, out var entry) && detail.Key is { } key && key == entry.Key)
            {
                result[pid] = detail;
            }
        }

        return result;
    }
}
