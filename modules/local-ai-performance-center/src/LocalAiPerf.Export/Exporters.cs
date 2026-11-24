using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAiPerf.Analytics;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Export;

public enum ExportFormat
{
    Csv,
    Json,
}

/// <summary>
/// Writes sessions and comparisons to CSV or JSON. Exports contain measurements, reported numbers and derived values,
/// each labelled; they never contain prompts, answers or credentials (none are stored).
/// </summary>
public static class SessionExporter
{
    public const int ExportFormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>CSV: a folder with samples.csv (merged timeline), requests.csv, events.csv and summary.csv.</summary>
    public static IReadOnlyList<string> ExportSessionCsv(SessionData data, SessionReport report, string directory, int gpuIndex = 0)
    {
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        var samples = Path.Combine(directory, "samples.csv");
        using (var csv = new CsvWriter(samples))
        {
            csv.Row("time_utc", "time_local", "cpu_util_pct", "ram_used_bytes", "ram_total_bytes", "gpu_index", "gpu_util_pct", "gpu_mem_util_pct",
                "gpu_temp_c", "gpu_power_w", "gpu_power_limit_w", "vram_used_bytes", "vram_total_bytes", "gpu_clock_gfx_mhz", "gpu_clock_mem_mhz",
                "gpu_fan_pct", "pcie_tx_kbps", "pcie_rx_kbps", "gpu_energy_mj", "gpu_throttle_reasons", "runtime_time_utc", "runtime_state",
                "answer_tok_s", "prefill_tok_s", "aggregate_tok_s_derived", "active_requests", "queued_requests", "context_used_tokens",
                "context_max_tokens", "sample_flags");
            var gpu = data.Gpu.Where(g => g.GpuIndex == gpuIndex).ToDictionary(g => g.TimeMs);
            var runtime = data.Runtime.ToList();
            var r = 0;
            foreach (var s in data.System)
            {
                gpu.TryGetValue(s.TimeMs, out var g);
                while (r + 1 < runtime.Count && runtime[r + 1].TimeMs <= s.TimeMs)
                {
                    r++;
                }

                var rt = runtime.Count > 0 && runtime[r].TimeMs <= s.TimeMs && s.TimeMs - runtime[r].TimeMs <= 2L * Math.Max(1000, data.Session.SampleIntervalMs)
                    ? runtime[r]
                    : null;
                var t = DateTimeOffset.FromUnixTimeMilliseconds(s.TimeMs);
                csv.Row(t, t.ToLocalTime(), s.CpuUtil, s.RamUsed, s.RamTotal, g?.GpuIndex, g?.Util, g?.MemUtil, g?.TempC, g?.PowerW, g?.PowerLimitW,
                    g?.VramUsed, g?.VramTotal, g?.ClockGfxMhz, g?.ClockMemMhz, g?.FanPct, g?.PcieTxKBps, g?.PcieRxKBps, g?.EnergyMj,
                    g?.Throttle is long th ? "0x" + th.ToString("X", CultureInfo.InvariantCulture) : null,
                    rt is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(rt.TimeMs), rt?.State.ToString(),
                    rt?.AnswerTokS, rt?.PrefillTokS, rt?.AggregateTokS, rt?.Active, rt?.Queued, rt?.ContextUsed, rt?.ContextMax, (int)s.Flags);
            }
        }

        files.Add(samples);
        var requests = Path.Combine(directory, "requests.csv");
        using (var csv = new CsvWriter(requests))
        {
            csv.Row("request_id", "instance_id", "started_utc", "duration_s", "finish", "prompt_tokens", "reused_tokens", "output_tokens",
                "prompt_ms", "decode_ms", "first_engine_token_s", "decode_tok_s_derived", "prefill_tok_s_derived");
            foreach (var row in data.Requests)
            {
                var c = row.ToCompletedRequest();
                csv.Row(row.RequestId, row.InstanceId, row.StartedMs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, row.DurationS,
                    row.Finish, row.PromptTokens, row.ReusedTokens, row.OutputTokens, row.PromptMs, row.DecodeMs, row.FirstTokenS,
                    c.DecodeTokPerSec, c.PrefillTokPerSec);
            }
        }

        files.Add(requests);
        var events = Path.Combine(directory, "events.csv");
        using (var csv = new CsvWriter(events))
        {
            csv.Row("time_utc", "severity", "category", "message");
            foreach (var e in data.Events)
            {
                csv.Row(e.Time, e.Severity.ToString(), e.Category, e.Message);
            }
        }

        files.Add(events);
        var summary = Path.Combine(directory, "summary.csv");
        using (var csv = new CsvWriter(summary))
        {
            csv.Row("metric_key", "metric", "unit", "origin", "source", "count", "mean", "min", "max", "stddev", "p50", "p95",
                "first_10pct_mean", "last_10pct_mean", "change_pct_derived", "slope_per_hour_derived");
            foreach (var m in report.Metrics)
            {
                csv.Row(m.Metric.Key, m.Metric.DisplayName, m.Metric.Unit, m.Metric.OriginLabel, m.Metric.Source, m.Stats.Count, m.Stats.Mean,
                    m.Stats.Min, m.Stats.Max, m.Stats.StdDev, m.Stats.P50, m.Stats.P95, m.Trend.FirstWindowMean, m.Trend.LastWindowMean,
                    m.Trend.ChangePercent, m.Trend.SlopePerHour);
            }

            csv.Row();
            csv.Row("energy_gpu_j_derived", report.Energy.GpuEnergyJoules);
            csv.Row("output_tokens_reported", report.Energy.OutputTokens);
            csv.Row("joules_per_output_token_derived", report.Energy.JoulesPerOutputToken);
            csv.Row("energy_basis", report.Energy.Basis);
            csv.Row();
            csv.Row("degradation_metric", "start_utc", "end_utc", "points", "mean", "baseline", "drop_pct", "concurrent_observations (not causes)");
            foreach (var d in report.Degradations)
            {
                csv.Row(d.MetricKey, DateTimeOffset.FromUnixTimeMilliseconds(d.StartMs), DateTimeOffset.FromUnixTimeMilliseconds(d.EndMs), d.Points,
                    d.Mean, d.Baseline, d.DropPercent, string.Join("; ", d.ConcurrentObservations));
            }
        }

        files.Add(summary);
        return files;
    }

