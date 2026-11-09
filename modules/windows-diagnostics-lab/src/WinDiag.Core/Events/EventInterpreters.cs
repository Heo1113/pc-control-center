using System.Globalization;
using System.Text.RegularExpressions;
using WinDiag.Core.Crash;
using WinDiag.Core.Time;

namespace WinDiag.Core.Events;

/// <summary>
/// Reads the documented data fields of specific events into facts. Interpreters only restate what the event contains;
/// they never add a cause. Unknown or missing fields are simply not shown.
/// </summary>
public static partial class EventInterpreters
{
    public static Interpretation KernelPower41(IEventFields e)
    {
        var facts = new List<EventFact>();
        var codeText = e.Get("BugcheckCode");
        BugCheckCatalog.TryParseCode(codeText, decimalByDefault: true, out var code);
        if (codeText is not null)
        {
            facts.Add(code != 0
                ? new EventFact("BugcheckCode", BugCheckCatalog.Describe(code), "0이 아니면 이 재시작 전에 BugCheck(블루스크린)가 있었다는 뜻입니다.")
                : new EventFact("BugcheckCode", "0", "BugCheck 정보가 기록되지 않았습니다 (전원 차단, 완전 정지 후 강제 재시작 등에서 0일 수 있음)."));
        }

        if (code != 0)
        {
            for (var i = 1; i <= 4; i++)
            {
                if (e.Get($"BugcheckParameter{i}") is { } p)
                {
                    facts.Add(new EventFact($"BugcheckParameter{i}", p));
                }
            }
        }

        var powerButton = e.Get("PowerButtonTimestamp");
        if (powerButton is not null)
        {
            var pressed = IsNonZero(powerButton);
            facts.Add(new EventFact("PowerButtonTimestamp", powerButton,
                pressed ? "0이 아니면 전원 버튼 조작이 기록된 것입니다 (Microsoft 문서 기준)." : null));
        }

        AddIfPresent(facts, e, "LongPowerButtonPressDetected", "전원 버튼 길게 누름 감지");
        AddIfPresent(facts, e, "SleepInProgress", "절전 전환 진행 중");
        AddIfPresent(facts, e, "BootAppStatus");
        var signature = code != 0
            ? "bugcheck=" + BugCheckCatalog.Hex(code)
            : IsNonZero(powerButton) ? "powerbutton" : "nobugcheck";
        return new Interpretation
        {
            TitleDetail = code != 0 ? BugCheckCatalog.Describe(code) : "BugCheck 정보 없음",
            Facts = facts,
            Signature = signature,
        };
    }

    /// <summary>Microsoft-Windows-WER-SystemErrorReporting 1001 (source "BugCheck").</summary>
    public static Interpretation BugCheck1001(IEventFields e)
    {
        var facts = new List<EventFact>();
        var text = e.Field("param1|0");
        var detail = "BugCheck";
        string? signature = null;
        if (TryParseBugCheckText(text, out var code, out var parameters))
        {
            detail = BugCheckCatalog.Describe(code);
            signature = "bugcheck=" + BugCheckCatalog.Hex(code);
            facts.Add(new EventFact("BugCheck 코드", detail, BugCheckCatalog.Find(code)?.Description));
            for (var i = 0; i < parameters.Count; i++)
            {
                facts.Add(new EventFact($"매개변수 {i + 1}", parameters[i]));
            }
        }
        else if (text is not null)
        {
            facts.Add(new EventFact("BugCheck 기록", text));
        }

        if (e.Field("param2|1") is { Length: > 0 } dump)
        {
            facts.Add(new EventFact("덤프 저장 위치(기록)", dump, "이 경로에 덤프가 저장되었다고 Windows가 기록했습니다. 현재 파일이 남아 있는지는 별도로 확인합니다."));
        }

        if (e.Field("param3|2") is { Length: > 0 } report)
        {
            facts.Add(new EventFact("보고서 ID", report));
        }

        return new Interpretation { TitleDetail = detail, Facts = facts, Signature = signature };
    }

