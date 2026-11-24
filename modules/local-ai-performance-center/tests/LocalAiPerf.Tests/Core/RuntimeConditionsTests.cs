using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using Xunit;

namespace LocalAiPerf.Tests.Core;

public class RuntimeConditionsTests
{
    private static readonly RuntimeSetting[] Temp06 = [new("temperature", "0.6")];
    private static readonly RuntimeSetting[] Temp08 = [new("temperature", "0.8")];

    [Fact]
    public void Unknown_becoming_known_is_not_a_change()
    {
        var before = new RuntimeConditions { ModelId = "m", EngineVersion = null, Settings = [] };
        var after = new RuntimeConditions { ModelId = "m", EngineVersion = "0.1.55", Settings = Temp06 };

        Assert.False(RuntimeConditions.IsMaterialChange(before, after));
        Assert.Empty(RuntimeConditions.Describe(before, after));
        Assert.NotEmpty(RuntimeConditions.Describe(before, after, includeUnknown: true));
        Assert.NotEqual(before.Fingerprint, after.Fingerprint); // still recorded as a new snapshot
    }

    [Theory]
    [InlineData("m2", "0.1.55", 32768)]
    [InlineData("m", "0.1.56", 32768)]
    [InlineData("m", "0.1.55", 65536)]
    public void Known_values_that_differ_are_changes(string model, string engine, long context)
    {
        var before = new RuntimeConditions { ModelId = "m", EngineVersion = "0.1.55", ContextTokens = 32768 };
        var after = new RuntimeConditions { ModelId = model, EngineVersion = engine, ContextTokens = context };

        Assert.True(RuntimeConditions.IsMaterialChange(before, after));
        Assert.Single(RuntimeConditions.Describe(before, after));
    }

    [Fact]
    public void Settings_change_counts_only_when_both_sides_reported_settings()
    {
        var a = new RuntimeConditions { ModelId = "m", Settings = Temp06 };
        var b = new RuntimeConditions { ModelId = "m", Settings = Temp08 };
        var none = new RuntimeConditions { ModelId = "m", Settings = [] };

        Assert.True(RuntimeConditions.IsMaterialChange(a, b));
        Assert.Contains(RuntimeConditions.Describe(a, b), d => d.Contains("temperature"));
        Assert.False(RuntimeConditions.IsMaterialChange(a, none));
    }

    [Theory]
    [InlineData(-25.04, "-25.0%")]
    [InlineData(3.26, "+3.3%")]
    [InlineData(-1e-12, "±0.0%")]
    [InlineData(null, "N/A")]
    public void Delta_text_never_shows_mixed_signs(double? value, string expected) => Assert.Equal(expected, NumberText.Delta(value));
}
