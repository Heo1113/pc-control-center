using LocalAiPerf.Core.Settings;
using Xunit;

namespace LocalAiPerf.Tests.Core;

public class EndpointValidatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    [InlineData("http://[::1]:9000", "http://[::1]:9000")]
    [InlineData("https://ai.example.com", "https://ai.example.com")]
    [InlineData("  http://127.0.0.1:8080  ", "http://127.0.0.1:8080")]
    public void Valid_roots_are_normalised(string input, string expected)
    {
        Assert.True(EndpointValidator.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://user:pass@127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://127.0.0.1:8080/?x=1")]
    [InlineData("http://10.0.0.5:8080")]
    public void Invalid_or_unsafe_roots_are_rejected(string input)
    {
        Assert.False(EndpointValidator.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