    /// <summary>"0x0000003b (0x00000000c0000005, 0xfffff80123456789, 0x0, 0x0)".</summary>
    public static bool TryParseBugCheckText(string? text, out uint code, out IReadOnlyList<string> parameters)
    {
        code = 0;
        parameters = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = BugCheckText().Match(text);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
        {
            return false;
        }

        parameters = match.Groups[2].Success
            ? match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
        return true;
    }

    public static Interpretation EventLog6008(IEventFields e)
    {
        var time = e.GetAt(0);
        var date = e.GetAt(1);
        var facts = new List<EventFact>();
        if (time is not null || date is not null)
        {
            facts.Add(new EventFact("기록된 이전 종료 시각", $"{date} {time}".Trim(),
                "Windows가 기록한 이전 종료 시각(현지 형식)입니다. 실제 정지 순간과 다를 수 있습니다."));
        }

        return new Interpretation { Facts = facts, Signature = "unexpected" };
    }

    /// <summary>Microsoft-Windows-Kernel-General 1: the system time changed.</summary>
    public static Interpretation KernelGeneral1(IEventFields e)
    {
        var facts = new List<EventFact>();
        var newText = e.Get("NewTime");
        var oldText = e.Get("OldTime");
        if (oldText is not null)
        {
            facts.Add(new EventFact("이전 시각", oldText));
        }

        if (newText is not null)
        {
            facts.Add(new EventFact("새 시각", newText));
        }

        AddIfPresent(facts, e, "Reason", "변경 사유 코드");
        if (IsoTime.TryParse(newText, out var newTime) && IsoTime.TryParse(oldText, out var oldTime))
        {
            var delta = newTime - oldTime;
            var large = delta.Duration() >= TimeSpan.FromMinutes(2);
            facts.Add(new EventFact("변경량", (delta < TimeSpan.Zero ? "-" : "+") + TimeDisplay.Duration(delta),
                large ? "큰 시각 변경: 이 시점 전후 이벤트의 순서와 간격 비교에 주의하세요." : null));
            return new Interpretation
            {
                TitleDetail = (delta < TimeSpan.Zero ? "-" : "+") + TimeDisplay.Duration(delta),
                Facts = facts,
                Severity = large ? DiagSeverity.Warning : DiagSeverity.Info,
                Signature = large ? "large" : "small",
            };
        }

        return new Interpretation { Facts = facts, Signature = "unknown" };
    }

    public static Interpretation KernelGeneralStartStop(IEventFields e)
    {
        var facts = new List<EventFact>();
        AddIfPresent(facts, e, "StartTime", "시작 시각");
        AddIfPresent(facts, e, "StopTime", "종료 시각");
        return new Interpretation { Facts = facts, Signature = e.EventId.ToString(CultureInfo.InvariantCulture) };
    }

    public static Interpretation ApplicationError1000(IEventFields e)
    {
        var app = e.Field("AppName|0");
        var appVersion = e.Field("AppVersion|1");
        var module = e.Field("ModuleName|3");
        var moduleVersion = e.Field("ModuleVersion|4");
        var exception = e.Field("ExceptionCode|6");
        var offset = e.Field("FaultingOffset|7");
        var facts = new List<EventFact>();
        Add(facts, "프로그램", app);
        Add(facts, "프로그램 버전", appVersion);
        Add(facts, "오류 모듈", module, "오류가 드러난 모듈입니다. 원인 모듈이라는 뜻은 아닙니다.");
        Add(facts, "모듈 버전", moduleVersion);
        string? exceptionText = null;
        if (ExceptionCodes.TryParse(exception, out var code))
        {
            exceptionText = "0x" + code.ToString("x8", CultureInfo.InvariantCulture);
            facts.Add(new EventFact("예외 코드", ExceptionCodes.Describe(code)));
        }

        Add(facts, "오류 오프셋", offset);
        Add(facts, "프로그램 경로", e.Field("AppPath|10"));
        Add(facts, "모듈 경로", e.Field("ModulePath|11"));
        return new Interpretation
        {
            TitleDetail = Join(" — ", app, module is null ? null : $"{module}{(exceptionText is null ? "" : $" ({exceptionText})")}"),
            Facts = facts,
            Signature = Sig(("app", app), ("module", module), ("code", exceptionText)),
        };
    }

