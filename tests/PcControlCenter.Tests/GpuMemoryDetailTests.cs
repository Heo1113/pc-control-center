using System.Text;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.GpuMemory;

namespace PcControlCenter.Tests;

/// <summary>Writes small GGUF files (metadata and a tensor table, a few bytes of data) for the parser tests.</summary>
internal sealed class GgufWriter
{
    private readonly List<(string Key, uint Type, Action<BinaryWriter> Value)> _metadata = [];
    private readonly List<(string Name, ulong[] Dims, ulong Bytes, ulong? Offset)> _tensors = [];

    public uint Version { get; init; } = 3;

    /// <summary>Length of metadata plus tensor table (where the padding before the tensor data starts).</summary>
    public long HeaderLength { get; private set; }

    public GgufWriter Text(string key, string value) => Add(key, 8, w => WriteString(w, value));

    public GgufWriter U32(string key, uint value) => Add(key, 4, w => w.Write(value));

    public GgufWriter Bool(string key, bool value) => Add(key, 7, w => w.Write(value));

    public GgufWriter I32s(string key, params int[] values) => Add(key, 9, w =>
    {
        w.Write(5u);
        WriteCount(w, (ulong)values.Length);
        foreach (var v in values)
        {
            w.Write(v);
        }
    });

    public GgufWriter Texts(string key, params string[] values) => Add(key, 9, w =>
    {
        w.Write(8u);
        WriteCount(w, (ulong)values.Length);
        foreach (var v in values)
        {
            WriteString(w, v);
        }
    });

    public GgufWriter F32s(string key, int count) => Add(key, 9, w =>
    {
        w.Write(6u);
        WriteCount(w, (ulong)count);
        for (var i = 0; i < count; i++)
        {
            w.Write(0.5f);
        }
    });

    /// <summary>A tensor of <paramref name="bytes"/> bytes (a multiple of 32, so no padding between tensors).</summary>
    public GgufWriter Tensor(string name, ulong bytes, params ulong[] dims)
    {
        _tensors.Add((name, dims, bytes, null));
        return this;
    }

    /// <summary>A tensor whose table entry claims <paramref name="offset"/> (a damaged file).</summary>
    public GgufWriter TensorAt(string name, ulong offset, params ulong[] dims)
    {
        _tensors.Add((name, dims, 0, offset));
        return this;
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("GGUF"u8);
            w.Write(Version);
            WriteCount(w, (ulong)_tensors.Count);
            WriteCount(w, (ulong)_metadata.Count);
            foreach (var (key, type, value) in _metadata)
            {
                WriteString(w, key);
                w.Write(type);
                value(w);
            }

            ulong running = 0;
            foreach (var (name, dims, bytes, offset) in _tensors)
            {
                WriteString(w, name);
                w.Write((uint)dims.Length);
                foreach (var d in dims)
                {
                    WriteCount(w, d);
                }

                w.Write(0u); // ggml type: the parser takes sizes from the offsets
                w.Write(offset ?? running);
                running += bytes;
            }

            w.Flush();
            HeaderLength = stream.Position;
            while (stream.Position % 32 != 0)
            {
                w.Write((byte)0);
            }

            w.Write(new byte[running]);
        }

        return stream.ToArray();
    }

    private GgufWriter Add(string key, uint type, Action<BinaryWriter> value)
    {
        _metadata.Add((key, type, value));
        return this;
    }

    private void WriteCount(BinaryWriter w, ulong n)
    {
        if (Version >= 2)
        {
            w.Write(n);
        }
        else
        {
            w.Write((uint)n);
        }
    }

    private void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteCount(w, (ulong)bytes.Length);
        w.Write(bytes);
    }
}

internal static class GgufFixtures
{
    /// <summary>
    /// The attention shape of Qwen3-0.6B (28 layers, 16 query and 8 KV heads, head size 128 although 1024 / 16 = 64) with
    /// a handful of tiny tensors. KV per token at f16: 28 × 8 × (128 + 128) × 2 = 114,688 bytes.
    /// </summary>
    public static GgufWriter Qwen3(uint version = 3, bool tiedEmbeddings = false)
    {
        var writer = new GgufWriter { Version = version }
            .Text("general.architecture", "qwen3")
            .Text("general.name", "Qwen3 0.6B")
            .U32("general.file_type", 7)
            .U32("general.alignment", 32)
            .U32("qwen3.block_count", 28)
            .U32("qwen3.context_length", 40960)
            .U32("qwen3.embedding_length", 1024)
            .U32("qwen3.attention.head_count", 16)
            .U32("qwen3.attention.head_count_kv", 8)
            .U32("qwen3.attention.key_length", 128)
            .U32("qwen3.attention.value_length", 128)
            .Texts("tokenizer.ggml.tokens", "a", "b", "c")
            .F32s("tokenizer.ggml.scores", 3)
            .Tensor("token_embd.weight", 4096, 1024, 151936)
            .Tensor("blk.0.attn_q.weight", 2048, 1024, 2048)
            .Tensor("blk.1.attn_q.weight", 2048, 1024, 2048)
            .Tensor("output_norm.weight", 64, 1024);
        return tiedEmbeddings ? writer : writer.Tensor("output.weight", 4096, 1024, 151936);
    }

