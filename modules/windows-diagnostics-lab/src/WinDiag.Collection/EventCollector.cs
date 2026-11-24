using System.Diagnostics;
using LocalAiPerf.Core.Diagnostics;
using WinDiag.Collection.Sources;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Storage;

namespace WinDiag.Collection;

public sealed record ChannelReport(
    string Channel,
    ChannelStatus Status,
    string? Message,
    int Read,
    int Inserted,
    int Duplicates,
    IReadOnlyList<CollectionGap> Gaps,
    TimeSpan Duration);

public sealed record CollectionReport(IReadOnlyList<ChannelReport> Channels, bool IsHistoryImport)
{
    public int Inserted => Channels.Sum(c => c.Inserted);
    public int Read => Channels.Sum(c => c.Read);
    public int Duplicates => Channels.Sum(c => c.Duplicates);
}

public sealed record CollectionProgress(string Channel, int Read, int Inserted);

/// <summary>New events stored by a scan, with the IDs needed to load them.</summary>
public sealed record StoredBatch(IReadOnlyList<long> EventIds, bool IsLive);

/// <summary>
/// Reads event log channels into the store. Progress is kept per channel as "scanned up to record N", saved in the same
/// transaction as the events, so a restart continues where it stopped and re-reading never duplicates events.
/// Missing ranges (overwritten or cleared logs, retention limits, unreadable channels) are recorded as gaps.
/// </summary>
public sealed class EventCollector
{
    private const int BatchSize = 500;

