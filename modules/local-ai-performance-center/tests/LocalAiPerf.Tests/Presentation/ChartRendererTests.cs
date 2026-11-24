using LocalAiPerf.Analytics;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Storage;
using ScottPlot;
using Xunit;
using static LocalAiPerf.Tests.FrameFactory;

namespace LocalAiPerf.Tests.Presentation;

public class ChartRendererTests
{
    private static List<TelemetryFrame> LiveFrames()
    {
        var frames = new List<TelemetryFrame>();
        for (var i = 0; i < 300; i++)
        {
            if (i is >= 120 and < 140)
            {
                continue; // a collection gap must stay visible
            }

            var load = 60 + 35 * Math.Sin(i / 15.0);
            frames.Add(Frame(T0.AddSeconds(i), gpuUtil: load, tokS: i is >= 200 and < 240 ? 22 : 40 + 3 * Math.Sin(i / 9.0), power: 120 + 2.2 * load, seq: i,
                temp: 60 + load / 5, throttle: i is >= 200 and < 240 ? (ulong)GpuThrottleReasons.SwThermalSlowdown : 0, energyMj: i * 250_000L));
        }

        return frames;
    }

    private static string OutputDirectory(TempDir dir)
    {
        var preview = Environment.GetEnvironmentVariable("LAPC_CHART_PREVIEW_DIR");
        var path = string.IsNullOrEmpty(preview) ? dir.Path : preview;
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Every_chart_renders_repeatedly_with_one_bottom_axis_and_writes_a_png()
    {
        using var dir = new TempDir();
        var output = OutputDirectory(dir);
        var frames = LiveFrames();
        var interval = TimeSpan.FromSeconds(1);

        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var a = store.StartSession(Start(T0, "업데이트 전"));
        foreach (var f in frames)
        {
            store.WriteFrame(new FrameWrite { SessionId = a.Id, Frame = f });
        }

        store.EndSession(a.Id, T0.AddSeconds(300), SessionEndReasons.UserStopped);
        var b = store.StartSession(Start(T0.AddHours(1), "업데이트 후"));
        foreach (var f in frames)
        {
            store.WriteFrame(new FrameWrite { SessionId = b.Id, Frame = f with { Timestamp = f.Timestamp.AddHours(1), Hardware = f.Hardware with { Timestamp = f.Hardware.Timestamp.AddHours(1) }, Runtime = f.Runtime! with { Timestamp = f.Runtime.Timestamp.AddHours(1), Rates = f.Runtime.Rates! with { AnswerTokPerSec = f.Runtime.Rates.AnswerTokPerSec * 0.8 } } } });
        }

        var dataA = store.LoadSessionData(a.Id)!;
        var dataB = store.LoadSessionData(b.Id)!;
        var reportA = SessionAnalyzer.Analyze(dataA, DetectionOptions.Default);
        var tok = MetricCatalog.Get(MetricKeys.RtAnswerTokS);
        var seriesA = SeriesExtractor.Extract(dataA);
        var seriesB = SeriesExtractor.Extract(dataB);

        var models = new Dictionary<string, ChartModel>
        {
            ["live-utilization"] = ChartBuilder.Utilization(frames, 0, interval),
            ["live-power"] = ChartBuilder.GpuPower(frames, 0, interval),
            ["live-temperature"] = ChartBuilder.GpuTemperature(frames, 0, interval, 83),
            ["live-vram"] = ChartBuilder.Vram(frames, 0, interval),
            ["live-clock"] = ChartBuilder.GpuClock(frames, 0, interval),
            ["live-pcie"] = ChartBuilder.Pcie(frames, 0, interval),
            ["live-cpu"] = ChartBuilder.Cpu(frames, interval),
            ["live-ram"] = ChartBuilder.Ram(frames, interval),
            ["live-throughput"] = ChartBuilder.Throughput(frames, interval),
            ["history-tok"] = ChartBuilder.SessionMetric(tok, seriesA[MetricKeys.RtAnswerTokS], 1000, reportA.Degradations, reportA.Throttles),
            ["compare-overlay"] = ChartBuilder.Overlay(tok,
            [
                ("#1 업데이트 전", ChartPalette.Slot(0), dataA.Session.StartedAt.ToUnixTimeMilliseconds(), seriesA[MetricKeys.RtAnswerTokS], 1000),
                ("#2 업데이트 후", ChartPalette.Slot(1), dataB.Session.StartedAt.ToUnixTimeMilliseconds(), seriesB[MetricKeys.RtAnswerTokS], 1000),
            ]),
        };

        Assert.NotEmpty(reportA.Degradations);
        Assert.Contains(models["history-tok"].Spans, s => s.Label == "성능 저하 후보");
        Assert.Equal(2, ChartRenderer.Segments(models["live-cpu"].Series[0], models["live-cpu"].GapThreshold).Count());
        foreach (var (name, model) in models)
        {
            var plot = new Plot();
            for (var i = 0; i < 3; i++)
            {
                Assert.True(ChartRenderer.Render(plot, model), name);
            }

            Assert.Single(plot.Axes.GetAxes(Edge.Bottom));
            Assert.DoesNotContain(plot.Axes.GetPanels(), p => p is ScottPlot.Panels.LegendPanel); // legend is drawn by the host
            Assert.Equal(model.Series.Count(x => x.X.Length > 0) >= 2 || model.Spans.Count > 0, ChartRenderer.LegendEntries(model).Count > 0);
            var file = Path.Combine(output, name + ".png");
            plot.SavePng(file, 900, 320);
            Assert.True(new FileInfo(file).Length > 2000, name);
        }

        // 200 % display scale (as on a 4K monitor): the app sets ScaleFactor; everything must still render.
        var scaled = new Plot { ScaleFactor = 2 };
        Assert.True(ChartRenderer.Render(scaled, models["live-utilization"]));
        var scaledFile = Path.Combine(output, "live-utilization-200pct.png");
        scaled.SavePng(scaledFile, 1800, 640);
        Assert.True(new FileInfo(scaledFile).Length > 2000);
    }

    [Fact]
    public void Empty_model_draws_nothing_and_asks_for_the_empty_text()
    {
        var plot = new Plot();
        Assert.False(ChartRenderer.Render(plot, null));
        Assert.False(ChartRenderer.Render(plot, new ChartModel { Title = "t", Unit = "%", Series = [new ChartSeries("x", [], [], "#3987e5")] }));
    }

    [Fact]
    public void Chart_font_is_a_name_the_chart_library_draws_hangul_with()
    {
        if (!SkiaSharp.SKFontManager.Default.FontFamilies.Any(f =>
            {
                using var typeface = SkiaSharp.SKTypeface.FromFamilyName(f);
                return typeface.ContainsGlyph('가');
            }))
        {
            Assert.Skip("이 머신에는 한글 글꼴이 없습니다.");
        }

        if (Environment.GetEnvironmentVariable("LAPC_FONT_REPORT") is { Length: > 0 } report)
        {
            // CI evidence: what the font system reports under the current regional format.
            var families = SkiaSharp.SKFontManager.Default.FontFamilies;
            using var malgun = SkiaSharp.SKFontManager.Default.MatchFamily("Malgun Gothic");
            File.WriteAllText(report, string.Join(Environment.NewLine,
                $"culture={System.Globalization.CultureInfo.CurrentCulture.Name}",
                $"chosen={ChartRenderer.Font}",
                $"drawn_with={(ChartRenderer.Font is null ? "-" : ScottPlot.Fonts.GetTypeface(ChartRenderer.Font, false, false).FamilyName)}",
                $"malgun_reported_as={malgun?.FamilyName ?? "-"}",
                $"families_contain_Malgun_Gothic={families.Contains("Malgun Gothic", StringComparer.OrdinalIgnoreCase)}",
                $"families_contain_맑은_고딕={families.Contains("맑은 고딕", StringComparer.OrdinalIgnoreCase)}"),
                new System.Text.UTF8Encoding(false));
        }

        Assert.NotNull(ChartRenderer.Font);
        var drawn = ScottPlot.Fonts.GetTypeface(ChartRenderer.Font, bold: false, italic: false);
        Assert.True(drawn.ContainsGlyph('가'), $"ScottPlot draws '{ChartRenderer.Font}' with '{drawn.FamilyName}', which has no Hangul");
    }

    [Fact]
    public void Font_choice_uses_the_localized_name_when_the_english_name_is_not_drawable()
    {
        // Korean Windows: "Malgun Gothic" is not in the installed-name list ScottPlot checks, "맑은 고딕" is.
        // Simulated here with a name pair: only the reported (localized) name gives a Hangul typeface.
        using var hangul = new[] { "Malgun Gothic", "NanumGothic", "Noto Sans CJK KR" }
            .Select(n => SkiaSharp.SKTypeface.FromFamilyName(n)).FirstOrDefault(tf => tf.ContainsGlyph('가'));
        if (hangul is null)
        {
            Assert.Skip("이 머신에는 시험용 한글 글꼴이 없습니다.");
        }

        using var plain = SkiaSharp.SKTypeface.FromFamilyName("DejaVu Sans");
        var chosen = ChartRenderer.ResolveHangulFont(
            ["Malgun Gothic"],
            name => name == "Malgun Gothic" ? "맑은 고딕" : null,
            ["Segoe UI"],
            name => name == "맑은 고딕" ? hangul : plain);

        Assert.Equal("맑은 고딕", chosen);
        Assert.Null(ChartRenderer.ResolveHangulFont(["Malgun Gothic"], _ => null, ["Segoe UI"], _ => plain));
    }
}