    public static GgufSummary Parse(byte[] bytes, int? length = null)
    {
        var n = length ?? bytes.Length;
        return GgufHeader.Parse(new MemoryStream(bytes, 0, n, writable: false), n, "test.gguf");
    }
}

public class GgufHeaderTests
{
    [Theory]
    [InlineData(3u)]
    [InlineData(2u)]
    [InlineData(1u)]
    public void The_header_gives_the_model_shape_and_the_kv_size_per_token(uint version)
    {
        var model = GgufFixtures.Parse(GgufFixtures.Qwen3(version).Build());

        Assert.Null(model.Error);
        Assert.Equal("LLM", model.Kind);
        Assert.Equal("Qwen3 0.6B", model.Name);
        Assert.Equal("qwen3", model.Architecture);
        Assert.Equal("Q8_0", model.Quantization);
        Assert.Equal(28, model.Layers);
        Assert.Equal(40960, model.TrainedContext);
        Assert.Equal(114_688, model.KvBytesPerTokenF16); // the key length (128) wins over embedding / heads (64)
        Assert.Equal(896L << 20, model.KvBytesPerTokenF16 * 8192);
        Assert.Equal(2L * 1024 * 151936 + 2 * 1024 * 2048 + 1024, model.Parameters);
        Assert.Equal(12_352, model.TensorBytes);
        // On the GPU with every layer offloaded: the blocks and the output; the token embeddings stay in RAM.
        Assert.Equal(4096 + 64 + 4096, model.GpuWeightBytesAllLayers);
    }

    [Fact]
    public void Tied_embeddings_count_once_more_on_the_gpu_for_the_output()
    {
        var model = GgufFixtures.Parse(GgufFixtures.Qwen3(tiedEmbeddings: true).Build());
        Assert.Equal(8256, model.TensorBytes);
        Assert.Equal(4096 + 64 + 4096, model.GpuWeightBytesAllLayers);
    }

    [Fact]
    public void Other_model_kinds_are_named_and_get_no_kv_estimate()
    {
        static GgufSummary Of(GgufWriter writer) => GgufFixtures.Parse(writer.Tensor("blk.0.w", 32, 8).Build());

        Assert.Equal("embedding", Of(new GgufWriter().Text("general.architecture", "bert").U32("bert.block_count", 12)
            .U32("bert.context_length", 512).U32("bert.pooling_type", 1)).Kind);
        Assert.Equal("reranker", Of(new GgufWriter().Text("general.architecture", "bert").U32("bert.block_count", 12)
            .U32("bert.context_length", 512).U32("bert.pooling_type", 4)).Kind);
        Assert.Equal("vision projector", Of(new GgufWriter().Text("general.architecture", "clip")).Kind);
        Assert.Equal("LoRA adapter", Of(new GgufWriter().Text("general.architecture", "llama").Text("general.type", "adapter")).Kind);
        var encoder = Of(new GgufWriter().Text("general.architecture", "t5encoder").U32("t5encoder.block_count", 4)
            .U32("t5encoder.context_length", 512).Bool("t5encoder.attention.causal", false));
        Assert.Equal("model", encoder.Kind);
        Assert.False(encoder.IsCausal);
        Assert.Null(encoder.KvBytesPerTokenF16);
    }

    [Fact]
    public void Kv_is_not_computed_for_hybrid_or_latent_attention_models()
    {
        var plain = GgufFixtures.Parse(GgufFixtures.Qwen3().Build());
        Assert.Null(plain.KvUnknownReason);

        var hybrid = GgufFixtures.Parse(new GgufWriter()
            .Text("general.architecture", "qwen3next").U32("qwen3next.block_count", 48).U32("qwen3next.context_length", 262144)
            .U32("qwen3next.attention.head_count", 16).U32("qwen3next.attention.head_count_kv", 2)
            .U32("qwen3next.attention.key_length", 256).U32("qwen3next.attention.value_length", 256)
            .U32("qwen3next.full_attention_interval", 4).U32("qwen3next.ssm.conv_kernel", 4)
            .Tensor("blk.0.attn_q.weight", 64, 8).Build());
        Assert.Equal("LLM", hybrid.Kind);
        Assert.Null(hybrid.KvBytesPerTokenF16); // only every fourth layer has attention; the header formula would count 48
        Assert.Equal("순환·선형 층이 섞인 구조", hybrid.KvUnknownReason);

        var latent = GgufFixtures.Parse(new GgufWriter()
            .Text("general.architecture", "deepseek2").U32("deepseek2.block_count", 61).U32("deepseek2.context_length", 163840)
            .U32("deepseek2.attention.head_count", 128).U32("deepseek2.attention.head_count_kv", 128)
            .U32("deepseek2.attention.key_length", 192).U32("deepseek2.attention.value_length", 128)
            .U32("deepseek2.attention.kv_lora_rank", 512).U32("deepseek2.expert_count", 256)
            .Tensor("blk.0.attn_q_a.weight", 64, 8).Build());
        Assert.Null(latent.KvBytesPerTokenF16); // MLA caches a compressed vector, not keys and values per head
        Assert.Equal("잠재 주의(MLA) 구조", latent.KvUnknownReason);
        Assert.Equal(256, latent.Experts);
    }

