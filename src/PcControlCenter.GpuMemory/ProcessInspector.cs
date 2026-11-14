using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PcControlCenter.GpuMemory;

/// <summary>What one scan of a process found: the libraries and the data files mapped into it (full paths).</summary>
public sealed record RawProcessScan(bool AccessDenied, bool Partial, IReadOnlyList<string> Libraries, IReadOnlyList<string> MappedFiles)
{
    public static RawProcessScan Denied { get; } = new(true, false, [], []);
}

/// <summary>Runtimes recognised from the file names of loaded libraries. Detection only: a loaded DLL proves nothing about GPU work.</summary>
public static partial class RuntimeLibraries
{
    /// <summary>Most specific first (that is the display order).</summary>
    private static readonly (string Tag, Regex Pattern)[] Table =
    [
        ("llama.cpp", LlamaRegex()),
        ("whisper.cpp", WhisperRegex()),
        ("ggml", GgmlRegex()),
        ("PyTorch", TorchRegex()),
        ("TensorRT", TensorRtRegex()),
        ("ONNX Runtime", OnnxRegex()),
        ("DirectML", DirectMlRegex()),
        ("OpenVINO", OpenVinoRegex()),
        ("cuDNN", CudnnRegex()),
        ("HIP", HipRegex()),
        ("CUDA", CudaRegex()),
        ("Vulkan", VulkanRegex()),
        ("D3D12", D3D12Regex()),
        ("OpenGL", OpenGlRegex()),
        ("D3D11", D3D11Regex()),
        ("NVENC", NvencRegex()),
        ("NVDEC", NvdecRegex()),
    ];

    public static readonly IReadOnlySet<string> AiRuntimes = new HashSet<string>(StringComparer.Ordinal)
    {
        "llama.cpp", "whisper.cpp", "ggml", "PyTorch", "TensorRT", "ONNX Runtime", "DirectML", "OpenVINO", "cuDNN",
    };

    public static IReadOnlyList<string> Recognize(IEnumerable<string> libraryPaths)
    {
        var names = libraryPaths.Select(p => Path.GetFileName(p.Replace('\\', '/')).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return [.. Table.Where(t => names.Any(n => t.Pattern.IsMatch(n))).Select(t => t.Tag)];
    }

    [GeneratedRegex(@"^(llama\.dll|libllama.*\.dll|llama-.*\.dll|llm_engine.*\.dll)$")] private static partial Regex LlamaRegex();
    [GeneratedRegex(@"^(lib)?whisper.*\.dll$")] private static partial Regex WhisperRegex();
    [GeneratedRegex(@"^(lib)?ggml.*\.dll$")] private static partial Regex GgmlRegex();
    [GeneratedRegex(@"^torch_(cuda.*|cpu)\.dll$")] private static partial Regex TorchRegex();
    [GeneratedRegex(@"^nvinfer.*\.dll$")] private static partial Regex TensorRtRegex();
    [GeneratedRegex(@"^onnxruntime.*\.dll$")] private static partial Regex OnnxRegex();
    [GeneratedRegex(@"^directml\.dll$")] private static partial Regex DirectMlRegex();
    [GeneratedRegex(@"^openvino.*\.dll$")] private static partial Regex OpenVinoRegex();
    [GeneratedRegex(@"^cudnn.*\.dll$")] private static partial Regex CudnnRegex();
    [GeneratedRegex(@"^amdhip64.*\.dll$")] private static partial Regex HipRegex();
    [GeneratedRegex(@"^(nvcuda|nvcuda64|cudart64_.*|cublas64_.*|cublaslt64_.*)\.dll$")] private static partial Regex CudaRegex();
    [GeneratedRegex(@"^vulkan-1\.dll$")] private static partial Regex VulkanRegex();
    [GeneratedRegex(@"^d3d12core\.dll$")] private static partial Regex D3D12Regex();
    [GeneratedRegex(@"^opengl32\.dll$")] private static partial Regex OpenGlRegex();
    [GeneratedRegex(@"^d3d11\.dll$")] private static partial Regex D3D11Regex();
    [GeneratedRegex(@"^nvencodeapi64\.dll$")] private static partial Regex NvencRegex();
    [GeneratedRegex(@"^nvcuvid(64)?\.dll$")] private static partial Regex NvdecRegex();
}

/// <summary>Which mapped data files are model files. Plausibility checks only; a mapping does not prove GPU placement.</summary>
public static class ModelFiles
{
    private const long MiB = 1L << 20;

    private static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".gguf", ".ggml", ".safetensors", ".onnx", ".pt", ".pth", ".bin", ".engine", ".plan", ".tflite", ".ckpt",
    };

    /// <summary>Also used for caches and tiles: these need 64 MiB to count as a model.</summary>
    private static readonly IReadOnlySet<string> Ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bin", ".pt", ".pth", ".ckpt" };

    /// <summary>
    /// A model file: a GGUF by its magic (any size; files without extension, such as Ollama's blobs, are checked from 64 MiB),
    /// or a model extension of at least 4 MiB (64 MiB for the ambiguous ones).
    /// </summary>
    public static (bool IsModel, bool IsGguf) Classify(string path, long size, Func<string, bool> hasGgufMagic)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            var gguf = hasGgufMagic(path);
            return (gguf, gguf);
        }

        if (ext.Length == 0)
        {
            var gguf = size >= 64 * MiB && hasGgufMagic(path);
            return (gguf, gguf);
        }

        if (!Extensions.Contains(ext))
        {
            return (false, false);
        }

        return (size >= (Ambiguous.Contains(ext) ? 64 * MiB : 4 * MiB), false);
    }

    public static string FormatOf(string path, bool gguf) => gguf ? "GGUF" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
    {
        "safetensors" => "safetensors",
        "onnx" => "ONNX",
        "engine" or "plan" => "TensorRT",
        "tflite" => "TFLite",
        "pt" or "pth" or "ckpt" => "PyTorch",
        var other => other,
    };
}

