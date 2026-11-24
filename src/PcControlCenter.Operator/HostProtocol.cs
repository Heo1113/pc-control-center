using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAiOperator.Core.Automation;

namespace PcControlCenter.Operator;

/// <summary>
/// The closed set of requests the shell can send to the Operator host. They map one to one to project E's
/// <see cref="IDesktopAutomation"/> members plus control messages; there is no request that runs a command, a script or
/// an arbitrary program. Transport: one JSON object per line over the host process's own stdin/stdout, so only the
/// parent process that started the host can talk to it (no named pipe or port another process could open).
/// </summary>
public enum HostOp
{
    Hello,
    Status,
    ListWindows,
    GetWindow,
    GetForegroundWindow,
    CaptureTree,
    FindElements,
    ElementFromPoint,
    FocusWindow,
    Perform,
    Launch,
    CaptureImage,
    Cancel,
    EmergencyStop,
    Resume,
    Shutdown,
}

public enum HostErrorKind
{
    /// <summary>The host's own guard refused the request (protected/blocked/elevated target, emergency stop).</summary>
    Refused,
    Failed,
    Cancelled,
    BadRequest,
}

public sealed record HostRequest(long Id, HostOp Op, JsonElement? Args = null);

public sealed record HostResponse(long Id, bool Ok, JsonElement? Result = null, string? Error = null, HostErrorKind? Kind = null);

/// <param name="DesktopOwnProcessId">The host's own process id as the desktop implementation reports it.</param>
public sealed record HelloResult(int ProtocolVersion, int ProcessId, string DesktopName, bool IsMock, DesktopStatus Status, bool HostElevated, bool Latched,
    int DesktopOwnProcessId);

public sealed record HandleArgs(long Handle);

public sealed record CaptureTreeArgs(long Handle, TreeCaptureOptions Options);

public sealed record FindElementsArgs(long Handle, ElementQuery Query);

public sealed record PointArgs(PixelPoint Point);

public sealed record PerformArgs(ActionRequest Request);

public sealed record LaunchArgs(string Executable, IReadOnlyList<string> ExpectedProcessNames, double TimeoutSeconds);

public sealed record CaptureImageArgs(long Handle, int MaxWidth);

public sealed record CancelArgs(long RequestId);

public static class HostProtocol
{
    public const int Version = 1;

    /// <summary>Longest accepted line (a 1280 px window screenshot is a few MB as base64).</summary>
    public const int MaxLineChars = 64 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 512,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    public static T? Read<T>(JsonElement? element) => element is { } e && e.ValueKind != JsonValueKind.Null ? e.Deserialize<T>(Json) : default;
}

/// <summary>The host refused or failed a request.</summary>
public sealed class HostCallException(HostErrorKind kind, string message) : Exception(message)
{
    public HostErrorKind Kind { get; } = kind;
}

/// <summary>The host process is not running or not answering. Derives from InvalidOperationException, which E's code already handles.</summary>
public sealed class HostUnavailableException(string message) : InvalidOperationException(message);