    public static Interpretation ApplicationHang1002(IEventFields e)
    {
        var app = e.Field("AppName|0");
        var facts = new List<EventFact>();
        Add(facts, "프로그램", app);
        Add(facts, "프로그램 버전", e.Field("AppVersion|1"));
        Add(facts, "프로그램 경로", e.Field("AppPath|5"));
        return new Interpretation { TitleDetail = app, Facts = facts, Signature = Sig(("app", app)) };
    }

    public static Interpretation DotNet1026(IEventFields e)
    {
        var text = e.GetAt(0) ?? e.Message ?? "";
        var app = LineValue(text, "Application:");
        var exception = LineValue(text, "Exception Info:");
        var facts = new List<EventFact>();
        Add(facts, "프로그램", app);
        Add(facts, "예외", exception);
        Add(facts, "Framework", LineValue(text, "Framework Version:"));
        return new Interpretation
        {
            TitleDetail = Join(": ", app, exception),
            Facts = facts,
            Signature = Sig(("app", app), ("exception", exception)),
        };
    }

    /// <summary>"Windows Error Reporting" 1001 in the Application log.</summary>
    public static Interpretation WindowsErrorReporting1001(IEventFields e)
    {
        var eventName = e.Field("EventName|2");
        var p1 = e.Field("P1|5");
        var facts = new List<EventFact>();
        Add(facts, "이벤트 이름", eventName);
        Add(facts, "버킷", e.Field("Bucket|0"));
        for (var i = 1; i <= 10; i++)
        {
            Add(facts, $"P{i}", e.Field($"P{i}|{4 + i}"));
        }

        if (string.Equals(eventName, "LiveKernelEvent", StringComparison.OrdinalIgnoreCase)
            && BugCheckCatalog.TryParseCode(p1, decimalByDefault: false, out var live) && live is 0x117 or 0x141)
        {
            return new Interpretation
            {
                TitleDetail = $"LiveKernelEvent {BugCheckCatalog.Describe(live)}",
                Facts = facts,
                Category = EventCategories.GpuDriver,
                Severity = DiagSeverity.Warning,
                Signature = Sig(("event", eventName), ("code", BugCheckCatalog.Hex(live))),
                Meaning = "Windows가 GPU 관련 라이브 커널 이벤트(시스템을 멈추지 않고 만든 진단 보고서)를 기록했습니다. 코드는 디스플레이 드라이버 시간 초과 유형을 나타냅니다.",
                Caution = "라이브 커널 이벤트는 시스템 충돌이 아닙니다. 원인은 같은 시각의 GPU 드라이버 이벤트와 함께 확인해야 합니다.",
            };
        }

        var module = string.Equals(eventName, "APPCRASH", StringComparison.OrdinalIgnoreCase) ? e.Field("P4|8") : null;
        return new Interpretation
        {
            TitleDetail = Join(" ", eventName, p1),
            Facts = facts,
            Signature = Sig(("event", eventName), ("p1", p1), ("module", module)),
        };
    }

    public static Interpretation Whea(IEventFields e)
    {
        var facts = new List<EventFact>();
        var errorSource = e.Get("ErrorSource");
        var component = WheaComponent(e, errorSource);
        var corrected = e.EventId switch
        {
            17 or 19 or 47 => "수정됨(corrected)",
            18 => "치명적(fatal)",
            _ => ContainsAny(e.Message, "corrected", "수정된") ? "수정됨(corrected)"
                : ContainsAny(e.Message, "fatal", "치명적") ? "치명적(fatal)" : null,
        };
        Add(facts, "구성 요소(판정)", ComponentName(component), "이벤트 데이터 필드·메시지·이벤트 ID에서 판정한 구성 요소입니다.");
        Add(facts, "오류 종류", corrected);
        if (errorSource is not null)
        {
            facts.Add(new EventFact("ErrorSource", WheaErrorSourceName(errorSource)));
        }

        foreach (var field in new[] { "ErrorType", "ApicId", "MCABank", "MciStat", "MciAddr", "TransactionType", "MemHierarchyLvl",
                     "PrimaryDeviceName", "SecondaryDeviceName", "VendorID", "DeviceID", "PhysicalAddress", "Node", "Module", "Bank" })
        {
            AddIfPresent(facts, e, field);
        }

        var category = component switch
        {
            "pcie" => EventCategories.HardwarePcie,
            "memory" => EventCategories.HardwareMemory,
            "cpu" => EventCategories.HardwareCpu,
            _ => EventCategories.HardwareOther,
        };
        return new Interpretation
        {
            TitleDetail = Join(", ", ComponentName(component), corrected),
            Facts = facts,
            Category = category,
            Severity = e.EventId == 18 ? DiagSeverity.Critical : null,
            Signature = Sig(("id", e.EventId.ToString(CultureInfo.InvariantCulture)), ("component", component), ("source", errorSource),
                ("bank", e.Get("MCABank")), ("device", e.Get("PrimaryDeviceName")), ("vendor", e.Get("VendorID")), ("dev", e.Get("DeviceID"))),
        };
    }

