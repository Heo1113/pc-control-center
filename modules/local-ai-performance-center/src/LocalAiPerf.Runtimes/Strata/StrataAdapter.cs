using System.Text.Json;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;

namespace LocalAiPerf.Runtimes.Strata;

public sealed record StrataAdapterOptions(
    string BaseUrl,
    string? Credential,
    TimeSpan Timeout,
    TimeSpan MetadataRefresh,
    string? CredentialVariable = null,
    bool AutoFindPort = false,
    IReadOnlyList<int>? ScanPorts = null);

/// <summary>
/// Observes a running Strata server through its documented read-only HTTP API.
/// Never sends a write request, never touches Strata's files, and keeps working (reporting "not reachable")
/// when Strata is not running.
/// </summary>
public sealed class StrataAdapter : IAiRuntimeAdapter
{
    public const string RuntimeKindId = "strata";
    private static readonly TimeSpan LegacyReprobe = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SlowRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ports the Strata desktop app picks for a model's engine: it tries 18500 and the next 20 ports
    /// (apps/host/server/native-control.mjs, freePort). Only /health (no credential) is asked while searching.
    /// </summary>
    public static readonly IReadOnlyList<int> DesktopEnginePorts = Enumerable.Range(18500, 21).ToArray();

    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);

    private readonly HttpMessageHandler? _handler;
    private readonly List<StrataHttpClient> _retiredClients = [];
    private StrataHttpClient _client;
    private string _activeBase;
    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;
    private readonly StrataAdapterOptions _options;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly object _metaGate = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private int _consecutiveFailures;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private RuntimeSnapshot? _lastFailure;
    private DateTimeOffset? _lastSuccess;
    private bool _preferLegacy;
    private DateTimeOffset _legacyCheckedAt;
    private RuntimeConnectionState? _lastLoggedState;
    private (string? Instance, long Counter, double Time)? _previousCounter;
    private RuntimeServerInfo? _lastServer;
    private RuntimeModelInfo? _lastModel;

    private StrataMetadata _metadata = StrataMetadata.Empty;
    private DateTimeOffset _metadataFetchedAt = DateTimeOffset.MinValue;
    private string? _metadataKey;
    private Task? _metadataTask;

    public StrataAdapter(StrataAdapterOptions options, TimeProvider? time = null, IAppLogger? log = null, HttpMessageHandler? handler = null)
    {
        if (!EndpointValidator.TryNormalize(options.BaseUrl, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(options));
        }

        _options = options with { BaseUrl = normalized };
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _handler = handler;
        _activeBase = normalized;
        _client = new StrataHttpClient(new Uri(normalized), options.Credential, options.Timeout, handler);
    }

    public string Kind => RuntimeKindId;
    public string DisplayName => "Strata";
    public string EndpointDisplay => _activeBase;

    /// <summary>The configured address, when automatic port search moved the connection to another port.</summary>
    public string? ConfiguredEndpoint => _activeBase == _options.BaseUrl ? null : _options.BaseUrl;
    public bool HasCredential => _client.HasCredential;

    /// <summary>Creates the adapter from settings. The key is read from the named environment variable and never stored.</summary>
    public static StrataAdapter FromSettings(StrataSettings settings, TimeProvider? time = null, IAppLogger? log = null)
    {
        string? credential = null;
        if (!string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable))
        {
            credential = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable);
        }

        return new StrataAdapter(
            new StrataAdapterOptions(settings.BaseUrl, credential, TimeSpan.FromMilliseconds(settings.TimeoutMs),
                TimeSpan.FromSeconds(settings.MetadataRefreshSeconds), settings.ApiKeyEnvironmentVariable, settings.AutoFindPort),
            time, log);
    }

    public void RequestReconnect()
    {
        _consecutiveFailures = 0;
        _nextAttempt = DateTimeOffset.MinValue;
        _preferLegacy = false;
        lock (_metaGate)
        {
            _metadataFetchedAt = DateTimeOffset.MinValue;
        }
    }

    public async Task<RuntimeSnapshot> PollAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_lastFailure is not null && now < _nextAttempt)
        {
            return _lastFailure with { Timestamp = now, NextRetryAt = _nextAttempt };
        }

        var (telemetry, error) = await FetchTelemetryAsync(now, cancellationToken).ConfigureAwait(false);
        if (telemetry is null && ShouldSearch(error!, now))
        {
            _nextDiscovery = now + DiscoveryInterval;
            if (await FindEngineAsync(cancellationToken).ConfigureAwait(false) is string found)
            {
                SwitchTo(found);
                (telemetry, error) = await FetchTelemetryAsync(_time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
        }

        now = _time.GetUtcNow();
        if (telemetry is null)
        {
            return RegisterFailure(now, error!);
        }

        _consecutiveFailures = 0;
        _lastFailure = null;
        _lastSuccess = now;
        MaybeRefreshMetadata(now, telemetry);
        var snapshot = BuildSnapshot(now, telemetry);
        _lastServer = snapshot.Server;
        _lastModel = snapshot.Model;
        LogState(snapshot.State, null);
        return snapshot;
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        try
        {
            _metadataTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelled or failed metadata refresh; nothing to keep.
        }

        _client.Dispose();
        foreach (var retired in _retiredClients)
        {
            retired.Dispose();
        }

        _disposeCts.Dispose();
    }

    private bool ShouldSearch(RuntimeError error, DateTimeOffset now)
        => _options.AutoFindPort
           && error.Category is RuntimeErrorCategory.Connection or RuntimeErrorCategory.Compatibility
           && now >= _nextDiscovery
           && !_disposeCts.IsCancellationRequested
           && Uri.TryCreate(_activeBase, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && EndpointValidator.IsLoopback(uri);

    /// <summary>
    /// Looks for a Strata engine on the desktop app's engine ports (loopback only): GET /health, which Strata documents
    /// as needing no key. With several engines, a loaded one that accepts the telemetry key is preferred.
    /// </summary>
    private async Task<string?> FindEngineAsync(CancellationToken ct)
    {
        var current = new Uri(_activeBase);
        var configured = new Uri(_options.BaseUrl);
        var ports = (_options.ScanPorts ?? DesktopEnginePorts).Append(configured.Port).Distinct().Where(p => p != current.Port).ToList();
        var probeTimeout = TimeSpan.FromMilliseconds(Math.Min(_options.Timeout.TotalMilliseconds, 1500));

        async Task<(int Port, StrataHealth? Health)> Probe(int port)
        {
            using var client = new StrataHttpClient(new Uri($"http://{current.Host}:{port}"), null, probeTimeout, _handler);
            using var health = await client.GetAsync(StrataEndpoints.Health, sendCredential: false, ct).ConfigureAwait(false);
            return (port, health.IsSuccess ? StrataParser.ParseHealth(health.Json!.RootElement) : null);
        }

        var results = await Task.WhenAll(ports.Select(Probe)).ConfigureAwait(false);
        var candidates = results.Where(r => r.Health?.Service == "strata")
            .OrderByDescending(r => r.Health!.Loaded == true).ThenBy(r => r.Port).Select(r => $"http://{current.Host}:{r.Port}").ToList();
        if (candidates.Count <= 1 || !_client.HasCredential)
        {
            return candidates.FirstOrDefault();
        }

        foreach (var candidate in candidates)
        {
            using var client = new StrataHttpClient(new Uri(candidate), _options.Credential, _options.Timeout, _handler);
            using var telemetry = await client.GetAsync(StrataEndpoints.Telemetry, sendCredential: true, ct).ConfigureAwait(false);
            if (telemetry.IsSuccess)
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private void SwitchTo(string baseUrl)
    {
        _log.Info("strata", $"Strata 엔진을 {baseUrl}에서 찾았습니다 (설정 주소 {_options.BaseUrl}, 포트 자동 찾기).");
        _retiredClients.Add(_client); // a metadata refresh may still be using it; disposed with the adapter
        _client = new StrataHttpClient(new Uri(baseUrl), _options.Credential, _options.Timeout, _handler);
        _activeBase = baseUrl;
        _preferLegacy = false;
        _previousCounter = null;
        lock (_metaGate)
        {
            _metadataFetchedAt = DateTimeOffset.MinValue;
            _metadataKey = null;
        }
    }

    private async Task<(StrataTelemetry? Telemetry, RuntimeError? Error)> FetchTelemetryAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!_preferLegacy || now - _legacyCheckedAt > LegacyReprobe)
        {
            using var v1 = await _client.GetAsync(StrataEndpoints.Telemetry, sendCredential: true, ct).ConfigureAwait(false);
            if (v1.IsSuccess)
            {
                _preferLegacy = false;
                var root = v1.Json!.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return (null, Incompatible(StrataEndpoints.Telemetry, "텔레메트리 응답이 JSON 객체가 아닙니다."));
                }

                var version = JsonRead.Int(root, "schema_version");
                if (version != 1)
                {
                    return (null, Incompatible(StrataEndpoints.Telemetry,
                        $"텔레메트리 스키마 버전 {(version?.ToString() ?? "없음")}은(는) 지원하지 않습니다 (지원: 1). Strata 업데이트로 API 형식이 바뀌었을 수 있습니다."));
                }

                return (StrataParser.ParseV1(root), null);
            }

            if (!v1.IsNotFound)
            {
                return (null, v1.Error);
            }

            _preferLegacy = true;
            _legacyCheckedAt = now;
        }

        using var metrics = await _client.GetAsync(StrataEndpoints.Metrics, sendCredential: true, ct).ConfigureAwait(false);
        if (metrics.IsSuccess)
        {
            if (metrics.Json!.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, Incompatible(StrataEndpoints.Metrics, "/metrics 응답이 JSON 객체가 아닙니다."));
            }

            using var status = await _client.GetAsync(StrataEndpoints.V1Status, sendCredential: true, ct).ConfigureAwait(false);
            if (status.IsSuccess && status.Json!.RootElement.ValueKind == JsonValueKind.Object)
            {
                return (StrataParser.ParseLegacy(metrics.Json.RootElement, status.Json.RootElement), null);
            }

            if (!status.IsNotFound && status.Error is not null)
            {
                return (null, status.Error);
            }

            using var empty = JsonDocument.Parse("{}");
            return (StrataParser.ParseLegacy(metrics.Json.RootElement, empty.RootElement), null);
        }

        if (!metrics.IsNotFound)
        {
            return (null, metrics.Error);
        }

        using var statusOnly = await _client.GetAsync(StrataEndpoints.V1Status, sendCredential: true, ct).ConfigureAwait(false);
        if (statusOnly.IsSuccess && statusOnly.Json!.RootElement.ValueKind == JsonValueKind.Object)
        {
            return (StrataParser.ParseStatusOnly(statusOnly.Json.RootElement), null);
        }

        if (!statusOnly.IsNotFound)
        {
            return (null, statusOnly.Error);
        }

        using var health = await _client.GetAsync(StrataEndpoints.Health, sendCredential: false, ct).ConfigureAwait(false);
        if (health.IsSuccess)
        {
            var service = StrataParser.ParseHealth(health.Json!.RootElement).Service;
            return (null, Incompatible(StrataEndpoints.Health, service == "strata"
                ? "Strata 서버가 응답하지만 알려진 텔레메트리 엔드포인트(/v1/telemetry, /metrics, /v1/status)가 없습니다. Strata 업데이트로 API가 바뀌었을 수 있습니다."
                : $"이 주소의 서버는 Strata가 아닌 것으로 보입니다 (service: {service ?? "알 수 없음"})."));
        }

        return (null, health.Error is { Category: RuntimeErrorCategory.Compatibility } || health.IsNotFound
            ? Incompatible(StrataEndpoints.Health, "이 주소에서 Strata API를 찾을 수 없습니다. 다른 프로그램이 이 포트를 쓰고 있거나 주소가 잘못되었을 수 있습니다.")
            : health.Error);
    }

    private RuntimeSnapshot RegisterFailure(DateTimeOffset now, RuntimeError error)
    {
        _consecutiveFailures++;
        var state = error.Category switch
        {
            RuntimeErrorCategory.Connection => RuntimeConnectionState.Unreachable,
            RuntimeErrorCategory.Timeout => RuntimeConnectionState.Timeout,
            RuntimeErrorCategory.Authentication => RuntimeConnectionState.AuthRequired,
            RuntimeErrorCategory.Compatibility => RuntimeConnectionState.Incompatible,
            RuntimeErrorCategory.Server => RuntimeConnectionState.ServerError,
            RuntimeErrorCategory.Configuration => RuntimeConnectionState.ConfigurationError,
            _ => RuntimeConnectionState.Unreachable,
        };

        var delay = state is RuntimeConnectionState.AuthRequired or RuntimeConnectionState.Incompatible or RuntimeConnectionState.ConfigurationError
            ? SlowRetry
            : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(_consecutiveFailures - 1, 5))));
        _nextAttempt = now + delay;
        if (error.Hint is not null && error.Hint.Contains(StrataHttpClient.KeyVariablePlaceholder, StringComparison.Ordinal))
        {
            error = error with
            {
                Hint = error.Hint.Replace(StrataHttpClient.KeyVariablePlaceholder,
                    string.IsNullOrWhiteSpace(_options.CredentialVariable) ? StrataSettings.DefaultKeyVariable : _options.CredentialVariable, StringComparison.Ordinal),
            };
        }

        LogState(state, error);
        _lastFailure = new RuntimeSnapshot
        {
            RuntimeKind = Kind,
            Timestamp = now,
            State = state,
            Endpoint = _activeBase,
            Error = error,
            LastSuccess = _lastSuccess,
            NextRetryAt = _nextAttempt,
            Server = _lastServer,
            Model = _lastModel,
        };
        _previousCounter = null;
        return _lastFailure;
    }

    private void MaybeRefreshMetadata(DateTimeOffset now, StrataTelemetry telemetry)
    {
        var key = $"{telemetry.InstanceId}|{telemetry.ModelId}|{telemetry.ModelLoaded}";
        lock (_metaGate)
        {
            var due = now - _metadataFetchedAt >= _options.MetadataRefresh || key != _metadataKey;
            if (!due || _metadataTask is { IsCompleted: false } || _disposeCts.IsCancellationRequested)
            {
                return;
            }

            _metadataKey = key;
            _metadataFetchedAt = now;
            _metadataTask = Task.Run(() => RefreshMetadataAsync(_disposeCts.Token));
        }
    }

    private async Task RefreshMetadataAsync(CancellationToken ct)
    {
        StrataHealth? health = null;
        StrataStatus? status = null;
        StrataProps? props = null;
        StrataModels? models = null;
        string? statusNote = null;
        var restricted = false;
        try
        {
            using (var r = await _client.GetAsync(StrataEndpoints.Health, sendCredential: false, ct).ConfigureAwait(false))
            {
                if (r.IsSuccess && r.Json!.RootElement.ValueKind == JsonValueKind.Object)
                {
                    health = StrataParser.ParseHealth(r.Json.RootElement);
                }
            }

            using (var r = await _client.GetAsync(StrataEndpoints.V1Status, sendCredential: true, ct).ConfigureAwait(false))
            {
                if (r.IsSuccess && r.Json!.RootElement.ValueKind == JsonValueKind.Object)
                {
                    status = StrataParser.ParseStatus(r.Json.RootElement);
                }
                else if (r.Error?.Category == RuntimeErrorCategory.Authentication)
                {
                    statusNote = "읽기 전용 텔레메트리 키로는 Strata가 제공하지 않음";
                    restricted = true;
                }
            }

            // /props and /v1/models need the same permission as /v1/status: do not ask again when it was refused.
            using (var r = restricted ? null : await _client.GetAsync(StrataEndpoints.Props, sendCredential: true, ct).ConfigureAwait(false))
            {
                if (r is not null && r.IsSuccess && r.Json!.RootElement.ValueKind == JsonValueKind.Object)
                {
                    props = StrataParser.ParseProps(r.Json.RootElement);
                }
            }

            using (var r = restricted ? null : await _client.GetAsync(StrataEndpoints.Models, sendCredential: true, ct).ConfigureAwait(false))
            {
                if (r is not null && r.IsSuccess && r.Json!.RootElement.ValueKind == JsonValueKind.Object)
                {
                    models = StrataParser.ParseModels(r.Json.RootElement);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn("strata", "Strata 메타데이터 조회 실패", ex);
        }

        lock (_metaGate)
        {
            _metadata = new StrataMetadata(health, status, props, models, statusNote, restricted);
        }
    }

    private RuntimeSnapshot BuildSnapshot(DateTimeOffset now, StrataTelemetry t)
    {
        StrataMetadata meta;
        lock (_metaGate)
        {
            meta = _metadata;
        }

        double? aggregate = null;
        if (t.Rates.OutputTokensCounter is long counter)
        {
            var sampleTime = t.SampledAt ?? now.ToUnixTimeMilliseconds() / 1000.0;
            if (_previousCounter is var (instance, previous, previousTime)
                && instance == t.InstanceId && counter >= previous && sampleTime > previousTime && sampleTime - previousTime <= 30)
            {
                aggregate = (counter - previous) / (sampleTime - previousTime);
            }

            _previousCounter = (t.InstanceId, counter, sampleTime);
        }
        else
        {
            _previousCounter = null;
        }

        var engineVersion = t.EngineVersion ?? meta.Status?.EngineVersion ?? meta.Props?.BuildVersion;
        var server = new RuntimeServerInfo
        {
            Service = meta.Health?.Service ?? t.Service ?? meta.Status?.Service,
            InstanceId = t.InstanceId,
            InstanceState = t.InstanceState,
            BootTimeUnix = t.BootTime ?? meta.Status?.Started,
            UptimeSeconds = meta.Status?.UptimeSeconds,
            EngineVersion = engineVersion,
            EngineVersionNote = engineVersion is null ? meta.StatusNote ?? "Strata가 엔진 버전을 알려주지 않았습니다." : null,
            TelemetrySource = t.Source,
            SchemaVersion = t.Source == "server-v1" ? 1 : null,
            ApiKeyConfigured = meta.Health?.ApiKey,
            MetadataRestricted = meta.Restricted,
        };

        var model = new RuntimeModelInfo
        {
            Id = t.ModelId ?? meta.Status?.Model ?? meta.Health?.Model,
            Loaded = t.ModelLoaded ?? meta.Status?.Loaded ?? meta.Health?.Loaded,
            ContextTokens = t.Context ?? meta.Status?.Context ?? meta.Props?.Context ?? meta.Health?.MaxContext,
            ModelFileName = meta.Props?.ModelFileName,
            Aliases = meta.Models?.Aliases ?? [],
            Vision = meta.Status?.Vision ?? meta.Props?.Vision ?? meta.Health?.Images,
            AutoLoad = meta.Status?.AutoLoad ?? meta.Props?.AutoLoad,
        };

        var work = t.Work with
        {
            SlotsEffective = t.Work.SlotsEffective ?? meta.Status?.SlotsServing ?? meta.Props?.TotalSlots,
            SlotsRequested = t.Work.SlotsRequested ?? meta.Status?.SlotsRequested,
        };

        var state = t.Source == "status-only" ? RuntimeConnectionState.Limited : RuntimeConnectionState.Connected;
        return new RuntimeSnapshot
        {
            RuntimeKind = Kind,
            Timestamp = now,
            State = state,
            Endpoint = _activeBase,
            Error = state == RuntimeConnectionState.Limited
                ? new RuntimeError(RuntimeErrorCategory.Compatibility, "이 Strata 버전은 실시간 텔레메트리를 제공하지 않아 모델·상태 정보만 표시합니다.", "Strata를 업데이트하면 처리량·지연 정보를 볼 수 있습니다.")
                : null,
            LastSuccess = now,
            ServerSampledAtUnix = t.SampledAt,
            Server = server,
            Model = model,
            Work = work,
            Rates = t.Rates with { AggregateOutputTokPerSec = aggregate },
            Slots = t.Slots,
            RecentRequests = t.Recent,
            Totals = t.Totals,
            Activity = t.Activity,
            Settings = meta.Props?.Settings ?? [],
        };
    }

    private static RuntimeError Incompatible(string path, string message)
        => new(RuntimeErrorCategory.Compatibility, message, "Strata 버전과 이 앱의 지원 범위를 확인하세요. 연결 자체는 되었습니다.", path);

    private void LogState(RuntimeConnectionState state, RuntimeError? error)
    {
        if (_lastLoggedState == state)
        {
            return;
        }

        _lastLoggedState = state;
        if (error is null)
        {
            _log.Info("strata", $"상태: {RuntimeStateText.ToKorean(state)} ({_activeBase})");
        }
        else
        {
            _log.Warn("strata", $"상태: {RuntimeStateText.ToKorean(state)} - {error.Message}");
        }
    }

    private sealed record StrataMetadata(StrataHealth? Health, StrataStatus? Status, StrataProps? Props, StrataModels? Models, string? StatusNote,
        bool Restricted = false)
    {
        public static StrataMetadata Empty { get; } = new(null, null, null, null, null);
    }
}