    [Fact]
    public void Per_layer_kv_heads_and_sliding_windows_are_read()
    {
        var model = GgufFixtures.Parse(new GgufWriter()
            .Text("general.architecture", "hybrid").U32("hybrid.block_count", 4).U32("hybrid.context_length", 8192)
            .U32("hybrid.attention.head_count", 8).I32s("hybrid.attention.head_count_kv", 4, 0, 4, 0)
            .U32("hybrid.attention.key_length", 256).U32("hybrid.attention.value_length", 256).U32("hybrid.attention.sliding_window", 1024)
            .Tensor("blk.0.attn_k.weight", 64, 8).Build());

        Assert.Equal("LLM", model.Kind);
        Assert.Equal((4 + 0 + 4 + 0) * (256 + 256) * 2, model.KvBytesPerTokenF16); // layers without attention hold no KV
        Assert.Equal(1024, model.SlidingWindow);
    }

    [Fact]
    public void Implausible_attention_shapes_give_no_estimate()
    {
        Assert.Null(GgufHeader.KvBytesPerToken(0, 16L, 8L, 1024L, 128L, 128L));
        Assert.Null(GgufHeader.KvBytesPerToken(28, 16L, 1L << 40, 1024L, 128L, 128L));
        Assert.Null(GgufHeader.KvBytesPerToken(28, 16L, 8L, 1024L, 1L << 40, 128L));
        Assert.Null(GgufHeader.KvBytesPerToken(28, null, null, 1024L, null, null));
        Assert.Equal(28L * 16 * (64 + 64) * 2, GgufHeader.KvBytesPerToken(28, 16L, null, 1024L, null, null)); // no GQA, head size from the width
    }

    [Fact]
    public void A_file_cut_anywhere_never_throws_and_a_cut_header_is_an_error()
    {
        var writer = GgufFixtures.Qwen3();
        var bytes = writer.Build();
        for (var length = 0; length < bytes.Length; length++)
        {
            var model = GgufFixtures.Parse(bytes, length);
            if (length < writer.HeaderLength)
            {
                Assert.NotNull(model.Error);
            }
            else
            {
                Assert.Null(model.Error);
                Assert.InRange(model.TensorBytes, 0, bytes.Length);
            }
        }
    }

    [Fact]
    public void Hostile_headers_are_errors_not_exceptions_or_huge_allocations()
    {
        static byte[] Header(uint version, Action<BinaryWriter> rest)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                w.Write("GGUF"u8);
                w.Write(version);
                rest(w);
            }

            return stream.ToArray();
        }

        static void Key(BinaryWriter w, string key, uint type)
        {
            w.Write((ulong)key.Length);
            w.Write(Encoding.ASCII.GetBytes(key));
            w.Write(type);
        }

        Assert.Equal("GGUF 파일이 아닙니다.", GgufFixtures.Parse("GGML\u0003\0\0\0"u8.ToArray()).Error);
        Assert.Contains("버전", GgufFixtures.Parse(Header(4, _ => { })).Error, StringComparison.Ordinal);
        Assert.Contains("버전", GgufFixtures.Parse(Header(0, _ => { })).Error, StringComparison.Ordinal);
        Assert.Contains("항목 수", GgufFixtures.Parse(Header(3, w =>
        {
            w.Write(1UL << 40); // tensors
            w.Write(0UL);
        })).Error, StringComparison.Ordinal);
        Assert.Contains("문자열 길이", GgufFixtures.Parse(Header(3, w =>
        {
            w.Write(0UL);
            w.Write(1UL);
            w.Write(1UL << 40); // key length
        })).Error, StringComparison.Ordinal);
        Assert.Contains("값 형식", GgufFixtures.Parse(Header(3, w =>
        {
            w.Write(0UL);
            w.Write(1UL);
            Key(w, "x", 13);
        })).Error, StringComparison.Ordinal);

        // Arrays nested 100,000 deep: rejected at a fixed depth (no unbounded recursion, no stack overflow).
        Assert.Contains("중첩", GgufFixtures.Parse(Header(3, w =>
        {
            w.Write(0UL);
            w.Write(1UL);
            Key(w, "x", 9);
            for (var i = 0; i < 100_000; i++)
            {
                w.Write(9u);
                w.Write(1UL);
            }

            w.Write(4u);
            w.Write(0UL);
        })).Error, StringComparison.Ordinal);

        // Element counts whose product overflows.
        Assert.NotNull(GgufFixtures.Parse(new GgufWriter().Tensor("blk.0.w", 32, ulong.MaxValue, 2).Build()).Error);
    }

    [Fact]
    public void Offsets_past_the_end_and_huge_element_counts_stay_within_the_file()
    {
        var model = GgufFixtures.Parse(new GgufWriter()
            .Tensor("blk.0.a", 64, ulong.MaxValue / 2)
            .TensorAt("blk.0.b", ulong.MaxValue, ulong.MaxValue / 2)
            .Build());

        Assert.Null(model.Error);
        Assert.Equal(64, model.TensorBytes);
        Assert.Equal(long.MaxValue, model.Parameters); // saturated, not wrapped around
    }

    [Fact]
    public void Files_are_read_once_until_they_change()
    {
        using var temp = new TempDir();
        var path = temp.Sub("model.gguf");
        File.WriteAllBytes(path, GgufFixtures.Qwen3().Build());

        Assert.True(GgufHeader.IsGguf(path));
        var first = GgufHeader.Read(path);
        Assert.NotNull(first);
        Assert.Same(first, GgufHeader.Read(path));

        File.WriteAllBytes(path, GgufFixtures.Qwen3(tiedEmbeddings: true).Build());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var second = GgufHeader.Read(path);
        Assert.NotSame(first, second);
        Assert.Equal(8256, second!.TensorBytes);

        File.WriteAllText(temp.Sub("notes.gguf"), "not a model");
        Assert.False(GgufHeader.IsGguf(temp.Sub("notes.gguf")));
        Assert.False(GgufHeader.IsGguf(temp.Sub("missing.gguf")));
        Assert.Null(GgufHeader.Read(temp.Sub("missing.gguf")));
    }
}

