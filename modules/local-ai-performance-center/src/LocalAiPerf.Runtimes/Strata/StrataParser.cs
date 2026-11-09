using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAiPerf.Core.Runtime;

namespace LocalAiPerf.Runtimes.Strata;

/// <summary>Telemetry as read from one poll, before the adapter adds metadata and derived values.</summary>
internal sealed record StrataTelemetry
{
    public required string Source { get; init; }
    public double? SampledAt { get; init; }
    public string? InstanceId { get; init; }
    public string? InstanceState { get; init; }
    public double? BootTime { get; init; }
    public string? Service { get; init; }
    public string? EngineVersion { get; init; }
    public string? ModelId { get; init; }
    public bool? ModelLoaded { get; init; }
    public long? Context { get; init; }
    public RuntimeWorkInfo Work { get; init; } = new();
    public IReadOnlyList<RuntimeSlot> Slots { get; init; } = [];
    public RuntimeRates Rates { get; init; } = new();
    public IReadOnlyList<CompletedRequest> Recent { get; init; } = [];
    public RuntimeTotals? Totals { get; init; }
    public IReadOnlyList<RuntimeActivityEvent> Activity { get; init; } = [];
}

internal sealed record StrataHealth(string? Service, bool? Loaded, long? MaxContext, string? Model, bool? ApiKey, bool? Images);

internal sealed record StrataStatus(string? Service, string? EngineVersion, long? UptimeSeconds, double? Started, long? Context,
    int? SlotsServing, int? SlotsRequested, bool? Vision, bool? AutoLoad, bool? Loaded, string? Model);

internal sealed record StrataProps(long? Context, int? TotalSlots, string? ModelAlias, string? ModelFileName, bool? Vision,
    bool? AutoLoad, bool? Sleeping, string? BuildVersion, IReadOnlyList<RuntimeSetting> Settings);

internal sealed record StrataModels(IReadOnlyList<string> Ids, IReadOnlyList<string> Aliases);

/// <summary>Parses the documented Strata JSON shapes (schema v1 telemetry and the older /metrics + /v1/status pair).</summary>
internal static partial class StrataParser
{
    private static readonly HashSet<string> ActivityEvents = new(StringComparer.Ordinal)
    {
        "queued", "admitted", "reading", "first_token", "finished", "cancelled", "disconnected", "timeout", "error",
    };

    private static readonly HashSet<string> ActivityOutcomes = new(StringComparer.Ordinal)
    {
        "stop", "length", "cancel", "disconnect", "timeout", "error",
    };

    /// <summary>Generation defaults exposed by /props that are kept as part of the measurement condition.</summary>
    private static readonly string[] SettingKeys =
    [
        "temperature", "top_p", "top_k", "min_p", "repeat_penalty", "presence_penalty", "frequency_penalty", "repeat_last_n", "n_predict",
    ];

