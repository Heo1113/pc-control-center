namespace Iron.App.Presentation;

public enum IronTab
{
    Overview,
    Processes,
    Power,
    Devices,
    Settings,
}

public enum WindowLayout
{
    /// <summary>The whole work area of the window's monitor (4K 175 %: 2194×1186 DIP).</summary>
    Full,

    /// <summary>The left or right half of that work area (4K 175 %: 1097×1186 DIP).</summary>
    Half,
}

/// <summary>Command line of Iron.App.exe.</summary>
public sealed record AppOptions
{
    public IronTab Tab { get; init; } = IronTab.Overview;
    public WindowLayout Layout { get; init; } = WindowLayout.Full;
    public MockPreset Preset { get; init; } = MockPreset.Normal;
    public bool UsbReleased { get; init; }
    public bool StartInTray { get; init; }
    public bool NoJitter { get; init; }

    /// <summary>Renders every screen offscreen at the given scale into this folder with a layout report, then exits.</summary>
    public string? RenderScreensTo { get; init; }

    /// <summary>Window size (DIP) list for --render-screens; default: the 4K 175 % full and half work areas of the design.</summary>
    public IReadOnlyList<(double Width, double Height)> RenderSizes { get; init; } = [(2194, 1186), (1097, 1186)];

    public double RenderScale { get; init; } = 1.75;

    /// <summary>Opens the real window, walks the tabs and states, writes a report into this folder, then exits.</summary>
    public string? SmokeTestTo { get; init; }

    /// <summary>Stage 2: "real" reads NVML, Windows counters and HWiNFO REST instead of the mock values (default "mock").</summary>
    public bool RealData { get; init; }

    /// <summary>HWiNFO REST address for the real source (loopback only); "off" = not used.</summary>
    public string HwInfoUrl { get; init; } = "http://127.0.0.1:8764/";

    /// <summary>Starts the real readers without a window, writes what they read (redacted) into this folder, then exits.</summary>
    public string? ProbeRealTo { get; init; }

    public int ProbeSeconds { get; init; } = 12;

