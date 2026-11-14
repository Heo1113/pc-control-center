using System.Text;

namespace PcControlCenter.GpuMemory;

/// <summary>
/// Reads the header of a GGUF model file from disk: metadata keys and the tensor table, never the weights. Tensor sizes
/// come from the offsets in the table, so new quantization types need no lookup table. Bounded: a damaged or hostile file
/// gives an error summary, never an exception or an unbounded allocation. Results are cached by path, size and time.
/// </summary>
public static class GgufHeader
{
    public const long MaxHeaderBytes = 256L << 20;
    private const ulong MaxEntries = 2_000_000;
    private const ulong MaxStringBytes = 16 << 20;
    private const int KeptStringChars = 512;
    private const ulong KeptArrayElements = 4096;

    /// <summary>Arrays of arrays are allowed by the format but unused; deeper nesting is treated as damage (no unbounded recursion).</summary>
    private const int MaxArrayDepth = 4;
    private static readonly byte[] Magic = "GGUF"u8.ToArray();
    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<(string Path, long Size, DateTime Time), GgufSummary> Cache = [];
    private static readonly Queue<(string, long, DateTime)> CacheOrder = new();

    /// <summary>llama.cpp's llama_ftype values (general.file_type).</summary>
    private static readonly Dictionary<long, string> FileTypes = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [7] = "Q8_0", [8] = "Q5_0", [9] = "Q5_1", [10] = "Q2_K",
        [11] = "Q3_K_S", [12] = "Q3_K_M", [13] = "Q3_K_L", [14] = "Q4_K_S", [15] = "Q4_K_M", [16] = "Q5_K_S", [17] = "Q5_K_M",
        [18] = "Q6_K", [19] = "IQ2_XXS", [20] = "IQ2_XS", [21] = "Q2_K_S", [22] = "IQ3_XS", [23] = "IQ3_XXS", [24] = "IQ1_S",
        [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M", [28] = "IQ2_S", [29] = "IQ2_M", [30] = "IQ4_XS", [31] = "IQ1_M",
        [32] = "BF16", [36] = "TQ1_0", [37] = "TQ2_0", [38] = "MXFP4_MOE",
    };