    public static StrataTelemetry ParseV1(JsonElement root)
    {
        var instance = JsonRead.Obj(root, "instance") ?? default;
        var model = JsonRead.Obj(root, "model") ?? default;
        var work = JsonRead.Obj(root, "work") ?? default;
        var rates = JsonRead.Obj(root, "rates") ?? default;
        var state = JsonRead.Str(instance, "state");
        return new StrataTelemetry
        {
            Source = JsonRead.Str(root, "source", 40) ?? "server-v1",
            SampledAt = JsonRead.Num(root, "sampled_at"),
            InstanceId = JsonRead.Str(instance, "id", 64),
            InstanceState = state is "ready" or "draining" ? state : null,
            BootTime = JsonRead.Num(instance, "boot_time"),
            Service = JsonRead.Str(instance, "server", 40),
            ModelId = JsonRead.Str(model, "id"),
            ModelLoaded = JsonRead.Bool(model, "loaded"),
            Context = JsonRead.Long(model, "context"),
            Work = new RuntimeWorkInfo
            {
                SlotsEffective = JsonRead.Int(work, "slots_effective"),
                SlotsRequested = JsonRead.Int(work, "slots_requested"),
                Active = JsonRead.Int(work, "active"),
                Queued = JsonRead.Int(work, "queued"),
                Waiting = JsonRead.Int(work, "waiting"),
                OccupiedSlots = JsonRead.Int(work, "occupied_slots"),
                MaxQueue = JsonRead.Int(work, "max_queue"),
                AdmissionRejected = JsonRead.Long(work, "admission_rejected"),
                QueueTimeouts = JsonRead.Long(work, "queue_timeouts"),
                ClientDisconnectCancels = JsonRead.Long(work, "client_disconnect_cancels"),
                OldestQueueSeconds = JsonRead.Num(work, "oldest_queue_s"),
                QueueTimeoutSeconds = JsonRead.Num(work, "queue_timeout_s"),
            },
            Slots = ParseSlots(JsonRead.Arr(work, "slots")),
            Rates = new RuntimeRates
            {
                AnswerTokPerSec = JsonRead.Num(rates, "answer_tok_s"),
                AnswerWindowSeconds = JsonRead.Num(rates, "answer_window_s"),
                PrefillTokPerSec = JsonRead.Num(rates, "prefill_tok_s"),
                OutputTokensCounter = JsonRead.Long(rates, "output_tokens_server_counter"),
            },
            Recent = ParseRequests(JsonRead.Arr(root, "recent")),
            Totals = ParseTotals(JsonRead.Obj(root, "totals")),
            Activity = ParseActivity(JsonRead.Obj(root, "activity")),
        };
    }

    /// <summary>Older servers: /metrics plus /v1/status, normalised the same way Strata's own client does.</summary>
    public static StrataTelemetry ParseLegacy(JsonElement metrics, JsonElement status)
    {
        var live = JsonRead.Obj(metrics, "live") ?? default;
        var engine = JsonRead.Obj(metrics, "engine") ?? default;
        var concurrency = JsonRead.Obj(status, "concurrency") ?? default;
        var liveState = JsonRead.Str(live, "state");
        int? active = JsonRead.Int(live, "running") ?? (liveState is "reading" or "generating" ? 1 : liveState is null ? null : 0);
        return new StrataTelemetry
        {
            Source = "legacy-adapter",
            SampledAt = JsonRead.Num(metrics, "time"),
            BootTime = JsonRead.Num(status, "started"),
            Service = JsonRead.Str(status, "service", 40),
            EngineVersion = JsonRead.Str(status, "engine", 64) ?? JsonRead.Str(engine, "version", 64),
            ModelId = JsonRead.Str(status, "model"),
            ModelLoaded = JsonRead.Bool(status, "loaded"),
            Context = JsonRead.Long(status, "cache_max_tokens"),
            Work = new RuntimeWorkInfo
            {
                SlotsEffective = JsonRead.Int(concurrency, "serving"),
                Active = active,
                Queued = JsonRead.Int(live, "queued"),
                Waiting = JsonRead.Int(live, "waiting"),
            },
            Slots = ParseSlots(JsonRead.Arr(live, "slots")),
            Rates = new RuntimeRates
            {
                AnswerTokPerSec = JsonRead.Num(live, "tok_s"),
                AnswerWindowSeconds = JsonRead.Num(live, "tok_s_window_s"),
                PrefillTokPerSec = JsonRead.Num(live, "prefill_tok_s_mean"),
            },
            Recent = ParseRequests(JsonRead.Arr(metrics, "requests")),
            Totals = ParseTotals(JsonRead.Obj(metrics, "totals")),
        };
    }

