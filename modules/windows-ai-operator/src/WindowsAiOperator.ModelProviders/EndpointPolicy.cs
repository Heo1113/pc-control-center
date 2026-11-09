using System.Net;
using System.Net.Sockets;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.ModelProviders;

public enum EndpointScope
{
    /// <summary>This PC (127.0.0.1, ::1, localhost).</summary>
    Loopback,

    /// <summary>Private network address (another PC on the LAN).</summary>
    LocalNetwork,

    /// <summary>Anything else: an external/cloud server.</summary>
    Internet,
}

public sealed record EndpointCheck(Uri? BaseUri, EndpointScope Scope, string? Problem)
{
    public bool IsRemote => Scope != EndpointScope.Loopback;
}

/// <summary>
/// URL rules for model servers (same spirit as the user's project A: no credentials in URLs, no redirects).
/// A server outside this PC is used only when the profile explicitly allows it; plain http is accepted only on this PC
/// or a private network.
/// </summary>
public static class EndpointPolicy
{
    public static EndpointCheck Check(ModelProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseUrl) || !Uri.TryCreate(profile.BaseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new EndpointCheck(null, EndpointScope.Internet, "서버 주소가 올바른 URL이 아닙니다.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return new EndpointCheck(null, EndpointScope.Internet, "http 또는 https 주소만 사용할 수 있습니다.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new EndpointCheck(null, EndpointScope.Internet, "주소에 사용자 이름·비밀번호를 넣지 마세요. API 키는 키 설정에서 지정합니다.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return new EndpointCheck(null, EndpointScope.Internet, "주소에 ?나 #을 넣지 마세요(기본 주소만 입력).");
        }

        var scope = Classify(uri.Host);
        if (scope != EndpointScope.Loopback && !profile.AllowRemote)
        {
            return new EndpointCheck(uri, scope, scope == EndpointScope.LocalNetwork
                ? "이 PC 밖(같은 네트워크)의 서버입니다. 화면 텍스트가 전송되므로 프로필에서 '외부 서버 사용'을 직접 허용해야 합니다."
                : "외부(클라우드) 서버입니다. 화면 텍스트가 외부로 전송되므로 프로필에서 '외부 서버 사용'을 직접 허용해야 합니다.");
        }

        if (scope == EndpointScope.Internet && uri.Scheme != "https")
        {
            return new EndpointCheck(uri, scope, "외부 서버는 https만 허용합니다.");
        }

        var normalized = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        return new EndpointCheck(normalized, scope, null);
    }

    public static EndpointScope Classify(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointScope.Loopback;
        }

        var trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var address))
        {
            if (IPAddress.IsLoopback(address))
            {
                return EndpointScope.Loopback;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
                return isPrivate ? EndpointScope.LocalNetwork : EndpointScope.Internet;
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal ? EndpointScope.LocalNetwork : EndpointScope.Internet;
        }

        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || !host.Contains('.')
            ? EndpointScope.LocalNetwork
            : EndpointScope.Internet;
    }

    /// <summary>The server root without a trailing /v1 (where Strata and llama.cpp expose /health, /props, /v1/status).</summary>
    public static Uri Root(Uri baseUri)
    {
        var text = baseUri.ToString().TrimEnd('/');
        if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^3];
        }

        return new Uri(text.TrimEnd('/') + "/");
    }
}
