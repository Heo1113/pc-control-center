using System.Text.Json.Nodes;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Planning;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatImage(byte[] Png);

public sealed record ChatMessage(ChatRole Role, string Text, IReadOnlyList<ChatImage>? Images = null);

public enum ResponseFormatKind
{
    None,
    JsonObject,
    JsonSchema,
}

public sealed record ModelRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public ResponseFormatKind ResponseFormat { get; init; } = ResponseFormatKind.None;
    public JsonObject? JsonSchema { get; init; }
    public string SchemaName { get; init; } = "operator_plan";
    public int MaxTokens { get; init; } = 2048;
    public double Temperature { get; init; }
}

public enum ModelErrorKind
{
    Connection,
    Timeout,
    Authentication,
    NotFound,
    BadRequest,
    ServerError,
    InvalidResponse,
    ResponseTooLarge,
    Configuration,
    RemoteNotAllowed,
    Cancelled,
}

public sealed record ModelResponse
{
    public bool Success { get; init; }
    public string? Content { get; init; }
    public ModelErrorKind? Error { get; init; }
    public string? ErrorMessage { get; init; }
    public int? StatusCode { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public string? FinishReason { get; init; }

    /// <summary>HTTP requests actually sent (response_format fallbacks count).</summary>
    public int Calls { get; init; } = 1;

    public ResponseFormatKind UsedFormat { get; init; }

    public static ModelResponse Fail(ModelErrorKind kind, string message, TimeSpan elapsed, int? status = null, int calls = 1) =>
        new() { Success = false, Error = kind, ErrorMessage = message, Elapsed = elapsed, StatusCode = status, Calls = calls };
}

/// <summary>What the planner knows about the connected model (from the profile and the last probe).</summary>
public sealed record ModelDescriptor
{
    public required string ProfileName { get; init; }
    public required string ModelId { get; init; }
    public required string BaseUrl { get; init; }
    public bool IsRemote { get; init; }

    /// <summary>Images may be sent: the server reported image input or the image test passed, the profile allows it,
    /// and (for a remote server) the user explicitly allowed screenshots to leave the PC.</summary>
    public bool CanSendImages { get; init; }
    public string VisionReason { get; init; } = "확인 안 됨";
    public StructuredOutputMode StructuredOutput { get; init; } = StructuredOutputMode.Auto;
    public bool IsMock { get; init; }
    public int MaxTokens { get; init; } = 2048;
    public double Temperature { get; init; }

    public string Label => IsMock ? $"MOCK 모델 ({ModelId})" : $"{ProfileName} · {ModelId}";
}

public interface IModelClient
{
    ModelDescriptor Descriptor { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

public static class ModelErrorText
{
    public static string Korean(ModelErrorKind kind) => kind switch
    {
        ModelErrorKind.Connection => "연결 오류(서버가 꺼져 있거나 주소가 틀림)",
        ModelErrorKind.Timeout => "응답 시간 초과",
        ModelErrorKind.Authentication => "인증 오류(API 키 확인)",
        ModelErrorKind.NotFound => "주소나 모델을 찾을 수 없음",
        ModelErrorKind.BadRequest => "요청 형식 거부",
        ModelErrorKind.ServerError => "서버 오류",
        ModelErrorKind.InvalidResponse => "응답 형식 오류(호환되지 않는 서버)",
        ModelErrorKind.ResponseTooLarge => "응답이 너무 큼",
        ModelErrorKind.Configuration => "설정 오류",
        ModelErrorKind.RemoteNotAllowed => "외부 전송 미허용",
        ModelErrorKind.Cancelled => "취소됨",
        _ => kind.ToString(),
    };
}
