using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using WinDiag.Core.Events;

namespace WinDiag.Collection.Sources;

/// <summary>
/// Reads the local Windows event logs with the documented Event Log API (System.Diagnostics.Eventing.Reader).
/// Read-only: nothing here writes to, clears or reconfigures a log. Runs with the caller's rights; a channel the
/// user may not read is reported as AccessDenied instead of asking for elevation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogSource : IEventLogSource
{
    private readonly bool _renderMessages;

    public WindowsEventLogSource(bool renderMessages = true)
    {
        _renderMessages = renderMessages;
    }

    public string Name => "Windows 이벤트 로그";
    public bool IsVirtual => false;

    public ChannelProbe Probe(string channel)
    {
        try
        {
            using var session = new EventLogSession();
            bool enabled;
            using (var configuration = new EventLogConfiguration(channel, session))
            {
                enabled = configuration.IsEnabled;
            }

            var info = session.GetLogInformation(channel, PathType.LogName);
            var (oldestId, oldestTime) = Edge(channel, session, reverse: false);
            var (newestId, newestTime) = Edge(channel, session, reverse: true);
            if (!enabled && newestId is null)
            {
                return new ChannelProbe(channel, ChannelAccess.DisabledInWindows, "Windows에서 이 로그가 사용 안 함으로 설정되어 있습니다.");
            }

            return new ChannelProbe(channel, ChannelAccess.Ok, enabled ? null : "Windows에서 사용 안 함 (남아 있는 기록만 읽음)",
                oldestId ?? info.OldestRecordNumber, oldestTime, newestId, newestTime, info.RecordCount);
        }
        catch (EventLogNotFoundException ex)
        {
            return new ChannelProbe(channel, ChannelAccess.NotFound, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ChannelProbe(channel, ChannelAccess.AccessDenied, ex.Message);
        }
        catch (EventLogException ex)
        {
            return new ChannelProbe(channel, IsAccessDenied(ex) ? ChannelAccess.AccessDenied : ChannelAccess.Error, ex.Message);
        }
    }

    public IEnumerable<RawEvent> Read(EventReadRequest request, CancellationToken cancellationToken)
    {
        var query = new EventLogQuery(request.Channel, PathType.LogName, request.Filter.ToStructuredQuery(request.AfterRecordId, request.From, request.To))
        {
            ReverseDirection = false,
        };

        EventLogReader reader;
        try
        {
            reader = new EventLogReader(query);
        }
        catch (EventLogException ex) when (IsAccessDenied(ex))
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        catch (EventLogException ex)
        {
            throw new EventSourceException($"'{request.Channel}' 로그 조회를 시작하지 못했습니다: {ex.Message}", ex);
        }

        using (reader)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EventRecord? record;
                try
                {
                    record = reader.ReadEvent();
                }
                catch (EventLogException ex) when (IsAccessDenied(ex))
                {
                    throw new UnauthorizedAccessException(ex.Message, ex);
                }
                catch (EventLogException ex)
                {
                    throw new EventSourceException($"'{request.Channel}' 로그를 읽는 중 오류: {ex.Message}", ex);
                }

                if (record is null)
                {
                    yield break;
                }

                RawEvent parsed;
                using (record)
                {
                    parsed = Convert(record, request.Channel);
                }

                yield return parsed;
            }
        }
    }

    private RawEvent Convert(EventRecord record, string channel)
    {
        string? message = null;
        string? messageError = null;
        if (_renderMessages)
        {
            try
            {
                message = record.FormatDescription();
                if (message is null)
                {
                    messageError = "공급자의 메시지 파일에서 이 이벤트의 설명을 찾지 못했습니다.";
                }
            }
            catch (EventLogException ex)
            {
                messageError = $"메시지를 만들 수 없습니다: {ex.Message}";
            }
        }

        return EventXmlParser.Parse(record.ToXml(), channel, message, messageError);
    }

    private static (long? Id, DateTimeOffset? Time) Edge(string channel, EventLogSession session, bool reverse)
    {
        using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, "*") { ReverseDirection = reverse, Session = session });
        using var record = reader.ReadEvent();
        if (record is null)
        {
            return (null, null);
        }

        DateTimeOffset? time = record.TimeCreated is DateTime t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : null;
        return (record.RecordId, time);
    }

    /// <summary>ERROR_ACCESS_DENIED (5) as an HRESULT or Win32 code.</summary>
    private static bool IsAccessDenied(EventLogException ex)
        => ex.HResult is 5 or unchecked((int)0x80070005) || ex.InnerException is UnauthorizedAccessException;
}