    /// <summary>A reachable old server that has neither /v1/telemetry nor /metrics: model and status only.</summary>
    public static StrataTelemetry ParseStatusOnly(JsonElement status)
    {
        var concurrency = JsonRead.Obj(status, "concurrency") ?? default;
        return new StrataTelemetry
        {
            Source = "status-only",
            BootTime = JsonRead.Num(status, "started"),
            Service = JsonRead.Str(status, "service", 40),
            EngineVersion = JsonRead.Str(status, "engine", 64),
            ModelId = JsonRead.Str(status, "model"),
            ModelLoaded = JsonRead.Bool(status, "loaded"),
            Context = JsonRead.Long(status, "cache_max_tokens"),
            Work = new RuntimeWorkInfo { SlotsEffective = JsonRead.Int(concurrency, "serving") },
        };
    }

    public static StrataHealth ParseHealth(JsonElement root)
        => new(JsonRead.Str(root, "service", 40), JsonRead.Bool(root, "loaded"), JsonRead.Long(root, "max_context"),
            JsonRead.Str(root, "model"), JsonRead.Bool(root, "api_key"), JsonRead.Bool(root, "images"));

    public static StrataStatus ParseStatus(JsonElement root)
    {
        var concurrency = JsonRead.Obj(root, "concurrency") ?? default;
        var vision = JsonRead.Obj(root, "vision") ?? default;
        return new StrataStatus(
            JsonRead.Str(root, "service", 40),
            JsonRead.Str(root, "engine", 64),
            JsonRead.Long(root, "uptime_s"),
            JsonRead.Num(root, "started"),
            JsonRead.Long(root, "cache_max_tokens"),
            JsonRead.Int(concurrency, "serving"),
            JsonRead.Int(concurrency, "requested"),
            JsonRead.Bool(vision, "enabled"),
            JsonRead.Bool(root, "auto_load"),
            JsonRead.Bool(root, "loaded"),
            JsonRead.Str(root, "model"));
    }