    internal static string WheaComponent(IEventFields e, string? errorSource)
    {
        if (e.EventId == 17 || e.Get("PrimaryDeviceName") is not null || e.Get("VendorID") is not null || errorSource is "4" or "PCIe"
            || ContainsAny(e.Message, "PCI Express", "PCIe"))
        {
            return "pcie";
        }

        if (e.EventId == 47 || MemoryComponent().IsMatch(e.Message ?? ""))
        {
            return "memory";
        }

        if (e.EventId is 18 or 19 || e.Get("MCABank") is not null || e.Get("ApicId") is not null || errorSource is "0" or "1"
            || ContainsAny(e.Message, "Processor", "프로세서"))
        {
            return "cpu";
        }

        return "unknown";
    }

    private static string ComponentName(string component) => component switch
    {
        "pcie" => "PCIe",
        "memory" => "메모리",
        "cpu" => "프로세서(머신 체크)",
        _ => "구성 요소 확인 불가",
    };

    /// <summary>WHEA_ERROR_SOURCE_TYPE values (Windows driver kit documentation).</summary>
    public static string WheaErrorSourceName(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return value;
        }

        var name = number switch
        {
            0 => "MCE (Machine Check Exception)",
            1 => "CMC (Corrected Machine Check)",
            2 => "CPE (Corrected Platform Error)",
            3 => "NMI",
            4 => "PCIe (AER)",
            5 => "Generic",
            6 => "INIT",
            7 => "BOOT",
            8 => "SCI Generic",
            12 => "Generic V2",
            13 => "SCI Generic V2",
            14 => "BMC",
            15 => "PMEM",
            16 => "Device Driver",
            _ => null,
        };
        return name is null ? value : $"{value} ({name})";
    }

    public static Interpretation ServiceControlManager(IEventFields e)
    {
        var service = e.EventId switch
        {
            7009 or 7011 => e.Field("param2|1"),
            7045 => e.Get("ServiceName"),
            _ => e.Field("param1|0"),
        };
        var facts = new List<EventFact>();
        Add(facts, e.EventId == 7026 ? "드라이버" : "서비스", service);
        if (e.EventId == 7031)
        {
            Add(facts, "누적 종료 횟수", e.Field("param2|1"));
            Add(facts, "복구 동작", e.Field("param5|4"));
        }
        else if (e.EventId is 7023 or 7024 or 7000 or 7001)
        {
            Add(facts, "오류", e.Field("param2|1"));
        }
        else if (e.EventId == 7045)
        {
            Add(facts, "이미지 경로", e.Get("ImagePath"));
            Add(facts, "서비스 유형", e.Get("ServiceType"));
            Add(facts, "시작 유형", e.Get("StartType"));
        }

        return new Interpretation { TitleDetail = service, Facts = facts, Signature = Sig(("service", service)) };
    }

    public static Interpretation Device(IEventFields e)
    {
        var device = e.GetAt(0);
        var facts = new List<EventFact>();
        Add(facts, "장치", device);
        return new Interpretation { TitleDetail = device, Facts = facts, Signature = Sig(("device", device)) };
    }

    public static Interpretation Display4101(IEventFields e)
    {
        var driver = e.GetAt(0);
        var facts = new List<EventFact>();
        Add(facts, "드라이버", driver);
        return new Interpretation { TitleDetail = driver, Facts = facts, Signature = Sig(("driver", driver)) };
    }

    /// <summary>Schannel 36874/36887/36888: a TLS handshake that failed; the process and protocol come from the event data.</summary>
    public static Interpretation Schannel(IEventFields e)
    {
        var process = e.Get("CallerProcessImageName");
        var protocol = e.Get("Protocol");
        var facts = new List<EventFact>();
        Add(facts, "연결을 처리한 프로세스", process,
            process?.Contains("TermService", StringComparison.OrdinalIgnoreCase) == true ? "원격 데스크톱 서비스입니다." : null);
        Add(facts, "프로토콜", protocol);
        Add(facts, "경고 코드", e.Get("AlertDesc"));
        return new Interpretation
        {
            TitleDetail = Join(" · ", process, protocol),
            Facts = facts,
            Signature = Sig(("process", process), ("protocol", protocol), ("alert", e.Get("AlertDesc"))),
        };
    }

    /// <summary>DCOM 10010: a COM server did not register in time. The package version is left out of the grouping.</summary>
    public static Interpretation Dcom10010(IEventFields e)
    {
        var server = e.Field("param1|0");
        var facts = new List<EventFact>();
        Add(facts, "서버", server);
        var group = server is null ? null : PackageVersion().Replace(server, "_");
        return new Interpretation { TitleDetail = Shorten(group, 70), Facts = facts, Signature = Sig(("server", group)) };
    }

    /// <summary>DCOM 10005: DCOM could not start a service to run a COM server.</summary>
    public static Interpretation Dcom10005(IEventFields e)
    {
        var service = e.Field("param2|1");
        var facts = new List<EventFact>();
        Add(facts, "서비스", service);
        Add(facts, "오류 코드", e.Field("param1|0"));
        Add(facts, "COM 서버", e.Field("param4|3"));
        return new Interpretation { TitleDetail = service, Facts = facts, Signature = Sig(("service", service), ("error", e.Field("param1|0"))) };
    }

    /// <summary>VSS 13/8193. hr 0x8007045b means "a system shutdown is in progress": recorded while Windows was shutting down.</summary>
    public static Interpretation Vss(IEventFields e)
    {
        var hr = FirstHResult(e.Message) ?? e.Data.Select(d => FirstHResult(d.Value)).FirstOrDefault(v => v is not null);
        var routine = e.EventId == 8193 ? e.GetAt(0) : e.GetAt(1);
        var facts = new List<EventFact>();
        Add(facts, e.EventId == 8193 ? "호출한 루틴" : "COM 서버", routine);
        Add(facts, "결과 코드(hr)", hr);
        var shutdown = string.Equals(hr, "0x8007045b", StringComparison.OrdinalIgnoreCase);
        return new Interpretation
        {
            TitleDetail = shutdown ? "시스템 종료 중" : Join(" ", routine, hr),
            Facts = facts,
            Signature = Sig(("id", e.EventId.ToString(CultureInfo.InvariantCulture)), ("routine", routine), ("hr", hr)),
            Severity = shutdown ? DiagSeverity.Info : null,
            Meaning = shutdown
                ? "Windows가 종료되는 중이라 볼륨 섀도 복사본 서비스가 필요한 구성 요소를 시작하지 못했다고 기록되었습니다(0x8007045b: 시스템을 종료하고 있음). PC를 끌 때 흔히 남는 기록입니다."
                : null,
        };
    }

    /// <summary>Windows Search indexer events: the HRESULT and reason come from the event text.</summary>
    public static Interpretation WindowsSearch(IEventFields e)
    {
        var hr = FirstHResult(e.Get("ExtraInfo")) ?? FirstHResult(e.Message) ?? e.Data.Select(d => FirstHResult(d.Value)).FirstOrDefault(v => v is not null);
        var reason = e.Get("Reason");
        var facts = new List<EventFact>();
        Add(facts, "사유", reason);
        Add(facts, "결과 코드(HRESULT)", hr, string.Equals(hr, "0x80040d23", StringComparison.OrdinalIgnoreCase) ? "색인 수집기(Gatherer)가 종료되는 중이었습니다." : null);
        return new Interpretation
        {
            TitleDetail = Join(" · ", reason, hr),
            Facts = facts,
            Signature = Sig(("id", e.EventId.ToString(CultureInfo.InvariantCulture)), ("hr", hr)),
        };
    }

    /// <summary>Kernel-PnP 225: a program kept a device from being removed or ejected (it had files open on it).</summary>
    public static Interpretation KernelPnp225(IEventFields e)
    {
        var path = e.Get("ProcessName");
        var process = path is null ? null : path[(path.LastIndexOf('\\') + 1)..];
        var device = e.Get("DeviceInstance");
        var facts = new List<EventFact>();
        Add(facts, "막은 프로그램", process);
        Add(facts, "프로그램 경로", path);
        Add(facts, "장치 인스턴스", device);
        return new Interpretation { TitleDetail = Join(" → ", process, device), Facts = facts, Signature = Sig(("process", process), ("device", device)) };
    }

    private static string? FirstHResult(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = HResult().Match(text);
        return match.Success ? "0x" + match.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static Interpretation MessageFirstLine(IEventFields e)
    {
        var line = FirstLine(e.Message);
        return new Interpretation { TitleDetail = Shorten(line, 90) };
    }

    public static Interpretation KernelPnpDevice(IEventFields e)
    {
        var device = e.Get("DeviceInstanceId");
        var driver = e.Get("DriverName");
        var version = e.Get("DriverVersion");
        var facts = new List<EventFact>();
        Add(facts, "장치 인스턴스", device);
        Add(facts, "드라이버(INF)", driver);
        Add(facts, "드라이버 버전", version);
        Add(facts, "드라이버 날짜", e.Get("DriverDate"));
        Add(facts, "드라이버 제공자", e.Get("DriverProvider"));
        Add(facts, "기본 제공 드라이버", e.Get("DriverInbox"));
        Add(facts, "문제 코드", e.Get("Problem"));
        Add(facts, "상태", e.Get("Status"));
        return new Interpretation
        {
            TitleDetail = Join(" ", driver ?? device, version),
            Facts = facts,
            Signature = Sig(("device", device), ("version", e.EventId == 400 ? version : null)),
        };
    }

    public static Interpretation KernelPnp219(IEventFields e)
    {
        var driver = e.Get("DriverName");
        var facts = new List<EventFact>();
        Add(facts, "드라이버", driver);
        Add(facts, "장치", e.Get("FailureName"));
        Add(facts, "상태", e.Get("Status"));
        return new Interpretation { TitleDetail = driver, Facts = facts, Signature = Sig(("driver", driver)) };
    }

    public static Interpretation UserPnp(IEventFields e)
    {
        var description = e.Get("DriverDescription") ?? e.Get("DriverName");
        var version = e.Get("DriverVersion");
        var facts = new List<EventFact>();
        foreach (var item in e.Data.Where(d => d.Name is not null).Take(12))
        {
            facts.Add(new EventFact(item.Name!, item.Value));
        }

        return new Interpretation
        {
            TitleDetail = description is null ? Shorten(FirstLine(e.Message), 90) : Join(" ", description, version),
            Facts = facts,
            Signature = Sig(("driver", e.Get("DriverName")), ("device", e.Get("DeviceInstanceID") ?? e.Get("DeviceInstanceId")), ("version", version)),
        };
    }

    public static Interpretation WindowsUpdate(IEventFields e)
    {
        var title = e.Get("updateTitle");
        var error = e.Get("errorCode");
        var kb = Kb(title) ?? Kb(e.Message);
        var facts = new List<EventFact>();
        Add(facts, "업데이트", title);
        Add(facts, "KB", kb);
        Add(facts, "오류 코드", error);
        return new Interpretation
        {
            TitleDetail = Shorten(kb is not null && title is not null && title.Length > 60 ? kb : title, 90),
            Facts = facts,
            Signature = Sig(("kb", kb ?? title), ("error", error)),
        };
    }

    public static Interpretation Servicing(IEventFields e)
    {
        var package = e.Get("PackageIdentifier");
        var kb = Kb(package) ?? Kb(e.Message);
        var facts = new List<EventFact>();
        Add(facts, "패키지", package);
        Add(facts, "KB", kb);
        Add(facts, "목표 상태", e.Get("IntendedPackageState"));
        Add(facts, "오류 코드", e.Get("ErrorCode"));
        return new Interpretation
        {
            TitleDetail = kb ?? Shorten(package, 60),
            Facts = facts,
            Signature = Sig(("kb", kb ?? package)),
        };
    }

    public static Interpretation Msi(IEventFields e)
    {
        var product = e.GetAt(0);
        var facts = new List<EventFact>();
        Add(facts, "제품", product);
        Add(facts, "버전", e.EventId is 1033 or 1034 ? e.GetAt(1) : null);
        return new Interpretation { TitleDetail = Shorten(product, 80), Facts = facts, Signature = Sig(("product", product)) };
    }

    public static Interpretation LogCleared(IEventFields e)
    {
        var channel = e.Get("Channel");
        var facts = new List<EventFact>();
        Add(facts, "지워진 로그", channel);
        return new Interpretation { TitleDetail = channel, Facts = facts, Signature = Sig(("channel", channel)) };
    }

    public static Interpretation User32Shutdown(IEventFields e)
    {
        var type = e.Field("param5|4");
        var facts = new List<EventFact>();
        Add(facts, "요청한 프로세스", e.Field("param1|0"));
        Add(facts, "사유", e.Field("param3|2"));
        Add(facts, "종류", type);
        Add(facts, "설명", e.Field("param6|5"));
        return new Interpretation { TitleDetail = type, Facts = facts, Signature = Sig(("type", type)) };
    }

    public static Interpretation OsVersion6009(IEventFields e)
    {
        var facts = new List<EventFact>();
        Add(facts, "버전", e.GetAt(0));
        Add(facts, "빌드", e.GetAt(1));
        return new Interpretation { TitleDetail = Join(" ", e.GetAt(0), e.GetAt(1)), Facts = facts, Signature = "boot-version" };
    }

    // ------------------------------------------------------------------ helpers

    public static string? Kb(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = KbNumber().Match(text);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public static string? Shorten(string? text, int max)
        => text is null || text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    private static string? LineValue(string text, string prefix)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[prefix.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }

    /// <summary>Stable "key=value; ..." signature from the present parts, lower-cased for grouping.</summary>
    public static string? Sig(params (string Key, string? Value)[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Key}={p.Value!.Trim().ToLowerInvariant()}").ToList();
        return present.Count == 0 ? null : string.Join("; ", present);
    }

    private static void Add(List<EventFact> facts, string label, string? value, string? note = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add(new EventFact(label, value.Trim(), note));
        }
    }

    private static void AddIfPresent(List<EventFact> facts, IEventFields e, string field, string? label = null)
        => Add(facts, label is null ? field : $"{label} ({field})", e.Get(field));

    private static bool IsNonZero(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex != 0;
        }

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number != 0 : value != "false";
    }

    private static bool ContainsAny(string? text, params string[] needles)
        => text is not null && needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,16})\s*(?:\(([^)]*)\))?", RegexOptions.CultureInvariant)]
    private static partial Regex BugCheckText();

    /// <summary>An 8-digit HRESULT such as 0x8007045b (not the "0x%08x" format placeholder some events contain).</summary>
    [GeneratedRegex(@"0x([0-9a-fA-F]{8})\b", RegexOptions.CultureInvariant)]
    private static partial Regex HResult();

    /// <summary>The version part of a package full name ("_2.2608.303.0_").</summary>
    [GeneratedRegex(@"_\d+(?:\.\d+){1,3}_", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersion();

    [GeneratedRegex(@"KB\d{6,8}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KbNumber();

    [GeneratedRegex(@"(Component:\s*Memory|구성\s*요소:\s*메모리|Memory\s+Error)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MemoryComponent();
}