    private readonly IEventLogSource _source;
    private readonly DiagnosticsStore _store;
    private readonly EventClassifier _classifier;
    private readonly PrivacyMasker _masker;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="trackProgress">
    /// False for imports and the virtual scenario: their events are stored, but the per-channel position and gap
    /// records of the real logs are never touched (an imported "System" log must not move the live "System" position).
    /// </param>
    /// <param name="sourceOverride">Source label stored with every event (for example <see cref="EventSources.Import"/>).</param>
    public EventCollector(IEventLogSource source, DiagnosticsStore store, EventClassifier classifier, PrivacyMasker masker,
        Func<AppSettings> settings, TimeProvider? time = null, IAppLogger? log = null, bool trackProgress = true, string? sourceOverride = null)
    {
        TrackProgress = trackProgress;
        _sourceOverride = sourceOverride;
        _source = source;
        _store = store;
        _classifier = classifier;
        _masker = masker;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public IEventLogSource Source => _source;

    public bool TrackProgress { get; }

    private readonly string? _sourceOverride;

    /// <summary>Raised (on the collecting thread) after a scan stored new events.</summary>
    public event Action<StoredBatch>? EventsStored;

    /// <summary>
    /// Reads every enabled channel from where it stopped. A channel never scanned before is read back
    /// <see cref="CollectionSettings.HistoryDays"/> days first.
    /// </summary>
    public async Task<CollectionReport> PollAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var reports = new List<ChannelReport>();
            foreach (var channel in settings.Collection.Channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reports.Add(channel.Enabled
                    ? await Task.Run(() => ScanChannel(channel.Name, settings, history: null, progress: null, cancellationToken), cancellationToken).ConfigureAwait(false)
                    : MarkDisabled(channel.Name));
            }

            return new CollectionReport(reports, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads a past range of every enabled channel (for example "the last 90 days"). Existing events are skipped.</summary>
    public async Task<CollectionReport> ImportHistoryAsync(DateTimeOffset from, DateTimeOffset to, IProgress<CollectionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var reports = new List<ChannelReport>();
            foreach (var channel in settings.Collection.Channels.Where(c => c.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                reports.Add(await Task.Run(() => ScanChannel(channel.Name, settings, (from, to), progress, cancellationToken), cancellationToken).ConfigureAwait(false));
            }

            return new CollectionReport(reports, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ChannelReport MarkDisabled(string channel)
    {
        var state = _store.GetChannelState(channel) ?? new ChannelState { Channel = channel };
        if (state.Status != ChannelStatus.Disabled)
        {
            _store.SaveChannelState(state with { Status = ChannelStatus.Disabled, StatusMessage = ChannelStatusText.Explain(ChannelStatus.Disabled, channel) });
        }

        return new ChannelReport(channel, ChannelStatus.Disabled, null, 0, 0, 0, [], TimeSpan.Zero);
    }

    private ChannelReport ScanChannel(string channel, AppSettings settings, (DateTimeOffset From, DateTimeOffset To)? history,
        IProgress<CollectionProgress>? progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var previous = (TrackProgress ? _store.GetChannelState(channel) : null) ?? new ChannelState { Channel = channel };
        var gaps = new List<CollectionGap>();
        var probe = _source.Probe(channel);
        if (probe.Access != ChannelAccess.Ok)
        {
            var status = probe.ToStatus();
            var explanation = ChannelStatusText.Explain(status, channel);
            if (previous.Status != status)
            {
                // Recorded once per change, not on every poll.
                if (status is ChannelStatus.AccessDenied or ChannelStatus.Error)
                {
                    gaps.Add(AddGap(channel, status == ChannelStatus.AccessDenied ? GapKinds.AccessDenied : GapKinds.ReadFailed, now, null, now,
                        $"{explanation} ({probe.Message})"));
                }

                _log.Warn("collector", $"{channel}: {ChannelStatusText.Name(status)} - {probe.Message}");
            }

            if (TrackProgress)
            {
                _store.SaveChannelState(previous with { Status = status, StatusMessage = $"{explanation} {probe.Message}".Trim(), LastScan = now });
            }

            return new ChannelReport(channel, status, probe.Message, 0, 0, 0, gaps, started.Elapsed);
        }

        var firstScan = previous.LastRecordId is null;
        long? after = null;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        var resetMarker = false;
        if (history is { } range)
        {
            from = range.From;
            to = range.To;
        }
        else if (firstScan)
        {
            from = now.AddDays(-settings.Collection.HistoryDays);
        }
        else
        {
            after = previous.LastRecordId;
            if (probe.NewestRecordId is long newest && newest < previous.LastRecordId)
            {
                // Record numbers went backwards: the log was recreated. Everything now in it is new.
                gaps.Add(AddGap(channel, GapKinds.LogReset, previous.LastScan, probe.OldestTime ?? now, now,
                    "레코드 번호가 이전보다 작아졌습니다. 로그가 지워졌거나 다시 만들어진 것으로 보입니다. 그 사이의 기록은 확인할 수 없습니다."));
                after = null;
                resetMarker = true;
            }
            else if (probe.OldestRecordId is long oldest && oldest > previous.LastRecordId + 1)
            {
                gaps.Add(AddGap(channel, GapKinds.LogOverwritten, previous.LastScan ?? previous.LastEventTime, probe.OldestTime, now,
                    $"이 로그의 가장 오래된 기록(레코드 {oldest})이 마지막으로 읽은 기록({previous.LastRecordId}) 이후입니다. " +
                    "그 사이의 기록은 앱이 읽기 전에 Windows가 덮어써서 확인할 수 없습니다 (로그 최대 크기 도달)."));
            }
        }

        if (from is { } requested && probe.OldestTime is { } available && available > requested && (history is not null || firstScan))
        {
            var alreadyKnown = previous.HistoryFrom is { } h && h <= requested;
            if (!alreadyKnown)
            {
                gaps.Add(AddGap(channel, GapKinds.LogRetention, requested, available, now,
                    $"요청한 기간의 시작보다 이 로그의 가장 오래된 기록이 늦습니다. 그 이전 기록은 Windows에 남아 있지 않습니다 (로그 크기·보존 정책)."));
            }
        }

        var filter = EventFilter.Build(channel, _classifier.Catalog, settings.Collection);
        var request = new EventReadRequest { Channel = channel, Filter = filter, AfterRecordId = after, From = from, To = to };
        var mask = settings.Privacy.MaskAtCollection;
        var source = history is null && !firstScan ? EventSources.Live : EventSources.History;
        if (_source.IsVirtual)
        {
            source = EventSources.Virtual;
        }
        else if (_sourceOverride is not null)
        {
            source = _sourceOverride;
        }

        var maxSeen = resetMarker ? 0 : previous.LastRecordId ?? 0;
        DateTimeOffset? lastEventTime = previous.LastEventTime;
        var batch = new List<NewEvent>(BatchSize);
        int read = 0, inserted = 0, duplicates = 0;
        var newIds = new List<long>();
        var state = previous;
        ChannelStatus finalStatus = ChannelStatus.Ok;
        string? finalMessage = probe.Message;

        void Flush(bool final)
        {
            var updated = state with
            {
                Status = ChannelStatus.Ok,
                StatusMessage = probe.Message,
                // History imports of an already tracked channel do not move the incremental marker.
                LastRecordId = history is not null && !firstScan ? previous.LastRecordId : (maxSeen > 0 ? maxSeen : state.LastRecordId),
                LastEventTime = history is not null && !firstScan ? previous.LastEventTime : lastEventTime,
                LastScan = final ? now : state.LastScan,
                OldestRecordId = probe.OldestRecordId,
                OldestEventTime = probe.OldestTime,
                RecordCount = probe.RecordCount,
                HistoryFrom = from is { } f && (history is not null || firstScan) ? Min(previous.HistoryFrom, f) : previous.HistoryFrom,
            };
            if (final && (history is null || firstScan) && probe.NewestRecordId is long newestAtProbe && newestAtProbe > (updated.LastRecordId ?? 0))
            {
                // Records up to the probe were all covered by the query (non-matching ones are simply not collected).
                updated = updated with { LastRecordId = newestAtProbe };
            }

            if (final && (history is null || firstScan) && updated.LastRecordId is null)
            {
                // An empty log: everything written from now on is new.
                updated = updated with { LastRecordId = 0 };
            }

            var result = _store.InsertEvents(batch, TrackProgress ? updated : null);
            inserted += result.Inserted;
            duplicates += result.Duplicates;
            newIds.AddRange(result.NewIds);
            if (result.Inserted > 0)
            {
                RecordLogClears(batch);
            }

            batch.Clear();
            state = updated;
            progress?.Report(new CollectionProgress(channel, read, inserted));
        }

        try
        {
            foreach (var raw in _source.Read(request, cancellationToken))
            {
                read++;
                var masked = _masker.Mask(raw);
                var classification = _classifier.Classify(masked);
                batch.Add(new NewEvent(mask ? masked : raw, classification, source, mask));
                if (history is null || firstScan)
                {
                    maxSeen = Math.Max(maxSeen, raw.RecordId);
                    if (lastEventTime is null || raw.TimeCreated > lastEventTime)
                    {
                        lastEventTime = raw.TimeCreated;
                    }
                }

                if (batch.Count >= BatchSize)
                {
                    Flush(final: false);
                }
            }

            Flush(final: true);
        }
        catch (OperationCanceledException)
        {
            // Keep what was read so far; the marker only covers stored events.
            Flush(final: false);
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventSourceException or IOException or FormatException or InvalidOperationException)
        {
            Flush(final: false);
            finalStatus = ex is UnauthorizedAccessException ? ChannelStatus.AccessDenied : ChannelStatus.Error;
            finalMessage = ex.Message;
            gaps.Add(AddGap(channel, finalStatus == ChannelStatus.AccessDenied ? GapKinds.AccessDenied : GapKinds.ReadFailed, state.LastEventTime, now, now,
                $"{ChannelStatusText.Explain(finalStatus, channel)} ({ex.Message}) 읽은 데까지는 저장했고 다음 확인 때 이어서 읽습니다."));
            if (TrackProgress)
            {
                _store.SaveChannelState(state with { Status = finalStatus, StatusMessage = ex.Message, LastScan = now });
            }

            _log.Warn("collector", $"{channel} 읽기 중단", ex);
        }

        if (newIds.Count > 0)
        {
            try
            {
                EventsStored?.Invoke(new StoredBatch(newIds, IsLive: source == EventSources.Live));
            }
            catch (Exception ex)
            {
                _log.Error("collector", "새 이벤트 처리기 예외", ex);
            }
        }

        return new ChannelReport(channel, finalStatus, finalMessage, read, inserted, duplicates, gaps, started.Elapsed);
    }

    private void RecordLogClears(IReadOnlyList<NewEvent> batch)
    {
        if (!batch.Any(b => b.Classification.Category == EventCategories.LogCleared))
        {
            return;
        }

        foreach (var cleared in batch.Where(b => b.Classification.Category == EventCategories.LogCleared))
        {
            var clearedChannel = cleared.Event.Get("Channel") ?? cleared.Event.Channel;
            if (_store.GapExists(clearedChannel, GapKinds.LogCleared, cleared.Event.TimeCreated))
            {
                continue;
            }

            AddGap(clearedChannel, GapKinds.LogCleared, null, cleared.Event.TimeCreated, _time.GetUtcNow(),
                $"'{clearedChannel}' 로그가 이 시각에 지워졌습니다. 그 이전의 기록은 Windows에 남아 있지 않습니다.");
        }
    }

    private CollectionGap AddGap(string scope, string kind, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset detected, string detail)
    {
        var gap = new CollectionGap { Scope = scope, Kind = kind, From = from, To = to, Detected = detected, Detail = detail };
        if (!TrackProgress)
        {
            return gap; // gaps describe the real logs only
        }

        var id = _store.AddGap(gap);
        return gap with { Id = id };
    }

    private static DateTimeOffset Min(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;
}