public class GpuEngineCounterTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000C8D1_phys_0_eng_3_engtype_Compute_0", 1234, 0xC8D1L, 0, 3, EngineGroup.Compute)]
    [InlineData("pid_8_luid_0x00000001_0x00000002_phys_1_eng_12_engtype_3D", 8, 0x1_0000_0002L, 1, 12, EngineGroup.ThreeD)]
    [InlineData("pid_77_luid_0x0_0x5E4A_phys_0_eng_0_engtype_VideoDecode", 77, 0x5E4AL, 0, 0, EngineGroup.Video)]
    [InlineData("pid_77_luid_0x0_0x5E4A_phys_0_eng_1_engtype_Copy", 77, 0x5E4AL, 0, 1, EngineGroup.Copy)]
    [InlineData("pid_77_luid_0x0_0x5E4A_phys_0_eng_2_engtype_Cuda", 77, 0x5E4AL, 0, 2, EngineGroup.Compute)]
    [InlineData("pid_77_luid_0x0_0x5E4A_phys_0_eng_4_engtype_Graphics_1", 77, 0x5E4AL, 0, 4, EngineGroup.ThreeD)]
    [InlineData("pid_77_luid_0x0_0x5E4A_phys_0_eng_5_engtype_Security", 77, 0x5E4AL, 0, 5, EngineGroup.Other)]
    [InlineData("pid_77_luid_0x0_0x5E4A_phys_0_eng_6_engtype_", 77, 0x5E4AL, 0, 6, EngineGroup.Other)]
    public void Engine_instance_names_give_process_adapter_engine_and_group(string instance, int pid, long luid, int phys, int engine, EngineGroup group)
    {
        Assert.True(CounterInstances.TryParseEngine(instance, out var p, out var l, out var ph, out var e, out var g));
        Assert.Equal((pid, luid, phys, engine, group), (p, l, ph, e, g));
    }

    [Theory]
    [InlineData("pid_1_luid_0x0_0x1_phys_0")]
    [InlineData("luid_0x0_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_x_luid_0x0_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_99999999999_luid_0x0_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x0_0x1_phys_0_eng_x_engtype_3D")]
    public void Other_names_are_not_engines(string instance)
        => Assert.False(CounterInstances.TryParseEngine(instance, out _, out _, out _, out _, out _));

    [Fact]
    public void A_process_counts_its_busiest_engine_and_an_adapter_engine_adds_its_processes()
    {
        var (processes, adapters) = CounterInstances.MergeEngines(
        [
            new("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 30),
            new("pid_20_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 50),
            new("pid_20_luid_0x0_0x1_phys_0_eng_4_engtype_Compute_0", 40),
            new("pid_20_luid_0x0_0x1_phys_0_eng_5_engtype_Compute_1", 25),
            new("pid_30_luid_0x0_0x1_phys_0_eng_5_engtype_Compute_1", double.NaN),
            new("pid_30_luid_0x0_0x1_phys_0_eng_1_engtype_Copy", -3),
            new("pid_40_luid_0x0_0x2_phys_0_eng_0_engtype_3D", 70),
            new("pid_41_luid_0x0_0x2_phys_0_eng_0_engtype_3D", 50),
            new("garbage", 99),
        ], out var unrecognized);

        Assert.Equal(1, unrecognized);
        // Two compute engines at 40 and 25 are a busiest engine of 40 (Task Manager's GPU column), not 65.
        Assert.Equal(new EngineUsage(50, 40, 0, 0, 0), processes[(20, 1)]);
        Assert.Equal(50, processes[(20, 1)].Busiest);
        Assert.Equal(EngineUsage.Idle, processes[(30, 1)]); // NaN and negative values read as 0
        Assert.Equal(new EngineUsage(80, 40, 0, 0, 0), adapters[1]); // 3D engine 0: 30 + 50
        Assert.Equal(100, adapters[2].ThreeD); // 70 + 50, capped
    }
}

public class GpuProcessInspectionTests
{
    private const long MiB = 1L << 20;
    private static readonly DateTimeOffset Start = GpuMemoryFixtures.Start.AddHours(-1);

    private sealed class FakeScanner
    {
        public Dictionary<int, RawProcessScan> Results { get; } = [];
        public List<int> Calls { get; } = [];

        public RawProcessScan Scan(int pid, TimeSpan budget)
        {
            Calls.Add(pid);
            return Results.GetValueOrDefault(pid, new RawProcessScan(false, false, [@"C:\Windows\System32\d3d11.dll"], []));
        }
    }

    private static GpuProcessMemory Memory(int pid, long mib, EngineUsage? engines = null)
        => new() { ProcessId = pid, Luid = 1, DedicatedBytes = mib * MiB, SharedBytes = 0, Engines = engines };

    private static Dictionary<int, ProcessEntry> Table(IEnumerable<GpuProcessMemory> processes, DateTimeOffset? start = null)
        => processes.Select(p => p.ProcessId).Distinct().ToDictionary(pid => pid, pid => new ProcessEntry(pid, 1, $"p{pid}.exe", start ?? Start, ProcessIdentityState.Verified));

    [Fact]
    public void The_largest_and_the_busy_processes_are_scanned_never_the_kernel_or_ended_ones()
    {
        var scanner = new FakeScanner();
        var inspector = new ProcessInspector(scanner.Scan, new ManualTime(GpuMemoryFixtures.Start));
        var processes = Enumerable.Range(100, 30).Select(pid => Memory(pid, pid)).ToList();
        processes.Add(Memory(4, 10_000));
        processes.Add(Memory(300, 9_000));
        processes.Add(Memory(301, 9_000));
        processes.Add(Memory(200, 1, new EngineUsage(50, 0, 0, 0, 0)));
        var table = Table(processes);
        table[4] = new ProcessEntry(4, 0, "System", null, ProcessIdentityState.StartTimeUnavailable);
        table[300] = table[300] with { State = ProcessIdentityState.Exited };
        table[301] = table[301] with { State = ProcessIdentityState.ReusedAfterSample };

        var details = inspector.Inspect(processes, table, TimeSpan.FromSeconds(10));

        // 25 slots for scannable processes (pids 129..105 by memory) plus the busy one; the kernel and ended processes take none.
        Assert.Equal([.. Enumerable.Range(105, 25).Reverse(), 200], scanner.Calls);
        Assert.Equal(26, details.Count);
        Assert.Equal(["D3D11"], details[129].Runtimes);
    }

    [Fact]
    public void A_process_is_scanned_again_when_its_memory_moves_or_after_two_minutes()
    {
        var scanner = new FakeScanner();
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var inspector = new ProcessInspector(scanner.Scan, time);
        void InspectAt(long mib) => inspector.Inspect([Memory(100, mib)], Table([Memory(100, mib)]), TimeSpan.FromSeconds(10));

        InspectAt(1000);
        Assert.Single(scanner.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        InspectAt(5000); // within 5 s of the last scan: kept
        time.Advance(TimeSpan.FromSeconds(10));
        InspectAt(1050); // +50 MiB: below max(64 MiB, 10 %)
        Assert.Single(scanner.Calls);
        time.Advance(TimeSpan.FromSeconds(10));
        InspectAt(1200); // +200 MiB
        Assert.Equal(2, scanner.Calls.Count);
        time.Advance(TimeSpan.FromMinutes(2));
        InspectAt(1200); // the backstop
        Assert.Equal(3, scanner.Calls.Count);
        Assert.Equal(3, inspector.Scans);
    }

    [Fact]
    public void A_reused_pid_is_another_process_and_is_scanned_afresh()
    {
        var scanner = new FakeScanner();
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var inspector = new ProcessInspector(scanner.Scan, time);
        scanner.Results[100] = new RawProcessScan(false, false, [@"C:\x\ggml-cuda.dll"], []);
        var before = inspector.Inspect([Memory(100, 500)], Table([Memory(100, 500)]), TimeSpan.FromSeconds(10));
        Assert.Equal(["ggml"], before[100].Runtimes);

        time.Advance(TimeSpan.FromSeconds(1));
        scanner.Results[100] = new RawProcessScan(false, false, [@"C:\x\d3d11.dll"], []);
        var after = inspector.Inspect([Memory(100, 500)], Table([Memory(100, 500)], Start.AddMinutes(30)), TimeSpan.FromSeconds(10));

        Assert.Equal(2, scanner.Calls.Count);
        Assert.Equal(["D3D11"], after[100].Runtimes);
    }

    [Fact]
    public void A_refused_process_is_retried_only_by_the_backstop_and_no_budget_means_no_scan()
    {
        var scanner = new FakeScanner();
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var inspector = new ProcessInspector(scanner.Scan, time);
        scanner.Results[100] = RawProcessScan.Denied;

        Assert.Empty(inspector.Inspect([Memory(100, 500)], Table([Memory(100, 500)]), TimeSpan.Zero));
        Assert.Empty(scanner.Calls);

        Assert.True(inspector.Inspect([Memory(100, 500)], Table([Memory(100, 500)]), TimeSpan.FromSeconds(10))[100].AccessDenied);
        time.Advance(TimeSpan.FromSeconds(30));
        inspector.Inspect([Memory(100, 4000)], Table([Memory(100, 4000)]), TimeSpan.FromSeconds(10));
        Assert.Single(scanner.Calls);
        time.Advance(TimeSpan.FromMinutes(2));
        inspector.Inspect([Memory(100, 4000)], Table([Memory(100, 4000)]), TimeSpan.FromSeconds(10));
        Assert.Equal(2, scanner.Calls.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_slow_inspection_never_holds_up_a_reading_and_its_result_is_kept_for_the_same_process_only()
    {
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var inspection = new BackgroundInspection((processes, table) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TestContext.Current.CancellationToken); // a slow disk or a large model header
            return processes.ToDictionary(p => p.ProcessId, p => new ProcessDetail { Runtimes = ["CUDA"], Key = table[p.ProcessId].Key });
        });
        var table = Table([Memory(100, 500)]);
        var issues = new List<SensorIssue>();

        var first = inspection.Next([Memory(100, 500)], table, issues); // returns at once with what is known (nothing yet)
        Assert.Empty(first);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(10))); // started on the pool
        Assert.Empty(inspection.Next([Memory(100, 500)], table, issues)); // still running: not started twice
        Assert.Equal(1, Volatile.Read(ref calls));

        release.Set();
        await inspection.Running!;
        Assert.Equal(["CUDA"], inspection.Next([Memory(100, 500)], table, issues)[100].Runtimes);

        // The PID now belongs to another process (another start time): the old detail is not shown for it.
        var reused = Table([Memory(100, 500)], Start.AddMinutes(30));
        Assert.Empty(inspection.Next([Memory(100, 500)], reused, issues));
        Assert.Empty(issues);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_failing_or_overdue_inspection_is_reported_and_the_last_details_stay()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        using var gate = new ManualResetEventSlim(true);
        var fail = false;
        var inspection = new BackgroundInspection((processes, table) =>
        {
            gate.Wait(TestContext.Current.CancellationToken);
            return fail ? throw new IOException("disk gone (test)") : processes.ToDictionary(p => p.ProcessId, p => new ProcessDetail { Key = table[p.ProcessId].Key });
        }, time);
        var table = Table([Memory(100, 500)]);
        var issues = new List<SensorIssue>();
        inspection.Next([Memory(100, 500)], table, issues);
        await inspection.Running!;

        fail = true;
        Assert.Single(inspection.Next([Memory(100, 500)], table, issues)); // takes the finished result, starts one that fails
        await Assert.ThrowsAsync<IOException>(() => inspection.Running!);
        fail = false;
        gate.Reset();
        Assert.Single(inspection.Next([Memory(100, 500)], table, issues)); // the failure is reported, the last good details stay
        Assert.Contains(issues, i => i.Message.Contains("disk gone (test)", StringComparison.Ordinal));

        issues.Clear();
        time.Advance(BackgroundInspection.StuckAfter + TimeSpan.FromSeconds(1)); // the inspection started above hangs
        Assert.Single(inspection.Next([Memory(100, 500)], table, issues));
        Assert.Contains(issues, i => i.Message.Contains("끝나지 않았습니다", StringComparison.Ordinal));
        gate.Set();
        await inspection.Running!;
    }

    [Fact]
    public void Model_files_are_named_by_file_name_only_and_llms_come_first()
    {
        var scanner = new FakeScanner();
        scanner.Results[100] = new RawProcessScan(false, true,
            [@"C:\app\llama.dll", @"C:\app\ggml-cuda.dll", @"C:\Windows\System32\nvcuda.dll"],
            [@"C:\Users\alice\models\Qwen3-0.6B-Q8_0.gguf", @"C:\Users\alice\AppData\cache.bin", @"C:\Users\alice\models\mmproj.gguf", @"C:\Windows\Fonts\arial.ttf"]);
        var sizes = new Dictionary<string, long>
        {
            [@"C:\Users\alice\models\Qwen3-0.6B-Q8_0.gguf"] = 600 * MiB,
            [@"C:\Users\alice\AppData\cache.bin"] = 10 * MiB,
            [@"C:\Users\alice\models\mmproj.gguf"] = 800 * MiB,
            [@"C:\Windows\Fonts\arial.ttf"] = MiB,
        };
        var inspector = new ProcessInspector(scanner.Scan, new ManualTime(GpuMemoryFixtures.Start),
            fileSize: path => sizes.TryGetValue(path, out var size) ? size : null,
            hasGgufMagic: path => path.EndsWith(".gguf", StringComparison.Ordinal),
            readGguf: path => new GgufSummary { Kind = path.Contains("mmproj", StringComparison.Ordinal) ? "vision projector" : "LLM", Name = Path.GetFileNameWithoutExtension(path) });

        var detail = inspector.Inspect([Memory(100, 1000)], Table([Memory(100, 1000)]), TimeSpan.FromSeconds(10))[100];

        Assert.Equal(["llama.cpp", "ggml", "CUDA"], detail.Runtimes);
        Assert.True(detail.Partial);
        Assert.Equal(3, detail.LoadedLibraries);
        Assert.Equal(["Qwen3-0.6B-Q8_0.gguf", "mmproj.gguf"], detail.Models.Select(m => m.FileName)); // the LLM first, then by size
        Assert.All(detail.Models, m => Assert.DoesNotContain("alice", m.FileName, StringComparison.Ordinal));
        Assert.Equal("GGUF", detail.Models[0].Format);
        Assert.Equal(600 * MiB, detail.Models[0].SizeBytes);
    }

    [Fact]
    public void At_most_eight_model_files_are_listed_per_process()
    {
        var scanner = new FakeScanner();
        scanner.Results[100] = new RawProcessScan(false, false, [], [.. Enumerable.Range(0, 12).Select(i => $@"D:\m\part{i}.gguf")]);
        var inspector = new ProcessInspector(scanner.Scan, new ManualTime(GpuMemoryFixtures.Start), fileSize: _ => 100 * MiB, hasGgufMagic: _ => true,
            readGguf: _ => new GgufSummary { Kind = "model" });

        Assert.Equal(8, inspector.Inspect([Memory(100, 1000)], Table([Memory(100, 1000)]), TimeSpan.FromSeconds(10))[100].Models.Count);
    }

    [Fact]
    public void Runtimes_are_recognised_from_library_file_names_only()
    {
        Assert.Equal(["llama.cpp", "ggml", "PyTorch", "CUDA", "Vulkan", "D3D11"], RuntimeLibraries.Recognize(
        [
            @"C:\Users\someone\AppData\Local\Programs\App\ggml-cuda.dll",
            @"C:\Program Files\App\LLAMA.DLL",
            @"C:\Windows\System32\nvcuda.dll",
            @"C:\Windows\System32\d3d11.dll",
            @"C:\Windows\System32\vulkan-1.dll",
            @"C:\Windows\System32\kernel32.dll",
            "/opt/x/torch_cuda.dll",
        ]));
        Assert.Empty(RuntimeLibraries.Recognize(["notllama.dll", "ggml.txt", "torch_python.dll", "cudart.dll", "d3d11on12.dll"]));
    }

    [Fact]
    public void Model_files_are_recognised_by_magic_or_by_extension_and_size()
    {
        const long M = MiB;
        var checks = 0;
        bool Magic(string path)
        {
            checks++;
            return true;
        }

        static bool NoMagic(string path) => false;

        Assert.Equal((true, true), ModelFiles.Classify(@"D:\models\tiny.gguf", 1024, Magic));
        Assert.Equal((false, false), ModelFiles.Classify(@"D:\models\fake.gguf", 1L << 30, NoMagic));
        // Files without an extension (Ollama's blobs) are opened only from 64 MiB.
        checks = 0;
        Assert.Equal((false, false), ModelFiles.Classify(@"C:\ollama\blobs\sha256-1", 10 * M, Magic));
        Assert.Equal(0, checks);
        Assert.Equal((true, true), ModelFiles.Classify(@"C:\ollama\blobs\sha256-1", 200 * M, Magic));
        Assert.Equal((true, false), ModelFiles.Classify("model.safetensors", 5 * M, NoMagic));
        Assert.Equal((false, false), ModelFiles.Classify("model.safetensors", M, NoMagic));
        Assert.Equal((false, false), ModelFiles.Classify("cache.bin", 10 * M, NoMagic)); // also used for caches: needs 64 MiB
        Assert.Equal((true, false), ModelFiles.Classify("pytorch_model.bin", 100 * M, NoMagic));
        Assert.Equal((false, false), ModelFiles.Classify("big.dll", 1L << 30, NoMagic));
        Assert.Equal("GGUF", ModelFiles.FormatOf("x.bin", true));
        Assert.Equal("ONNX", ModelFiles.FormatOf("x.onnx", false));
        Assert.Equal("TensorRT", ModelFiles.FormatOf("x.plan", false));
        Assert.Equal("PyTorch", ModelFiles.FormatOf("x.pth", false));
    }
}