    /// <summary>Long runs (12 h on the user's PC): every minute one line with IRON's own memory, handles, GDI/USER and source states.</summary>
    public string? SoakLog { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public const string Usage = """
        Iron.App.exe [옵션]
          --tab overview|processes|power|devices|settings   시작 화면
          --layout full|half                                 작업 영역 전체 / 절반
          --mock-state normal|alert|disconnected|loading      시작 모의 상태
          --usb-released                                     WireView USB 해제 상태로 시작 (모의)
          --tray                                             트레이에서 시작
          --no-jitter                                        모의 값 흔들림 없음 (설계 값 그대로)
          --render-screens DIR [--render-size 2194x1186,1097x1186] [--render-scale 1.75]
          --smoke-test DIR
          --source mock|real                                 Stage 2: real = NVML·Windows 카운터·HWiNFO REST (기본 mock)
          --hwinfo-url URL|off                               HWiNFO REST 주소 (루프백만, 기본 http://127.0.0.1:8764/)
          --probe-real DIR [--probe-seconds 12]               창 없이 실제 읽기 결과(민감정보 제거)를 DIR에 기록하고 종료
          --soak-log FILE                                    1분마다 IRON 자신의 메모리·핸들·GDI·소스 상태를 FILE(CSV)에 추가
        """;

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var options = new AppOptions();
        var errors = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next()
            {
                if (i + 1 < args.Count)
                {
                    return args[++i];
                }

                errors.Add($"{arg} 다음에 값이 필요합니다.");
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--tab":
                    options = Next() is { } tab && TryTab(tab, out var t) ? options with { Tab = t } : Fail(options, errors, arg);
                    break;
                case "--layout":
                    options = Next()?.ToLowerInvariant() switch
                    {
                        "full" => options with { Layout = WindowLayout.Full },
                        "half" => options with { Layout = WindowLayout.Half },
                        _ => Fail(options, errors, arg),
                    };
                    break;
                case "--mock-state":
                    options = Next()?.ToLowerInvariant() switch
                    {
                        "normal" => options with { Preset = MockPreset.Normal },
                        "alert" or "error" => options with { Preset = MockPreset.Alert },
                        "disconnected" => options with { Preset = MockPreset.Disconnected },
                        "loading" => options with { Preset = MockPreset.Loading },
                        _ => Fail(options, errors, arg),
                    };
                    break;
                case "--usb-released":
                    options = options with { UsbReleased = true };
                    break;
                case "--tray":
                case "--minimized":
                    options = options with { StartInTray = true };
                    break;
                case "--no-jitter":
                    options = options with { NoJitter = true };
                    break;
                case "--render-screens":
                    options = Next() is { } dir ? options with { RenderScreensTo = dir, NoJitter = true } : options;
                    break;
                case "--render-size":
                    options = Next() is { } sizes && TrySizes(sizes, out var parsed) ? options with { RenderSizes = parsed } : Fail(options, errors, arg);
                    break;
                case "--render-scale":
                    options = Next() is { } scale && double.TryParse(scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)
                              && s is >= 1 and <= 4
                        ? options with { RenderScale = s }
                        : Fail(options, errors, arg);
                    break;
                case "--smoke-test":
                    options = Next() is { } smoke ? options with { SmokeTestTo = smoke } : options;
                    break;
                case "--source":
                    options = Next()?.ToLowerInvariant() switch
                    {
                        "real" => options with { RealData = true },
                        "mock" => options with { RealData = false },
                        _ => Fail(options, errors, arg),
                    };
                    break;
                case "--hwinfo-url":
                    options = Next() is { } url && (url.Equals("off", StringComparison.OrdinalIgnoreCase)
                                                    || (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback && u.Port != 8080))
                        ? options with { HwInfoUrl = url }
                        : Fail(options, errors, arg);
                    break;
                case "--probe-real":
                    options = Next() is { } probe ? options with { ProbeRealTo = probe, RealData = true } : options;
                    break;
                case "--soak-log":
                    options = Next() is { } soak ? options with { SoakLog = soak } : options;
                    break;
                case "--probe-seconds":
                    options = Next() is { } secs && int.TryParse(secs, out var n) && n is >= 3 and <= 600 ? options with { ProbeSeconds = n } : Fail(options, errors, arg);
                    break;
                default:
                    errors.Add($"알 수 없는 옵션: {arg}");
                    break;
            }
        }

        return options with { Errors = errors };
    }

    public static bool TryTab(string text, out IronTab tab)
    {
        tab = text.ToLowerInvariant() switch
        {
            "overview" => IronTab.Overview,
            "processes" => IronTab.Processes,
            "power" => IronTab.Power,
            "devices" => IronTab.Devices,
            "settings" => IronTab.Settings,
            _ => (IronTab)(-1),
        };
        return Enum.IsDefined(tab);
    }

    private static bool TrySizes(string text, out IReadOnlyList<(double, double)> sizes)
    {
        var list = new List<(double, double)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var wh = part.Split('x', 'X');
            if (wh.Length != 2 || !double.TryParse(wh[0], System.Globalization.CultureInfo.InvariantCulture, out var w)
                || !double.TryParse(wh[1], System.Globalization.CultureInfo.InvariantCulture, out var h) || w is < 320 or > 10000 || h is < 240 or > 10000)
            {
                sizes = [];
                return false;
            }

            list.Add((w, h));
        }

        sizes = list;
        return list.Count > 0;
    }

    private static AppOptions Fail(AppOptions options, List<string> errors, string arg)
    {
        errors.Add($"{arg} 값이 올바르지 않습니다.");
        return options;
    }
}

/// <summary>Delayed actions on the UI thread (a dispatcher timer in the app, a manual clock in tests).</summary>
public interface IUiScheduler
{
    DateTimeOffset Now { get; }

    IDisposable Schedule(TimeSpan delay, Action action);
}