    public static void ExportSessionJson(SessionData data, SessionReport report, string path)
    {
        var document = new
        {
            exportFormatVersion = ExportFormatVersion,
            exportedAt = DateTimeOffset.UtcNow,
            app = new { name = AppInfo.ProductName, version = AppInfo.Version },
            notes = new[]
            {
                "origin 'Measured' = 이 앱이 센서/OS API에서 직접 읽은 값, 'Reported' = AI 엔진이 공개 API로 보고한 값, 'Derived' = 이 앱이 계산한 값.",
                "프롬프트, 모델 응답, 인증 정보는 저장하지도 내보내지도 않습니다.",
            },
            session = data.Session,
            host = report.Host,
            conditions = data.RuntimeInfo.Select(r => new { time = DateTimeOffset.FromUnixTimeMilliseconds(r.TimeMs), r.Fingerprint, r.Conditions }),
            summary = report.Metrics.Select(m => new
            {
                m.Metric.Key, m.Metric.DisplayName, m.Metric.Unit, origin = m.Metric.Origin.ToString(), m.Metric.Source, m.Stats, trend = m.Trend,
            }),
            energy = report.Energy,
            degradationCandidates = report.Degradations,
            throttleIntervals = report.Throttles,
            samples = new { system = data.System, gpu = data.Gpu, runtime = data.Runtime },
            requests = data.Requests,
            events = data.Events,
        };
        WriteJson(path, document);
    }

