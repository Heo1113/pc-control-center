using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.ModelProviders;

/// <summary>Builds the model client for a profile. A missing key, a forbidden remote URL or no selected model give a
/// readable problem instead of a client; the app keeps working without a model.</summary>
public static class ModelClientFactory
{
    public static (IModelClient? Client, string? Problem) Create(ModelProfile profile, ISecretStore secrets, ProbeResult? lastProbe, HttpMessageHandler? handler = null, IOperatorLog? log = null, ResponseFormatKind? knownFormatCeiling = null)
    {
        if (profile.Kind == ProviderKind.Mock)
        {
            return (new MockModelClient(), null);
        }

        var endpoint = EndpointPolicy.Check(profile);
        if (endpoint.Problem is not null)
        {
            return (null, endpoint.Problem);
        }

        var (key, keyProblem) = ApiKeyResolver.Resolve(profile, secrets);
        if (keyProblem is not null)
        {
            return (null, keyProblem);
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            return (null, "모델이 선택되지 않았습니다. 모델 화면에서 [연결 확인] 후 모델을 고르세요.");
        }

        var (canSend, reason) = DecideVision(profile, lastProbe, endpoint);
        var descriptor = new ModelDescriptor
        {
            ProfileName = profile.Name,
            ModelId = profile.Model,
            BaseUrl = endpoint.BaseUri!.ToString(),
            IsRemote = endpoint.IsRemote,
            CanSendImages = canSend,
            VisionReason = reason,
            StructuredOutput = profile.StructuredOutput,
            MaxTokens = profile.MaxTokens,
            Temperature = profile.Temperature,
        };
        return (new OpenAiCompatibleClient(profile, key, descriptor, handler, log, knownFormatCeiling: knownFormatCeiling), null);
    }

    /// <summary>Client for the image test only: the pictures are synthetic, so they may be sent before vision is confirmed.</summary>
    public static (IModelClient? Client, string? Problem) CreateForVisionTest(ModelProfile profile, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        var (client, problem) = Create(profile, secrets, null, handler);
        if (client is not OpenAiCompatibleClient)
        {
            return (null, problem ?? "이 연결에서는 이미지 테스트를 할 수 없습니다.");
        }

        (client as IDisposable)?.Dispose();
        var (key, _) = ApiKeyResolver.Resolve(profile, secrets);
        var endpoint = EndpointPolicy.Check(profile);
        var descriptor = new ModelDescriptor
        {
            ProfileName = profile.Name,
            ModelId = profile.Model!,
            BaseUrl = endpoint.BaseUri!.ToString(),
            IsRemote = endpoint.IsRemote,
            CanSendImages = false,
            MaxTokens = profile.MaxTokens,
        };
        return (new OpenAiCompatibleClient(profile, key, descriptor, handler, allowImagesRegardless: true), null);
    }

    public static (bool CanSend, string Reason) DecideVision(ModelProfile profile, ProbeResult? probe, EndpointCheck endpoint)
    {
        if (profile.Vision == VisionPreference.Disabled)
        {
            return (false, "프로필에서 화면 이미지 사용을 껐습니다.");
        }

        if (endpoint.IsRemote && !profile.AllowRemoteScreenshots)
        {
            return (false, "이 PC 밖의 서버로 화면 이미지를 보내는 것을 허용하지 않았습니다.");
        }

        if (profile.VisionVerified && string.Equals(profile.VisionVerifiedModel, profile.Model, StringComparison.Ordinal))
        {
            return (true, "이미지 입력 실제 테스트 통과");
        }

        return probe?.SelectedModelAcceptsImages switch
        {
            true => (true, probe.VisionReason + " (실제 테스트 권장)"),
            false => (false, probe.VisionReason),
            _ => (false, "이미지 지원 여부를 확인하지 못했습니다. 모델 화면의 [이미지 입력 테스트]로 확인하세요."),
        };
    }
}