    /// <summary>True when the file starts with the GGUF magic (reads four bytes).</summary>
    public static bool IsGguf(string path)
    {
        try
        {
            using var stream = Open(path);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.SequenceEqual(Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The header summary of a file (cached), or null when the file cannot be opened.</summary>
    public static GgufSummary? Read(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var key = (Path.GetFullPath(path), info.Length, info.LastWriteTimeUtc);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        GgufSummary summary;
        try
        {
            using var stream = Open(path);
            summary = Parse(stream, info.Length, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (CacheGate)
        {
            if (Cache.TryAdd(key, summary))
            {
                CacheOrder.Enqueue(key);
                while (CacheOrder.Count > 64)
                {
                    Cache.Remove(CacheOrder.Dequeue());
                }
            }
        }

        return summary;
    }

    /// <summary>Parses a GGUF header from a stream positioned at the start of the file. Never throws for malformed data.</summary>
    public static GgufSummary Parse(Stream stream, long fileSize, string fileName)
    {
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        var tensors = new List<(ulong Offset, string Name, ulong Elements)>();
        long dataStart;
        try
        {
            using var reader = new BinaryReader(new BufferedStream(stream, 1 << 20), Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic))
            {
                return Failed("GGUF 파일이 아닙니다.");
            }

            var version = reader.ReadUInt32();
            if (version is 0 or > 3)
            {
                return Failed($"알 수 없는 GGUF 버전 {version}");
            }

            var wide = version >= 2;
            ulong Count() => wide ? reader.ReadUInt64() : reader.ReadUInt32();
            var tensorCount = Count();
            var kvCount = Count();
            if (tensorCount > MaxEntries || kvCount > MaxEntries)
            {
                return Failed("헤더의 항목 수가 비정상적으로 큽니다.");
            }

            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(reader, Count, keep: true) ?? "";
                var type = reader.ReadUInt32();
                var value = ReadValue(reader, type, Count, depth: 0);
                if (value is not null)
                {
                    metadata[key] = value;
                }

                CheckBudget(reader);
            }

            for (ulong i = 0; i < tensorCount; i++)
            {
                var name = ReadString(reader, Count, keep: true) ?? "";
                var dims = reader.ReadUInt32();
                if (dims > 8)
                {
                    return Failed("텐서 차원 수가 비정상적입니다.");
                }

                ulong elements = 1;
                for (var d = 0; d < dims; d++)
                {
                    elements = checked(elements * (wide ? reader.ReadUInt64() : reader.ReadUInt32()));
                }

                reader.ReadUInt32(); // ggml type: sizes come from the offsets
                tensors.Add((reader.ReadUInt64(), name, elements));
                CheckBudget(reader);
            }

            var alignment = metadata.TryGetValue("general.alignment", out var a) && ToLong(a) is long al and > 0 and <= 1 << 20 ? al : 32;
            var position = reader.BaseStream.Position;
            dataStart = (position + alignment - 1) / alignment * alignment;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OverflowException or InvalidDataException or DecoderFallbackException)
        {
            return Failed(ex is InvalidDataException ? ex.Message : "헤더가 잘렸거나 손상되었습니다.");
        }

        return Summarize(metadata, tensors, fileSize - dataStart, fileName);

        GgufSummary Failed(string error) => new() { Kind = "model", Name = fileName, Error = error };
    }

    private static GgufSummary Summarize(Dictionary<string, object> metadata, List<(ulong Offset, string Name, ulong Elements)> tensors, long dataBytes, string fileName)
    {
        var arch = Str(metadata, "general.architecture");
        object? Meta(string key) => arch is null ? null : metadata.GetValueOrDefault($"{arch}.{key}");
        long tensorBytes = 0, outputBytes = 0, tokenEmbdBytes = 0, parameters = 0;
        var layerBytes = new Dictionary<int, long>();
        var hasOutputWeight = false;
        tensors.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        var data = Math.Max(0, dataBytes);
        long Within(ulong offset) => offset > (ulong)data ? data : (long)offset; // offsets past the end of the file count as empty
        for (var i = 0; i < tensors.Count; i++)
        {
            var (offset, name, elements) = tensors[i];
            var end = i + 1 < tensors.Count ? Within(tensors[i + 1].Offset) : data;
            var bytes = Math.Max(0, end - Within(offset));
            tensorBytes += bytes;
            parameters = elements >= (ulong)(long.MaxValue - parameters) ? long.MaxValue : parameters + (long)elements;
            if (name.StartsWith("blk.", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4, Math.Max(0, name.IndexOf('.', 4) - 4)), out var layer))
            {
                layerBytes[layer] = layerBytes.GetValueOrDefault(layer) + bytes;
            }
            else if (name.StartsWith("token_embd.", StringComparison.Ordinal))
            {
                tokenEmbdBytes += bytes;
            }
            else if (name.StartsWith("output", StringComparison.Ordinal))
            {
                outputBytes += bytes;
                hasOutputWeight |= name == "output.weight";
            }
        }

        if (!hasOutputWeight && layerBytes.Count > 0)
        {
            outputBytes += tokenEmbdBytes; // tied embeddings: llama.cpp keeps a copy for the output on the GPU
        }

        var layers = (int)(ToLong(Meta("block_count")) ?? 0);
        var hybrid = arch is not null && metadata.Keys.Any(k => k.StartsWith($"{arch}.ssm.", StringComparison.Ordinal)
                                                               || k.StartsWith($"{arch}.wkv.", StringComparison.Ordinal)
                                                               || k.StartsWith($"{arch}.shortconv.", StringComparison.Ordinal)
                                                               || k == $"{arch}.full_attention_interval");
        var latent = Meta("attention.kv_lora_rank") is not null;
        var kvUnknown = hybrid ? "순환·선형 층이 섞인 구조" : latent ? "잠재 주의(MLA) 구조" : null;
        var causal = Meta("attention.causal") is not false;
        var type = Str(metadata, "general.type");
        string kind;
        if (arch == "clip" || type == "mmproj")
        {
            kind = "vision projector";
        }
        else if (type == "adapter")
        {
            kind = "LoRA adapter";
        }
        else if (Meta("pooling_type") is { } pooling)
        {
            kind = ToLong(pooling) == 4 ? "reranker" : "embedding";
        }
        else if (layers > 0 && Meta("context_length") is not null && causal)
        {
            kind = "LLM";
        }
        else
        {
            kind = "model";
        }

        var gpuWeights = layerBytes.Count == 0 ? tensorBytes : layerBytes.Values.Sum() + outputBytes;
        var fileType = ToLong(metadata.GetValueOrDefault("general.file_type"));
        return new GgufSummary
        {
            Name = Str(metadata, "general.name") ?? Str(metadata, "general.basename") ?? fileName,
            Architecture = arch,
            Kind = kind,
            Parameters = parameters,
            Quantization = fileType is long ft ? FileTypes.GetValueOrDefault(ft, $"type{ft}") : null,
            Layers = layers,
            TrainedContext = ToLong(Meta("context_length")),
            SlidingWindow = (int?)ToLong(Meta("attention.sliding_window")),
            Experts = (int?)ToLong(Meta("expert_count")),
            TensorBytes = tensorBytes,
            GpuWeightBytesAllLayers = gpuWeights,
            // Only attention layers hold a KV cache; with recurrent or linear layers the header alone does not say how many.
            KvBytesPerTokenF16 = kind == "LLM" && kvUnknown is null ? KvBytesPerToken(layers, Meta("attention.head_count"), Meta("attention.head_count_kv"),
                Meta("embedding_length"), Meta("attention.key_length"), Meta("attention.value_length")) : null,
            IsCausal = causal,
            KvUnknownReason = kind == "LLM" ? kvUnknown : null,
        };
    }

    /// <summary>
    /// KV bytes per token with f16 keys and values: sum over layers of KV heads × (key length + value length) × 2 bytes.
    /// Assumes ordinary attention in every layer (models with sliding windows, latent or recurrent attention use less).
    /// </summary>
    public static long? KvBytesPerToken(int layers, object? headCount, object? headCountKv, object? embedding, object? keyLength, object? valueLength)
    {
        if (layers is <= 0 or > 100_000)
        {
            return null;
        }

        long? heads = headCount is long[] hs ? (hs.Length > 0 ? hs.Max() : null) : ToLong(headCount);
        var kvPerLayer = headCountKv switch
        {
            long[] list => list.Take(layers).ToArray(),
            null => heads is long h ? Enumerable.Repeat(h, layers).ToArray() : null,
            _ => ToLong(headCountKv) is long kv ? Enumerable.Repeat(kv, layers).ToArray() : null,
        };
        var headDim = ToLong(embedding) is long e && heads is long hd && hd > 0 ? e / hd : (long?)null;
        var k = ToLong(keyLength) ?? headDim;
        var v = ToLong(valueLength) ?? headDim;
        // Implausible values (a damaged header) give no estimate rather than an overflowed one.
        if (kvPerLayer is null || kvPerLayer.Length == 0 || kvPerLayer.Any(n => n is < 0 or > 1 << 16)
            || k is not long kl || v is not long vl || kl is <= 0 or > 1 << 20 || vl is <= 0 or > 1 << 20)
        {
            return null;
        }

        return kvPerLayer.Sum(n => n * (kl + vl)) * 2;
    }

    private static object? ReadValue(BinaryReader reader, uint type, Func<ulong> count, int depth) => type switch
    {
        0 => (long)reader.ReadByte(),
        1 => (long)reader.ReadSByte(),
        2 => (long)reader.ReadUInt16(),
        3 => (long)reader.ReadInt16(),
        4 => (long)reader.ReadUInt32(),
        5 => (long)reader.ReadInt32(),
        6 => (double)reader.ReadSingle(),
        7 => reader.ReadByte() != 0,
        8 => ReadString(reader, count, keep: true),
        9 => ReadArray(reader, count, depth + 1),
        10 => (long)Math.Min(reader.ReadUInt64(), long.MaxValue),
        11 => reader.ReadInt64(),
        12 => reader.ReadDouble(),
        _ => throw new InvalidDataException($"알 수 없는 GGUF 값 형식 {type}"),
    };

    /// <summary>Small integer arrays are kept (per-layer head counts); everything else is skipped without allocating.</summary>
    private static object? ReadArray(BinaryReader reader, Func<ulong> count, int depth)
    {
        if (depth > MaxArrayDepth)
        {
            throw new InvalidDataException("배열이 너무 깊게 중첩되어 있습니다.");
        }

        var elementType = reader.ReadUInt32();
        var n = count();
        if (n > MaxEntries * 64)
        {
            throw new InvalidDataException("배열 길이가 비정상적으로 큽니다.");
        }

        var size = elementType switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
        if (size > 0)
        {
            if (n <= KeptArrayElements && elementType is not (6 or 12 or 7))
            {
                var values = new long[n];
                for (ulong i = 0; i < n; i++)
                {
                    values[i] = (long)ReadValue(reader, elementType, count, depth)!;
                }

                return values;
            }

            Skip(reader, (long)n * size);
            return null;
        }

        for (ulong i = 0; i < n; i++)
        {
            if (elementType == 8)
            {
                ReadString(reader, count, keep: false);
            }
            else
            {
                ReadValue(reader, elementType, count, depth);
            }

            CheckBudget(reader);
        }

        return null;
    }

    private static string? ReadString(BinaryReader reader, Func<ulong> count, bool keep)
    {
        var length = count();
        if (length > MaxStringBytes)
        {
            throw new InvalidDataException("문자열 길이가 비정상적으로 큽니다.");
        }

        if (!keep || length > KeptStringChars * 4)
        {
            Skip(reader, (long)length);
            return null;
        }

        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void Skip(BinaryReader reader, long bytes)
    {
        if (bytes < 0 || reader.BaseStream.Position + bytes > reader.BaseStream.Length)
        {
            throw new EndOfStreamException();
        }

        reader.BaseStream.Seek(bytes, SeekOrigin.Current);
    }

    private static void CheckBudget(BinaryReader reader)
    {
        if (reader.BaseStream.Position > MaxHeaderBytes)
        {
            throw new InvalidDataException("헤더가 너무 큽니다.");
        }
    }

    private static string? Str(Dictionary<string, object> metadata, string key) => metadata.GetValueOrDefault(key) as string;

    private static long? ToLong(object? value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)d,
        bool b => b ? 1 : 0,
        _ => null,
    };

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
}
