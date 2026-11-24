namespace WindowsAiOperator.Core.Settings;

/// <summary>
/// Stores API keys outside settings.json. The Windows implementation encrypts with DPAPI (current user); values are
/// never logged, exported, stored in the database or put into a prompt.
/// </summary>
public interface ISecretStore
{
    bool IsAvailable { get; }

    string? Get(string key);

    void Set(string key, string? value);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
    }
}

public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public string? Get(string key) => null;

    public void Set(string key, string? value) => throw new PlatformNotSupportedException("암호화 저장소는 Windows에서만 사용할 수 있습니다.");
}

public static class ApiKeyResolver
{
    public static string SecretKeyFor(ModelProfile profile) => "model-api-key:" + profile.Id;

    /// <summary>The key for a profile, or null with the reason it is missing (the value itself is never returned in messages).</summary>
    public static (string? Key, string? Problem) Resolve(ModelProfile profile, ISecretStore store)
    {
        switch (profile.KeySource)
        {
            case ApiKeySource.None:
                return (null, null);
            case ApiKeySource.EnvironmentVariable:
                if (string.IsNullOrWhiteSpace(profile.KeyEnvironmentVariable))
                {
                    return (null, "API 키 환경 변수 이름이 비어 있습니다.");
                }

                var value = Environment.GetEnvironmentVariable(profile.KeyEnvironmentVariable)
                            ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(profile.KeyEnvironmentVariable, EnvironmentVariableTarget.User) : null);
                return string.IsNullOrEmpty(value) ? (null, $"환경 변수 {profile.KeyEnvironmentVariable}이(가) 설정되어 있지 않습니다.") : (value, null);
            case ApiKeySource.StoredEncrypted:
                if (!store.IsAvailable)
                {
                    return (null, "암호화 저장소를 사용할 수 없습니다.");
                }

                var stored = store.Get(SecretKeyFor(profile));
                return string.IsNullOrEmpty(stored) ? (null, "저장된 API 키가 없습니다.") : (stored, null);
            default:
                return (null, "알 수 없는 키 방식입니다.");
        }
    }
}