    public static StrataProps ParseProps(JsonElement root)
    {
        var defaults = JsonRead.Obj(root, "default_generation_settings") ?? default;
        var parameters = JsonRead.Obj(defaults, "params") ?? default;
        var modalities = JsonRead.Obj(root, "modalities") ?? default;
        var settings = new List<RuntimeSetting>();
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in SettingKeys)
            {
                if (parameters.TryGetProperty(key, out var value) && JsonRead.Scalar(value) is string text)
                {
                    settings.Add(new RuntimeSetting(key, text));
                }
            }
        }

        string? buildVersion = null;
        if (JsonRead.Str(root, "build_info", 80) is string build)
        {
            var match = BuildInfoRegex().Match(build);
            buildVersion = match.Success ? match.Groups[1].Value : null;
        }

        // Only the file name: the full path can contain the Windows user name.
        string? fileName = null;
        if (JsonRead.Str(root, "model_path", 1024) is string path && path.Length > 0)
        {
            fileName = JsonRead.Clean(path.Replace('\\', '/').Split('/').LastOrDefault(), 120);
        }

        return new StrataProps(
            JsonRead.Long(defaults, "n_ctx"),
            JsonRead.Int(root, "total_slots"),
            JsonRead.Str(root, "model_alias"),
            fileName,
            JsonRead.Bool(modalities, "vision"),
            JsonRead.Bool(root, "models_autoload"),
            JsonRead.Bool(root, "is_sleeping"),
            buildVersion,
            settings);
    }

    public static StrataModels ParseModels(JsonElement root)
    {
        var ids = new List<string>();
        var aliases = new List<string>();
        if (JsonRead.Arr(root, "data") is JsonElement data)
        {
            foreach (var item in data.EnumerateArray().Take(16))
            {
                var id = JsonRead.Str(item, "id");
                if (id is null)
                {
                    continue;
                }

                if (JsonRead.Str(item, "alias_of") is not null)
                {
                    aliases.Add(id);
                }
                else
                {
                    ids.Add(id);
                }
            }
        }

        return new StrataModels(ids, aliases);
    }

    private static IReadOnlyList<RuntimeSlot> ParseSlots(JsonElement? array)
    {
        if (array is not JsonElement slots)
        {
            return [];
        }

        var rows = new List<RuntimeSlot>();
        foreach (var row in slots.EnumerateArray().Take(64))
        {
            if (row.ValueKind != JsonValueKind.Object || JsonRead.Int(row, "slot") is not int index || index < 0)
            {
                continue;
            }

            rows.Add(new RuntimeSlot(index, JsonRead.Str(row, "state", 20), JsonRead.Long(row, "held_tokens"),
                JsonRead.Long(row, "prompt_tokens"), JsonRead.Long(row, "prompt_read"), JsonRead.Long(row, "generated"),
                JsonRead.Num(row, "tok_s")));
        }

        return rows;
    }

    private static IReadOnlyList<CompletedRequest> ParseRequests(JsonElement? array)
    {
        if (array is not JsonElement requests)
        {
            return [];
        }

        var rows = new List<CompletedRequest>();
        foreach (var row in requests.EnumerateArray().Take(50))
        {
            if (row.ValueKind != JsonValueKind.Object || JsonRead.Str(row, "id", 64) is not string id || id.Length == 0)
            {
                continue;
            }

            rows.Add(new CompletedRequest
            {
                Id = id,
                StartedUnix = JsonRead.Num(row, "time"),
                DurationSeconds = JsonRead.Num(row, "duration_s"),
                Finish = JsonRead.Str(row, "finish", 20),
                PromptTokens = JsonRead.Long(row, "prompt_tokens"),
                ReusedTokens = JsonRead.Long(row, "reused"),
                OutputTokens = JsonRead.Long(row, "output_tokens"),
                PromptMs = JsonRead.Num(row, "prompt_ms"),
                DecodeMs = JsonRead.Num(row, "decode_ms"),
                FirstEngineTokenSeconds = JsonRead.Num(row, "first_engine_token_s"),
            });
        }

        return rows;
    }

    private static RuntimeTotals? ParseTotals(JsonElement? element)
    {
        if (element is not JsonElement totals)
        {
            return null;
        }

        return new RuntimeTotals
        {
            SinceUnix = JsonRead.Num(totals, "since"),
            Requests = JsonRead.Long(totals, "requests"),
            PromptTokens = JsonRead.Long(totals, "prompt_tokens"),
            OutputTokens = JsonRead.Long(totals, "output_tokens"),
        };
    }

    /// <summary>Keeps only well-formed, increasing, bounded activity events (the server is not trusted blindly).</summary>
    private static IReadOnlyList<RuntimeActivityEvent> ParseActivity(JsonElement? element)
    {
        if (element is not JsonElement activity || JsonRead.Long(activity, "last_seq") is not long lastSeq || lastSeq < 0
            || JsonRead.Arr(activity, "events") is not JsonElement events)
        {
            return [];
        }

        var rows = new List<RuntimeActivityEvent>();
        long previous = 0;
        foreach (var row in events.EnumerateArray().TakeLast(32))
        {
            if (JsonRead.Long(row, "seq") is not long seq || seq <= previous || seq > lastSeq
                || JsonRead.Num(row, "at") is not double at || at < 0 || at > 4_102_444_800
                || JsonRead.Str(row, "request_id", 32) is not string requestId || !RequestIdRegex().IsMatch(requestId)
                || JsonRead.Str(row, "event", 20) is not string kind || !ActivityEvents.Contains(kind))
            {
                continue;
            }

            var outcome = JsonRead.Str(row, "outcome", 20);
            if (outcome is not null && !ActivityOutcomes.Contains(outcome))
            {
                continue;
            }

            rows.Add(new RuntimeActivityEvent(seq, at, requestId, kind, outcome));
            previous = seq;
        }

        return rows;
    }

    [GeneratedRegex("^[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdRegex();

    [GeneratedRegex(@"^Strata\s+([0-9A-Za-z._+\-]{1,40})$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildInfoRegex();
}
