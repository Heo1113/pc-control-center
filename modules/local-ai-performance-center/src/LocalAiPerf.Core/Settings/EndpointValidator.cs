using System.Net;

namespace LocalAiPerf.Core.Settings;

/// <summary>
/// Validates a runtime server root URL. Follows the rules of Strata's own monitor client:
/// http(s) only, no credentials, no path/query/fragment, and plain http only for loopback addresses.
/// </summary>
public static class EndpointValidator
{
    public static bool TryNormalize(string? raw, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "주소가 비어 있습니다.";
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            error = "URL 형식이 아닙니다.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "http 또는 https 주소만 사용할 수 있습니다.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "주소에 사용자 이름이나 비밀번호를 넣을 수 없습니다.";
            return false;
        }

        if (uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "서버 루트 주소만 입력하세요 (경로, 쿼리 없이). 예: http://127.0.0.1:8080";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            error = "원격 서버는 https만 허용합니다 (평문 http는 이 PC의 127.0.0.1/localhost만).";
            return false;
        }

        normalized = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }

    public static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
