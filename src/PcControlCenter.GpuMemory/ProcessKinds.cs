namespace PcControlCenter.GpuMemory;

/// <summary>What a GPU process most likely is. A classification from evidence (libraries, model files, engines, names), not a measurement.</summary>
public enum ProcessKind
{
    Llm,
    Ai,
    Compute,
    Graphics3D,
    Video,
    Browser,
    Compositor,
    System,
    Kernel,
    App,
}

/// <summary>The kind of a process and the evidence it rests on.</summary>
public sealed record KindResult(ProcessKind Kind, string Basis);

public static class ProcessKinds
{
    /// <summary>
    /// Strata's engine executable: "strata.exe" on Windows, "strata" elsewhere (Strata's strata_control/shape_engine.py).
    /// Its tools (strata-gguf, strata-plan, ...) are not the engine.
    /// </summary>
    public static bool IsStrataEngineName(string name)
        => name.Equals("strata.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("strata", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] LlmNames = ["llama-", "llama.", "ollama", "lm studio", "lmstudio", "llmster", "koboldcpp", "jan.", "gpt4all", "llamafile", "strata"];

    private static readonly IReadOnlySet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe", "chrome.exe", "firefox.exe", "msedgewebview2.exe", "brave.exe", "opera.exe", "arc.exe", "vivaldi.exe", "whale.exe",
    };

    private static readonly IReadOnlySet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "winlogon.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe", "system",
    };

    public static KindResult Classify(int pid, string name, ProcessDetail? detail, EngineUsage? engines, bool isStrataProcess)
    {
        var lower = name.ToLowerInvariant();
        var runtimes = detail?.Runtimes ?? [];
        if (pid == 4)
        {
            return new(ProcessKind.Kernel, "PID 4 (System)");
        }

        if (lower == "dwm.exe")
        {
            return new(ProcessKind.Compositor, "dwm.exe");
        }

        if (SystemNames.Contains(lower))
        {
            return new(ProcessKind.System, name);
        }

        if (isStrataProcess)
        {
            return new(ProcessKind.Llm, "Strata 서버 포트와 프로세스 트리");
        }

        if (detail?.Models.FirstOrDefault(m => m.Gguf?.Kind == "LLM") is { } llm)
        {
            return new(ProcessKind.Llm, $"LLM 모델 파일 매핑 ({llm.FileName})");
        }

        if (runtimes.Contains("llama.cpp"))
        {
            return new(ProcessKind.Llm, "llama.cpp 라이브러리");
        }

        if (LlmNames.Any(n => lower.StartsWith(n, StringComparison.Ordinal)))
        {
            return new(ProcessKind.Llm, "프로세스 이름");
        }

        var video = engines?.Video ?? 0;
        var ai = runtimes.FirstOrDefault(RuntimeLibraries.AiRuntimes.Contains);
        if (ai is not null && video <= 1)
        {
            return new(ProcessKind.Ai, $"{ai} 라이브러리");
        }

        if (detail?.Models.Count > 0 && video <= 1)
        {
            return new(ProcessKind.Ai, $"모델 파일 매핑 ({detail.Models[0].FileName})");
        }

        if (Browsers.Contains(lower))
        {
            return new(ProcessKind.Browser, name);
        }

        if (video > 1 || runtimes.Contains("NVENC"))
        {
            return new(ProcessKind.Video, video > 1 ? "영상 엔진 사용" : "NVENC 라이브러리");
        }

        if (runtimes.Contains("CUDA") || runtimes.Contains("HIP"))
        {
            return new(ProcessKind.Compute, runtimes.Contains("CUDA") ? "CUDA 라이브러리" : "HIP 라이브러리");
        }

        if ((runtimes.Contains("D3D12") || runtimes.Contains("Vulkan")) && (engines?.ThreeD ?? 0) > 10)
        {
            return new(ProcessKind.Graphics3D, "D3D12/Vulkan과 3D 엔진 사용");
        }

        return new(ProcessKind.App, detail is null ? "추가 정보 없음" : "AI·연산 근거 없음");
    }
}

/// <summary>
/// An estimate of how an LLM process's local memory splits, from the model file header and the context size, assuming
/// llama.cpp's placement with every layer on the GPU. Only the process total is a measurement. When the header's weights
/// are larger than that total, not every layer can be on the GPU and which part is what is unknown: no split is made
/// (<see cref="FitsAllLayers"/> false, <see cref="KvCache"/> and <see cref="Rest"/> null) and the two numbers are shown apart.
/// </summary>
public sealed record LlmMemoryEstimate(long Total, long WeightsAllLayers, long? KvCache, long? Rest, bool KvIsUpperBound)
{
    public bool FitsAllLayers => WeightsAllLayers <= Total;

    public static LlmMemoryEstimate? From(GgufSummary model, long? processDedicated, long? contextTokens)
    {
        if (processDedicated is not long total || total <= 0 || model.Error is not null || model.GpuWeightBytesAllLayers <= 0)
        {
            return null;
        }

        var weights = model.GpuWeightBytesAllLayers;
        if (weights > total)
        {
            return new LlmMemoryEstimate(total, weights, null, null, false);
        }

        long? kv = contextTokens is long ctx && ctx > 0 && model.KvBytesPerTokenF16 is long perToken ? ctx * perToken : null;
        var rest = total - weights;
        if (kv is long k && k > rest)
        {
            kv = null; // does not fit what is left: no split is shown rather than a wrong one
        }

        return new LlmMemoryEstimate(total, weights, kv, rest - (kv ?? 0), model.SlidingWindow is > 0);
    }
}
