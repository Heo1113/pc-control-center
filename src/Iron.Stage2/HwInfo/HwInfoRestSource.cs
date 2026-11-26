using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using IronPoc;

namespace Iron.Stage2.HwInfo;

/// <summary>What IRON shows from HWiNFO (everything else comes from NVML and Windows). Null = not reported.</summary>
public sealed record HwInfoValues(DateTimeOffset At, double? CpuTctlC, double? CpuPackagePowerW, double? NvmeMaxTempC, Stage2WireView WireView);

public sealed record HwInfoStats(long Requests, long Ok, long Failed, long SkippedLate, double LongestFailMs, long SdbLoads);

/// <summary>
/// Read-only HWiNFO REST client for the IRON window (GET /api/sensors/sdb and /api/sensors/read on a loopback address).
/// RC3.2 timing: one request at a time; connect limit = interval (0.25–1 s), request limit = 2 × interval (1–5 s); a period
/// that has already passed is skipped, never caught up; after failures the next try waits longer (up to 5 s). Values
/// older than three intervals are not shown (state <see cref="SourceState.Stale"/>). HWiNFO going away never makes IRON
/// open the WireView USB port: WireView simply becomes N/A.
/// </summary>
public sealed class HwInfoRestSource : IDisposable
{
    private static readonly string[] AllowedPaths = ["/api/sensors/sdb", "/api/sensors/read"];
    private readonly Uri _base;
    private readonly TimeSpan _interval;
    private readonly IronConfig _config;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _requestLimit;
    private Task? _loop;
    private volatile HwInfoValues? _latest;
    private volatile SourceStatus _status;
    private Dictionary<string, SdbMeta>? _sdb;
    private long _requests;
    private long _ok;
    private long _failed;
    private long _skipped;
    private long _sdbLoads;
    private double _longestFail;

