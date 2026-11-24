using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using WinDiag.Core.Time;

namespace WinDiag.Core.Events;

/// <summary>
/// Parses Windows event XML (EventRecord.ToXml(), "wevtutil qe /f:xml" or "/f:RenderedXml") into <see cref="RawEvent"/>.
/// Platform-neutral, so classification is tested on any OS with recorded fixtures. DTDs and external entities are refused.
/// </summary>
public static class EventXmlParser
{
    public const int MaxValueLength = 4000;
    public const int MaxDataItems = 200;
    public const int MaxMessageLength = 16_000;

    private static readonly XmlReaderSettings FragmentSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        ConformanceLevel = ConformanceLevel.Fragment,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>Parses one &lt;Event&gt; element.</summary>
    /// <param name="channelOverride">Used when the XML has no Channel element.</param>
    /// <param name="message">Message rendered separately (EventRecord.FormatDescription); wins over RenderingInfo.</param>
    public static RawEvent Parse(string xml, string? channelOverride = null, string? message = null, string? messageError = null)
    {
        using var reader = XmlReader.Create(new StringReader(xml), FragmentSettings);
        reader.MoveToContent();
        var element = (XElement)XNode.ReadFrom(reader);
        return FromElement(element, channelOverride, message, messageError);
    }

    /// <summary>Parses a sequence of &lt;Event&gt; elements, with or without a root element (wevtutil output has none).</summary>
    public static IEnumerable<RawEvent> ParseMany(TextReader text, string? channelOverride = null)
    {
        using var reader = XmlReader.Create(text, FragmentSettings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Event")
            {
                // ReadFrom leaves the reader on the node after the element, so no extra Read here.
                var element = (XElement)XNode.ReadFrom(reader);
                yield return FromElement(element, channelOverride, null, null);
            }
            else
            {
                reader.Read();
            }
        }
    }

    public static RawEvent FromElement(XElement evt, string? channelOverride, string? message, string? messageError)
    {
        var system = Child(evt, "System") ?? throw new FormatException("이벤트 XML에 System 요소가 없습니다.");
        var provider = Child(system, "Provider");
        var providerName = Attr(provider, "Name");
        var eventSourceName = Attr(provider, "EventSourceName");
        if (string.IsNullOrWhiteSpace(providerName))
        {
            providerName = eventSourceName ?? throw new FormatException("이벤트 XML에 Provider 이름이 없습니다.");
        }

        var eventIdElement = Child(system, "EventID") ?? throw new FormatException("이벤트 XML에 EventID가 없습니다.");
        var eventId = ParseInt(eventIdElement.Value) ?? throw new FormatException($"EventID를 읽을 수 없습니다: {eventIdElement.Value}");
        var timeText = Attr(Child(system, "TimeCreated"), "SystemTime");
        if (timeText is null || !IsoTime.TryParse(timeText, out var created))
        {
            throw new FormatException($"TimeCreated를 읽을 수 없습니다: {timeText}");
        }

        var recordText = Child(system, "EventRecordID")?.Value;
        var recordId = ParseLong(recordText) ?? throw new FormatException($"EventRecordID를 읽을 수 없습니다: {recordText}");
        var channel = Child(system, "Channel")?.Value;
        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = channelOverride ?? throw new FormatException("이벤트 XML에 Channel이 없습니다.");
        }

        var execution = Child(system, "Execution");
        var rendering = Child(evt, "RenderingInfo");
        var renderedMessage = message ?? Child(rendering, "Message")?.Value;

        return new RawEvent
        {
            Channel = channel.Trim(),
            Provider = providerName.Trim(),
            ProviderGuid = Attr(provider, "Guid"),
            EventSourceName = string.IsNullOrWhiteSpace(eventSourceName) ? null : eventSourceName.Trim(),
            EventId = eventId,
            Qualifiers = ParseInt(Attr(eventIdElement, "Qualifiers")),
            Version = ParseInt(Child(system, "Version")?.Value),
            Level = ParseInt(Child(system, "Level")?.Value) ?? 4,
            Task = ParseInt(Child(system, "Task")?.Value),
            Opcode = ParseInt(Child(system, "Opcode")?.Value),
            Keywords = ParseHexLong(Child(system, "Keywords")?.Value),
            RecordId = recordId,
            TimeCreated = created,
            ProcessId = ParseInt(Attr(execution, "ProcessID")),
            ThreadId = ParseInt(Attr(execution, "ThreadID")),
            Computer = Child(system, "Computer")?.Value,
            UserSid = Attr(Child(system, "Security"), "UserID"),
            Data = ReadData(evt),
            Message = Truncate(NormalizeMessage(renderedMessage), MaxMessageLength),
            MessageError = messageError,
        };
    }

    private static List<EventDataItem> ReadData(XElement evt)
    {
        var items = new List<EventDataItem>();
        var eventData = Child(evt, "EventData");
        if (eventData is not null)
        {
            foreach (var data in eventData.Elements())
            {
                if (items.Count >= MaxDataItems)
                {
                    break;
                }

                if (data.Name.LocalName == "Data")
                {
                    var name = Attr(data, "Name");
                    items.Add(new EventDataItem(string.IsNullOrEmpty(name) ? null : name, Truncate(data.Value, MaxValueLength) ?? ""));
                }
                else if (data.Name.LocalName == "Binary")
                {
                    // Raw binary is not kept; its size is (some providers put dump-like content here).
                    items.Add(new EventDataItem("(Binary)", $"{data.Value.Length / 2} bytes"));
                }
            }
        }

        var userData = Child(evt, "UserData");
        if (userData is not null)
        {
            foreach (var leaf in userData.Descendants().Where(d => !d.HasElements))
            {
                if (items.Count >= MaxDataItems)
                {
                    break;
                }

                items.Add(new EventDataItem(leaf.Name.LocalName, Truncate(leaf.Value, MaxValueLength) ?? ""));
            }
        }

        return items;
    }

    private static XElement? Child(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Attr(XElement? element, string name)
        => element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static long? ParseLong(string? text)
        => long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? ParseHexLong(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? unchecked((long)hex) : null;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    }

    internal static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : string.Concat(value.AsSpan(0, max), " …(잘림)");
}