    public static void ExportComparisonJson(ComparisonReport comparison, string path)
    {
        var document = new
        {
            exportFormatVersion = ExportFormatVersion,
            exportedAt = DateTimeOffset.UtcNow,
            app = new { name = AppInfo.ProductName, version = AppInfo.Version },
            baselineSessionId = comparison.Sessions[comparison.BaselineIndex].Session.Id,
            sessions = comparison.Sessions.Select(s => new { s.Session.Id, s.Session.Name, s.Session.StartedAt, s.Session.EndedAt, s.Conditions, s.Energy }),
            warnings = comparison.Warnings,
            rows = comparison.Rows.Select(r => new
            {
                r.Metric.Key, r.Metric.DisplayName, r.Metric.Unit, origin = r.Metric.Origin.ToString(), perSession = r.PerSession,
                deltaPercentVsBaseline = r.DeltaPercent, r.Note,
            }),
            degradationCandidates = comparison.Sessions.Select(s => new { sessionId = s.Session.Id, candidates = s.Degradations }),
        };
        WriteJson(path, document);
    }

    public static void ExportComparisonCsv(ComparisonReport comparison, string path)
    {
        using var csv = new CsvWriter(path);
        var header = new List<object?> { "metric_key", "metric", "unit", "origin" };
        foreach (var s in comparison.Sessions)
        {
            header.Add($"#{s.Session.Id} mean");
            header.Add($"#{s.Session.Id} min");
            header.Add($"#{s.Session.Id} max");
            header.Add($"#{s.Session.Id} n");
            header.Add($"#{s.Session.Id} delta_pct_vs_baseline");
        }

        header.Add("note");
        csv.Row([.. header]);
        foreach (var row in comparison.Rows)
        {
            var values = new List<object?> { row.Metric.Key, row.Metric.DisplayName, row.Metric.Unit, row.Metric.OriginLabel };
            for (var i = 0; i < comparison.Sessions.Count; i++)
            {
                values.Add(row.PerSession[i].Mean);
                values.Add(row.PerSession[i].Min);
                values.Add(row.PerSession[i].Max);
                values.Add(row.PerSession[i].Count);
                values.Add(row.DeltaPercent[i]);
            }

            values.Add(row.Note);
            csv.Row([.. values]);
        }

        csv.Row();
        csv.Row("warning_kind", "message");
        foreach (var warning in comparison.Warnings)
        {
            csv.Row(warning.Kind, warning.Message);
        }
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "session" : cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    private static void WriteJson(string path, object document)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, document, Json);
    }
}

/// <summary>A zip with what is needed to diagnose a problem. Contains no API keys (only the variable name), no prompts.</summary>
public static class DiagnosticsBundle
{
    public static void Create(string zipPath, AppSettings settings, AppPaths paths, HardwareInfo? hardware, IReadOnlyList<SensorCapability> capabilities,
        RuntimeSnapshot? runtime, DatabaseStats? database, IReadOnlyList<string> notices, IReadOnlyList<string> recentLog)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } };
        void Add(string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(SecretRedactor.Redact(content));
        }

        Add("app.json", JsonSerializer.Serialize(new
        {
            AppInfo.ProductName,
            AppInfo.Version,
            created = DateTimeOffset.UtcNow,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            dataDirectory = paths.IsPortable ? "(portable)" : "(LocalAppData)",
            isPortable = paths.IsPortable,
            notices,
        }, options));
        Add("settings.json", SettingsSerializer.Serialize(settings)); // contains the key variable NAME only
        Add("hardware.json", JsonSerializer.Serialize(new { hardware, capabilities }, options));
        Add("runtime.json", JsonSerializer.Serialize(runtime is null ? null : new
        {
            runtime.RuntimeKind, runtime.State, runtime.Error, runtime.Endpoint, runtime.LastSuccess, runtime.Server, runtime.Model,
            settingKeys = runtime.Settings.Select(s => s.Key),
        }, options));
        Add("database.json", JsonSerializer.Serialize(database is null ? null : database with { Path = Path.GetFileName(database.Path) }, options));
        Add("recent-log.txt", string.Join(Environment.NewLine, recentLog));
    }
}