    public HwInfoRestSource(Uri baseUrl, TimeSpan interval, IronConfig? config = null, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        if (!baseUrl.IsLoopback)
        {
            throw new ArgumentException("HWiNFO REST는 루프백 주소만 허용합니다(LAN 접근은 별도 승인 전 금지).", nameof(baseUrl));
        }

        if (baseUrl.Port == 8080)
        {
            throw new ArgumentException("8080은 Basalt 운영 포트입니다. 사용하지 않습니다.", nameof(baseUrl));
        }

        _base = baseUrl;
        _interval = interval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : interval;
        _config = config ?? EmbeddedConfig();
        _time = time ?? TimeProvider.System;
        var (connect, request) = Timeouts(_interval);
        _requestLimit = request;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = connect }) { Timeout = TimeSpan.FromSeconds(5) };
        _status = new SourceStatus("HWiNFO REST", SourceState.Off, baseUrl.ToString());
    }

    /// <summary>Connect: the interval (0.25–1 s). Request: two intervals (1–5 s). Same rule as the RC3.2 PoC and collector.</summary>
    public static (TimeSpan Connect, TimeSpan Request) Timeouts(TimeSpan interval) =>
        (TimeSpan.FromMilliseconds(Math.Clamp(interval.TotalMilliseconds, 250, 1000)), TimeSpan.FromMilliseconds(Math.Clamp(interval.TotalMilliseconds * 2, 1000, 5000)));

    public SourceStatus Status => _status;

    /// <summary>stage2-rc3/config (mapping rules, Stage 1 thresholds, gates), embedded in this assembly.</summary>
    public static IronConfig EmbeddedConfig()
    {
        static JsonElement L(string name)
        {
            using var s = typeof(HwInfoRestSource).Assembly.GetManifestResourceStream("Iron.Stage2.hwinfo." + name)
                          ?? throw new InvalidOperationException("HWiNFO 매핑 설정 리소스가 없습니다: " + name);
            return JsonDocument.Parse(s).RootElement.Clone();
        }

        return new IronConfig { Rules = L("mapping_rules.json"), Thresholds = L("iron_stage1_thresholds.json"), Gates = L("gates.json") };
    }

    /// <summary>The newest values, or null when none is younger than three intervals.</summary>
    public HwInfoValues? Latest
    {
        get
        {
            var v = _latest;
            return v is not null && _time.GetUtcNow() - v.At <= _interval * 3 ? v : null;
        }
    }

    public HwInfoStats Stats => new(Interlocked.Read(ref _requests), Interlocked.Read(ref _ok), Interlocked.Read(ref _failed), Interlocked.Read(ref _skipped),
        _longestFail, Interlocked.Read(ref _sdbLoads));

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var due = TimeSpan.Zero;
        var consecutiveFailures = 0;
        _status = _status with { State = SourceState.Connecting };
        while (!ct.IsCancellationRequested)
        {
            // never catch up: a period that has already passed is skipped (counted), the next one is waited for
            var now = clock.Elapsed;
            if (now - due >= _interval)
            {
                var missed = (long)((now - due) / _interval);
                Interlocked.Add(ref _skipped, missed);
                due += _interval * missed;
            }

            if (due > now)
            {
                try
                {
                    await Task.Delay(due - now, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            var started = clock.Elapsed;
            try
            {
                _sdb ??= await LoadSdbAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _requests);
                using var doc = await GetJsonAsync("/api/sensors/read", _requestLimit, ct).ConfigureAwait(false);
                _latest = Map(doc.RootElement, _sdb, _time.GetUtcNow());
                Interlocked.Increment(ref _ok);
                consecutiveFailures = 0;
                _status = new SourceStatus("HWiNFO REST", SourceState.Live, _base.ToString());
                due += _interval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or KeyNotFoundException
                                           or InvalidOperationException or FormatException)
            {
                Interlocked.Increment(ref _failed);
                var took = (clock.Elapsed - started).TotalMilliseconds;
                if (took > _longestFail)
                {
                    _longestFail = took;
                }

                consecutiveFailures++;
                // the periods the failing request held are skipped (counted), never sent later
                Interlocked.Add(ref _skipped, (long)(TimeSpan.FromMilliseconds(took) / _interval));
                _sdb = null; // HWiNFO may have restarted with another sensor list: read the SDB again on reconnect
                var reason = ex is InvalidDataException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                _status = new SourceStatus("HWiNFO REST", ex is InvalidDataException ? SourceState.Unavailable : Latest is null ? SourceState.Offline : SourceState.Stale,
                    Short(reason, 200));
                // back off while it is down: 1, 2, 4, (max 5) intervals — one request at a time, nothing queued
                var backoff = TimeSpan.FromMilliseconds(Math.Min(_interval.TotalMilliseconds * Math.Pow(2, Math.Min(consecutiveFailures - 1, 3)), 5000));
                due = clock.Elapsed + backoff;
            }
        }
    }

    private async Task<Dictionary<string, SdbMeta>> LoadSdbAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/api/sensors/sdb", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        Interlocked.Increment(ref _sdbLoads);
        return IronModel.ParseSdb(doc.RootElement.Clone()); // throws InvalidDataException for an SDB version other than 1
    }

    private async Task<JsonDocument> GetJsonAsync(string path, TimeSpan limit, CancellationToken ct)
    {
        if (!AllowedPaths.Contains(path))
        {
            throw new InvalidOperationException("허용되지 않은 경로: " + path);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        using var resp = await _http.GetAsync(new Uri(_base, path), cts.Token).ConfigureAwait(false);
        if ((int)resp.StatusCode != 200)
        {
            throw new HttpRequestException("HTTP " + (int)resp.StatusCode);
        }

        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    /// <summary>One /api/sensors/read through the RC3 mapping (units checked against the SDB) to the values IRON shows.</summary>
    internal HwInfoValues Map(JsonElement read, Dictionary<string, SdbMeta> sdb, DateTimeOffset at) => Extract(IronModel.MapFrame(IronModel.ParseRead(read), sdb, "LIVE", _config), at);

    internal static HwInfoValues Extract(JsonObject model, DateTimeOffset at)
    {
        static double? D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        var cpu = model["cpu"];
        var nvme = model["nvme"]?.AsArray().Select(n => D(n?["temp"])).Where(t => t is not null).Select(t => t!.Value).ToList() ?? [];
        var wv = model["wireview"];
        var wire = Stage2WireView.None;
        if (wv?["present"]?.GetValue<bool>() == true)
        {
            var completeness = wv["completeness"]?.GetValue<string>() ?? "ABSENT";
            var pins = wv["pins"]?.AsArray() ?? [];
            var amps = pins.Select(p => D(p?["currentA"])).ToList();
            var volts = pins.Select(p => D(p?["voltageV"])).Where(v => v is not null).Select(v => v!.Value).ToList();
            var temps = wv["temperaturesC"]?.AsArray().Select(t => (Name: t?["name"]?.GetValue<string>() ?? "", C: D(t?["valueC"]))).ToList() ?? [];
            // which connector side a temperature is: only when its name says so (In/Inlet/Input, Out/Outlet/Output); the real
            // names come from the user's SDB inventory (G2) — an unnamed single "Connector Temperature" is not guessed
            double? Temp(string pattern) => temps.FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t.Name, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant) && t.C is not null).C;
            wire = new Stage2WireView(
                Connected: true,
                PowerW: D(wv["totalW"]),
                VoltageV: volts.Count > 0 ? Math.Round(volts.Average(), 2) : null,
                ConnectorInC: Temp(@"\b(In|Inlet|Input)\b"),
                ConnectorOutC: Temp(@"\b(Out|Outlet|Output)\b"),
                // all six pins or none: a partial set is never shown as pins (Stage 1 rule: no deviation from partial data)
                PinCurrentsA: completeness == "COMPLETE" ? amps.Select(a => a!.Value).ToList() : [],
                Completeness: completeness);
        }

        return new HwInfoValues(at, D(cpu?["tctlC"]), D(cpu?["packagePowerW"]), nvme.Count > 0 ? nvme.Max() : null, wire);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _http.Dispose();
        _stop.Dispose();
    }
}