public class GpuProcessKindTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;

    private static ProcessDetail Libraries(params string[] runtimes) => new() { Runtimes = runtimes };

    private static ProcessDetail Model(string kind, string file) => new() { Models = [new ModelFile(file, 1, "GGUF", new GgufSummary { Kind = kind })] };

    private static ProcessKind Kind(string name, ProcessDetail? detail = null, EngineUsage? engines = null, bool strata = false, int pid = 10)
        => ProcessKinds.Classify(pid, name, detail, engines, strata).Kind;

    [Fact]
    public void Kinds_follow_the_strongest_evidence()
    {
        Assert.Equal(ProcessKind.Kernel, Kind("System", pid: 4));
        Assert.Equal(ProcessKind.Compositor, Kind("DWM.EXE"));
        Assert.Equal(ProcessKind.System, Kind("csrss.exe"));
        Assert.Equal(ProcessKind.Llm, Kind("python.exe", strata: true));
        var mapped = ProcessKinds.Classify(10, "python.exe", Model("LLM", "qwen.gguf"), null, false);
        Assert.Equal(ProcessKind.Llm, mapped.Kind);
        Assert.Contains("qwen.gguf", mapped.Basis, StringComparison.Ordinal);
        Assert.Equal(ProcessKind.Llm, Kind("server.exe", Libraries("llama.cpp", "CUDA")));
        Assert.Equal(ProcessKind.Llm, Kind("ollama.exe"));
        Assert.Equal(ProcessKind.Ai, Kind("python.exe", Libraries("PyTorch", "CUDA")));
        Assert.Equal(ProcessKind.Ai, Kind("whisper-app.exe", Model("model", "ggml-base.bin")));
        // An editor that loads an AI runtime is a video process while it decodes or encodes.
        Assert.Equal(ProcessKind.Video, Kind("editor.exe", Libraries("ONNX Runtime"), new EngineUsage(5, 0, 0, 30, 0)));
        Assert.Equal(ProcessKind.Browser, Kind("msedge.exe", Libraries("D3D12", "D3D11"), new EngineUsage(40, 0, 0, 0, 0)));
        Assert.Equal(ProcessKind.Video, Kind("obs64.exe", Libraries("NVENC", "D3D11")));
        Assert.Equal(ProcessKind.Compute, Kind("solver.exe", Libraries("CUDA")));
        Assert.Equal(ProcessKind.Graphics3D, Kind("game.exe", Libraries("D3D12"), new EngineUsage(55, 0, 0, 0, 0)));
        Assert.Equal(ProcessKind.App, Kind("game.exe", Libraries("D3D12"), new EngineUsage(3, 0, 0, 0, 0)));
        var unknown = ProcessKinds.Classify(10, "app.exe", null, null, false);
        Assert.Equal((ProcessKind.App, "추가 정보 없음"), (unknown.Kind, unknown.Basis));
    }

    [Fact]
    public void The_llm_split_is_drawn_only_when_every_layer_can_be_on_the_gpu()
    {
        var model = new GgufSummary { Kind = "LLM", GpuWeightBytesAllLayers = 8 * GiB, KvBytesPerTokenF16 = 163_840 };

        var split = LlmMemoryEstimate.From(model, 10 * GiB, 4096)!;
        Assert.True(split.FitsAllLayers);
        Assert.Equal((10 * GiB, 8 * GiB, (long?)(640 * MiB), (long?)(2 * GiB - 640 * MiB)), (split.Total, split.WeightsAllLayers, split.KvCache, split.Rest));
        Assert.False(split.KvIsUpperBound);

        var noContext = LlmMemoryEstimate.From(model, 10 * GiB, null)!;
        Assert.Null(noContext.KvCache);
        Assert.Equal(2 * GiB, noContext.Rest);

        Assert.Null(LlmMemoryEstimate.From(model, 10 * GiB, 32768)!.KvCache); // 5 GiB of KV do not fit in 2 GiB: not split

        // 8 GiB of weights cannot all sit in a process holding 6 GiB: the 6 GiB may hold KV and buffers too, so none of it
        // is assigned to the weights. No split; the two numbers stay apart.
        var partial = LlmMemoryEstimate.From(model, 6 * GiB, 4096)!;
        Assert.False(partial.FitsAllLayers);
        Assert.Equal((6 * GiB, 8 * GiB, (long?)null, (long?)null), (partial.Total, partial.WeightsAllLayers, partial.KvCache, partial.Rest));
        Assert.False(LlmMemoryEstimate.From(model, 8 * GiB - 1, null)!.FitsAllLayers); // no slack either: a byte over is over
        Assert.True(LlmMemoryEstimate.From(model, 8 * GiB, null)!.FitsAllLayers);

        Assert.True(LlmMemoryEstimate.From(model with { SlidingWindow = 1024 }, 10 * GiB, 4096)!.KvIsUpperBound);
        Assert.Null(LlmMemoryEstimate.From(model, null, 4096));
        Assert.Null(LlmMemoryEstimate.From(model, 0, 4096));
        Assert.Null(LlmMemoryEstimate.From(model with { Error = "damaged" }, 10 * GiB, 4096));
        Assert.Null(LlmMemoryEstimate.From(model with { GpuWeightBytesAllLayers = 0 }, 10 * GiB, 4096));
    }
}
