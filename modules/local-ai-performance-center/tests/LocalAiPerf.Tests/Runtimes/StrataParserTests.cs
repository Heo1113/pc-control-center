using LocalAiPerf.Runtimes.Strata;
using Xunit;

namespace LocalAiPerf.Tests.Runtimes;

public class StrataParserTests
{
    [Fact]
    public void V1_telemetry_is_parsed_with_units_and_nulls_preserved()
    {
        using var doc = Fixtures.Json("strata", "telemetry_v1.json");
        var t = StrataParser.ParseV1(doc.RootElement);

        Assert.Equal("server-v1", t.Source);
        Assert.Equal("4f1c2a9b8e7d6c5b4a3928170615aabb", t.InstanceId);
        Assert.Equal("ready", t.InstanceState);
        Assert.Equal("Qwen3.8-Flash-Next", t.ModelId);
        Assert.True(t.ModelLoaded);
        Assert.Equal(65536, t.Context);
        Assert.Equal(2, t.Work.SlotsEffective);
        Assert.Equal(1, t.Work.Active);
        Assert.Equal(3, t.Work.AdmissionRejected);
        Assert.Equal(1, t.Work.QueueTimeouts);
        Assert.Equal(38.5, t.Rates.AnswerTokPerSec);
        Assert.Equal(1450.2, t.Rates.PrefillTokPerSec);
        Assert.Equal(987654, t.Rates.OutputTokensCounter);
        Assert.Null(t.Rates.AggregateOutputTokPerSec); // derived later by the adapter, never by the parser
        Assert.Equal(2, t.Slots.Count);
        Assert.Equal(12000, t.Slots[0].HeldTokens);
        Assert.Null(t.Slots[1].TokPerSec);
    }

    [Fact]
    public void Recent_requests_skip_rows_without_a_string_id_and_derive_rates()
    {
        using var doc = Fixtures.Json("strata", "telemetry_v1.json");
        var t = StrataParser.ParseV1(doc.RootElement);

        Assert.Equal(2, t.Recent.Count);
        var ok = t.Recent[0];
        Assert.Equal("0f0e0d0c0b0a", ok.Id);
        Assert.Equal(640 / 16.4, ok.DecodeTokPerSec!.Value, 3);
        Assert.Equal(1000 / 0.69, ok.PrefillTokPerSec!.Value, 3); // 9000 prompt - 8000 reused
        var failed = t.Recent[1];
        Assert.True(failed.IsError);
        Assert.Null(failed.DecodeTokPerSec); // no decode time -> no rate, not zero
    }

    [Fact]
    public void Activity_events_are_validated_like_the_strata_client()
    {
        using var doc = Fixtures.Json("strata", "telemetry_v1.json");
        var t = StrataParser.ParseV1(doc.RootElement);

        // seq 119 duplicate and the non-hex request id are dropped.
        Assert.Equal([118L, 119L], t.Activity.Select(a => a.Sequence));
    }

    [Fact]
    public void Legacy_metrics_and_status_are_normalised()
    {
        using var metrics = Fixtures.Json("strata", "metrics_legacy.json");
        using var status = Fixtures.Json("strata", "status_v1.json");
        var t = StrataParser.ParseLegacy(metrics.RootElement, status.RootElement);

        Assert.Equal("legacy-adapter", t.Source);
        Assert.Equal("0.1.40", t.EngineVersion);
        Assert.Equal("Qwen3.6-35B-A3B", t.ModelId);
        Assert.Equal(32768, t.Context);
        Assert.Equal(1, t.Work.Active); // state "generating" without "running"
        Assert.Equal(2, t.Work.Queued);
        Assert.Equal(55.2, t.Rates.AnswerTokPerSec);
        Assert.Null(t.Rates.OutputTokensCounter);
        Assert.Single(t.Recent);
        Assert.Equal(7, t.Totals!.Requests);
    }

    [Fact]
    public void Props_keep_only_known_scalar_settings_and_the_model_file_name()
    {
        using var doc = Fixtures.Json("strata", "props.json");
        var p = StrataParser.ParseProps(doc.RootElement);

        Assert.Equal("Qwen3.8-Flash-Next-IQ3_S.gguf", p.ModelFileName);
        Assert.DoesNotContain("SomeUser", p.ModelFileName);
        Assert.Equal("0.1.55", p.BuildVersion);
        Assert.Equal(2, p.TotalSlots);
        Assert.Equal(65536, p.Context);
        var keys = p.Settings.Select(s => s.Key).ToList();
        Assert.Contains("temperature", keys);
        Assert.Contains("repeat_penalty", keys);
        Assert.DoesNotContain("unknown_new_param", keys);
        Assert.DoesNotContain("seed", keys);
        Assert.Equal("0.6", p.Settings.Single(s => s.Key == "temperature").Value);
    }

    [Fact]
    public void Wrong_types_become_null_instead_of_throwing()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"schema_version": 1, "model": {"id": 7, "loaded": "yes", "context": 1.5},
             "work": {"active": true, "slots": "nope"}, "rates": {"answer_tok_s": "fast"}, "recent": {}, "activity": []}
            """);
        var t = StrataParser.ParseV1(doc.RootElement);

        Assert.Null(t.ModelId);
        Assert.Null(t.ModelLoaded);
        Assert.Null(t.Context);
        Assert.Null(t.Work.Active);
        Assert.Empty(t.Slots);
        Assert.Null(t.Rates.AnswerTokPerSec);
        Assert.Empty(t.Recent);
        Assert.Empty(t.Activity);
    }

    [Fact]
    public void Control_characters_and_long_strings_are_cleaned()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"model\": {\"id\": \"evil\\u001b[31mname" + new string('x', 500) + "\"}}");
        var t = StrataParser.ParseV1(doc.RootElement);

        Assert.NotNull(t.ModelId);
        Assert.DoesNotContain('\u001b', t.ModelId);
        Assert.True(t.ModelId!.Length <= 120);
    }
}