/// <summary>
/// Scans the larger GPU processes for loaded libraries and mapped model files, within a time budget per reading, and keeps
/// the results per process (PID plus start time, so a reused PID is scanned afresh). A process is scanned again when its
/// GPU memory moved by 64 MiB or 10 %, or every two minutes. The scan itself is injected (Windows: VirtualQueryEx).
/// </summary>
public sealed class ProcessInspector(
    Func<int, TimeSpan, RawProcessScan> scan,
    TimeProvider? time = null,
    Func<string, long?>? fileSize = null,
    Func<string, bool>? hasGgufMagic = null,
    Func<string, GgufSummary?>? readGguf = null)
{
    public const int Candidates = 25;
    private const int MaxModelsPerProcess = 8;
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Backstop = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PerProcessBudget = TimeSpan.FromMilliseconds(80);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, long?> _fileSize = fileSize ?? DefaultFileSize;
    private readonly Func<string, bool> _hasGgufMagic = hasGgufMagic ?? GgufHeader.IsGguf;
    private readonly Func<string, GgufSummary?> _readGguf = readGguf ?? GgufHeader.Read;
    private readonly Dictionary<ProcessKey, (ProcessDetail Detail, long MemoryAtScan)> _cache = [];

    public int Scans { get; private set; }

    /// <summary>Details for the processes of a reading. Returns cached results for processes not due (or out of budget).</summary>
    public IReadOnlyDictionary<int, ProcessDetail> Inspect(IReadOnlyList<GpuProcessMemory> processes, IReadOnlyDictionary<int, ProcessEntry> table,
        TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow();
        var memory = processes.GroupBy(p => p.ProcessId)
            .ToDictionary(g => g.Key, g => g.Sum(p => (p.DedicatedBytes ?? 0) + (p.SharedBytes ?? 0)));
        var eligible = memory.Keys
            .Where(pid => pid > 4 && table.TryGetValue(pid, out var e) && e.State is ProcessIdentityState.Verified or ProcessIdentityState.StartTimeUnavailable)
            .ToHashSet();
        var busy = processes.Where(p => p.Engines?.Busiest >= 2 && eligible.Contains(p.ProcessId)).Select(p => p.ProcessId);
        var candidates = memory.Where(m => eligible.Contains(m.Key)).OrderByDescending(m => m.Value).Take(Candidates).Select(m => m.Key)
            .Concat(busy)
            .Distinct()
            .ToList();
        var result = new Dictionary<int, ProcessDetail>();
        foreach (var pid in candidates)
        {
            var key = table[pid].Key;
            var mem = memory.GetValueOrDefault(pid);
            var cached = _cache.TryGetValue(key, out var c) ? c : ((ProcessDetail Detail, long MemoryAtScan)?)null;
            var due = cached is not { } hit || Due(hit.Detail, hit.MemoryAtScan, mem, now);
            if (due && Stopwatch.GetElapsedTime(started) < budget)
            {
                var detail = Build(scan(pid, PerProcessBudget), now);
                Scans++;
                _cache[key] = (detail, mem);
                result[pid] = detail;
            }
            else if (cached is { } previous)
            {
                result[pid] = previous.Detail;
            }
        }

        var live = table.Values.Select(e => e.Key).ToHashSet();
        foreach (var gone in _cache.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _cache.Remove(gone);
        }

        return result;
    }

    private static bool Due(ProcessDetail detail, long memoryAtScan, long memory, DateTimeOffset now)
    {
        var age = now - detail.ScannedAt;
        if (age < MinGap)
        {
            return false;
        }

        if (age >= Backstop)
        {
            return true;
        }

        return !detail.AccessDenied && Math.Abs(memory - memoryAtScan) > Math.Max(64L << 20, memoryAtScan / 10);
    }

    private ProcessDetail Build(RawProcessScan raw, DateTimeOffset now)
    {
        if (raw.AccessDenied)
        {
            return new ProcessDetail { AccessDenied = true, ScannedAt = now };
        }

        var models = new List<ModelFile>();
        foreach (var path in raw.MappedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (models.Count >= MaxModelsPerProcess || _fileSize(path) is not long size)
            {
                continue;
            }

            var (isModel, isGguf) = ModelFiles.Classify(path, size, _hasGgufMagic);
            if (!isModel)
            {
                continue;
            }

            // File name only: the full path can contain the Windows user name.
            models.Add(new ModelFile(Path.GetFileName(path.Replace('\\', '/')), size, ModelFiles.FormatOf(path, isGguf), isGguf ? _readGguf(path) : null));
        }

        return new ProcessDetail
        {
            Runtimes = RuntimeLibraries.Recognize(raw.Libraries),
            Models = [.. models.OrderByDescending(m => m.Gguf?.Kind == "LLM").ThenByDescending(m => m.SizeBytes)],
            Partial = raw.Partial,
            LoadedLibraries = raw.Libraries.Count,
            ScannedAt = now,
        };
    }

    private static long? DefaultFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
