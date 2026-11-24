using WinDiag.Core.Crash;
using WinDiag.Core.Events;

namespace WinDiag.Tests.Core;

public class EventXmlParserTests
{
    [Fact]
    public void Parses_system_fields_and_named_data()
    {
        var e = Fixtures.Event("kernel-power-41.xml");
        Assert.Equal("System", e.Channel);
        Assert.Equal("Microsoft-Windows-Kernel-Power", e.Provider);
        Assert.Equal(41, e.EventId);
        Assert.Equal(1, e.Level);
        Assert.Equal(48211, e.RecordId);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 44, 5, 491, TimeSpan.Zero), e.TimeCreated, TimeSpan.FromMilliseconds(1));
        Assert.Equal(4, e.ProcessId);
        Assert.Equal("292", e.Get("BugcheckCode"));
        Assert.Equal(unchecked((long)0x8000400000000002), e.Keywords);
    }

    [Fact]
    public void Parses_unnamed_classic_data_qualifiers_and_event_source_name()
    {
        var display = Fixtures.Event("display-4101.xml");
        Assert.Equal("nvlddmkm", display.GetAt(0));
        Assert.Null(display.Data[0].Name);
        Assert.Equal(0, display.Qualifiers);

        var bugcheck = Fixtures.Event("bugcheck-1001.xml");
        Assert.Equal("BugCheck", bugcheck.EventSourceName);
        Assert.Equal(16384, bugcheck.Qualifiers);
    }

    [Fact]
    public void Flattens_user_data_and_reads_rendering_info()
    {
        var cleared = Fixtures.Event("eventlog-104.xml");
        Assert.Equal("System", cleared.Get("Channel"));
        Assert.Equal("kimtester", cleared.Get("SubjectUserName"));

        var rendered = Fixtures.Event("rendered-6008.xml");
        Assert.StartsWith("이전 시스템 종료", rendered.Message);
        Assert.Contains(rendered.Data, d => d.Name == "(Binary)");
    }

    [Fact]
    public void Parses_wevtutil_output_without_root_element()
    {
        using var reader = new StringReader(Fixtures.Text("events", "wevtutil-multi.xml"));
        var events = EventXmlParser.ParseMany(reader).ToList();
        Assert.Equal([6005, 153, 20], events.Select(e => e.EventId));
    }

    [Fact]
    public void Rejects_dtd_and_missing_required_fields()
    {
        const string dtd = "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Event><System><Provider Name='a'/><EventID>1</EventID></System></Event>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => EventXmlParser.Parse(dtd));
        Assert.Throws<FormatException>(() => EventXmlParser.Parse("<Event><System><Provider Name='a'/><EventID>1</EventID></System></Event>"));
    }

    [Fact]
    public void Truncates_oversized_values()
    {
        var big = new string('x', EventXmlParser.MaxValueLength + 50);
        var xml = $"<Event><System><Provider Name='p'/><EventID>1</EventID><Level>2</Level><TimeCreated SystemTime='2026-10-30T00:00:00Z'/><EventRecordID>5</EventRecordID><Channel>System</Channel></System><EventData><Data>{big}</Data></EventData></Event>";
        var e = EventXmlParser.Parse(xml);
        Assert.EndsWith("…(잘림)", e.GetAt(0));
        Assert.True(e.GetAt(0)!.Length < big.Length);
    }
}

public class EventClassifierTests
{
    private static readonly EventClassifier Classifier = EventClassifier.Default;

    [Fact]
    public void Kernel_power_41_is_classified_by_provider_and_id_with_bugcheck_fact_and_caution()
    {
        var c = Classifier.Classify(Fixtures.Event("kernel-power-41.xml"));
        Assert.Equal(EventCategories.UnexpectedShutdown, c.Category);
        Assert.Equal(DiagSeverity.Critical, c.Severity);
        Assert.Contains("0x00000124", c.Title);
        Assert.Contains("WHEA_UNCORRECTABLE_ERROR", c.Title);
        Assert.Contains("파워서플라이", c.Caution);
        Assert.Equal("bugcheck=0x00000124", c.Signature);
        Assert.Contains(c.Facts, f => f.Label == "BugcheckCode" && f.Value.StartsWith("0x00000124", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_event_id_from_another_provider_is_not_a_kernel_power_event()
    {
        var other = Ev.Make("SomeVendorDriver", 41, level: 2);
        var c = Classifier.Classify(other);
        Assert.Equal(EventCategories.Other, c.Category);
        Assert.False(c.IsCatalogued);
        Assert.Equal(DiagSeverity.Error, c.Severity);
    }

    [Fact]
    public void Kernel_power_41_without_bugcheck_says_so()
    {
        var e = Ev.Make("Microsoft-Windows-Kernel-Power", 41, level: 1, data: [("BugcheckCode", "0"), ("PowerButtonTimestamp", "0")]);
        var c = Classifier.Classify(e);
        Assert.Contains("BugCheck 정보 없음", c.Title);
        Assert.Equal("nobugcheck", c.Signature);
    }

    [Fact]
    public void Bugcheck_1001_parses_code_parameters_and_dump_path()
    {
        var c = Classifier.Classify(Fixtures.Event("bugcheck-1001.xml"));
        Assert.Equal(EventCategories.BugCheck, c.Category);
        Assert.Equal(DiagSeverity.Critical, c.Severity);
        Assert.Contains("0x00000124 WHEA_UNCORRECTABLE_ERROR", c.Title);
        Assert.Contains(c.Facts, f => f.Label == "매개변수 2" && f.Value == "0xffffb08c2a6e9028");
        Assert.Contains(c.Facts, f => f.Label.StartsWith("덤프 저장 위치", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("whea-19.xml", EventCategories.HardwareCpu, "bank=5")]
    [InlineData("whea-17.xml", EventCategories.HardwarePcie, "vendor=0x1022")]
    public void Whea_component_comes_from_event_data(string file, string category, string signaturePart)
    {
        var c = Classifier.Classify(Fixtures.Event(file));
        Assert.Equal(category, c.Category);
        Assert.Contains(signaturePart, c.Signature);
        Assert.Contains(c.Facts, f => f.Label.StartsWith("구성 요소", StringComparison.Ordinal));
        Assert.Contains("확정할 수 없습니다", c.Caution);
    }

    [Fact]
    public void Whea_with_unknown_id_and_memory_message_is_memory()
    {
        var e = Ev.Make("Microsoft-Windows-WHEA-Logger", 46, level: 3, message: "A corrected hardware error has occurred.\nComponent: Memory");
        Assert.Equal(EventCategories.HardwareMemory, Classifier.Classify(e).Category);
    }

    [Fact]
    public void Display_4101_is_gpu_driver_error_grouped_by_driver()
    {
        var c = Classifier.Classify(Fixtures.Event("display-4101.xml"));
        Assert.Equal(EventCategories.GpuDriver, c.Category);
        Assert.Equal(DiagSeverity.Error, c.Severity);
        Assert.Equal("driver=nvlddmkm", c.Signature);
    }

    [Fact]
    public void App_crash_signature_is_the_same_for_named_and_positional_data()
    {
        var named = Classifier.Classify(Fixtures.Event("app-error-1000.xml"));
        Assert.Equal("app=game.exe; module=nvwgf2umx.dll; code=0xc0000005", named.Signature);
        Assert.Contains(named.Facts, f => f.Value.Contains("STATUS_ACCESS_VIOLATION", StringComparison.Ordinal));

        var classic = Classifier.Classify(Fixtures.Event("app-error-1000-classic.xml"));
        Assert.Equal("app=editor.exe; module=ntdll.dll; code=0xc0000374", classic.Signature);
        Assert.Contains(classic.Facts, f => f.Value.Contains("STATUS_HEAP_CORRUPTION", StringComparison.Ordinal));
    }

    [Fact]
    public void Named_event_without_a_field_does_not_borrow_a_positional_value()
    {
        var e = Ev.Make("Application Error", 1000, data: [("AppName", "a.exe"), ("Other", "x")]);
        var c = Classifier.Classify(e);
        Assert.Equal("app=a.exe", c.Signature);
    }

    [Fact]
    public void Service_crash_groups_by_service_name()
    {
        var c = Classifier.Classify(Fixtures.Event("scm-7031.xml"));
        Assert.Equal(EventCategories.ServiceFailure, c.Category);
        Assert.Equal("service=example update service", c.Signature);
    }

    [Fact]
    public void Large_clock_change_is_a_warning_with_delta_from_nine_digit_times()
    {
        var c = Classifier.Classify(Fixtures.Event("kernel-general-1.xml"));
        Assert.Equal(EventCategories.TimeChange, c.Category);
        Assert.Equal(DiagSeverity.Warning, c.Severity);
        Assert.Equal("large", c.Signature);
        Assert.Contains(c.Facts, f => f.Label == "변경량" && f.Value.StartsWith('+'));
    }

    [Fact]
    public void Live_kernel_event_0x141_is_reclassified_as_gpu()
    {
        var e = Ev.Make("Windows Error Reporting", 1001, level: 4, channel: "Application",
            data: [(null, "bucket"), (null, "0"), (null, "LiveKernelEvent"), (null, "n/a"), (null, "0"), (null, "141"), (null, "x")]);
        var c = Classifier.Classify(e);
        Assert.Equal(EventCategories.GpuDriver, c.Category);
        Assert.Contains("VIDEO_ENGINE_TIMEOUT_DETECTED", c.Title);
    }

    [Fact]
    public void Dcom_10016_is_demoted_with_microsoft_guidance()
    {
        var e = Ev.Make("Microsoft-Windows-DistributedCOM", 10016, level: 3);
        var c = Classifier.Classify(e);
        Assert.Equal(DiagSeverity.Info, c.Severity);
        Assert.Contains("무시해도 된다고", c.Caution);
    }

    [Fact]
    public void Unknown_events_group_by_message_template()
    {
        var a = Classifier.Classify(Ev.Make("X", 7, message: "Timeout after 3000 ms on {12345678-1234-1234-1234-123456789abc} at 0x1F"));
        var b = Classifier.Classify(Ev.Make("X", 7, message: "Timeout after 5000 ms on {87654321-1234-1234-1234-123456789abc} at 0x2A"));
        Assert.Equal(a.GroupKey, b.GroupKey);
        Assert.Equal("msg=timeout after # ms on {g} at 0x#", a.Signature);
    }

    [Fact]
    public void Windows_update_failure_extracts_kb_and_error()
    {
        using var reader = new StringReader(Fixtures.Text("events", "wevtutil-multi.xml"));
        var update = EventXmlParser.ParseMany(reader).Single(e => e.EventId == 20);
        var c = Classifier.Classify(update);
        Assert.Equal(EventCategories.UpdateFailure, c.Category);
        Assert.Contains(c.Facts, f => f.Label == "KB" && f.Value == "KB2267602");
        Assert.Contains("error=0x80070643", c.Signature);
    }

    [Fact]
    public void Every_catalog_rule_names_a_provider_and_a_known_category()
    {
        foreach (var rule in EventCatalog.Default.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Provider));
            Assert.NotEqual(rule.Category, EventCategories.Get(rule.Category).Name);
            Assert.False(string.IsNullOrWhiteSpace(rule.Title));
        }
    }

    [Fact]
    public void Bugcheck_codes_parse_in_decimal_and_hex()
    {
        Assert.True(BugCheckCatalog.TryParseCode("292", decimalByDefault: true, out var dec));
        Assert.Equal(0x124u, dec);
        Assert.True(BugCheckCatalog.TryParseCode("0x116", decimalByDefault: true, out var hex));
        Assert.Equal(0x116u, hex);
        Assert.Equal("0x00000116 VIDEO_TDR_FAILURE", BugCheckCatalog.Describe(0x116));
        Assert.Equal("알 수 없는 코드", BugCheckCatalog.Name(0xDEAD));
    }
}

/// <summary>
/// Rules added after the first run on a real PC (2026-11-05). The events below follow the field layout of events seen
/// there; process IDs, package versions and the USB serial are made up.
/// </summary>
public class CommonWindowsErrorRuleTests
{
    private static readonly EventClassifier Classifier = EventClassifier.Default;

    [Fact]
    public void Schannel_tls_failure_names_the_process_and_is_only_a_warning()
    {
        var c = Classifier.Classify(Ev.Make("Schannel", 36874, level: 2,
            message: "원격 클라이언트 응용 프로그램으로부터 TLS 1.0 연결 요청을 받았지만 …",
            data: [("CallerProcessId", "7648"), ("CallerProcessImageName", "svchost[TermService]"), ("Protocol", "TLS 1.0")]));
        Assert.True(c.IsCatalogued);
        Assert.Equal(DiagSeverity.Warning, c.Severity);
        Assert.Equal("process=svchost[termservice]; protocol=tls 1.0", c.Signature);
        Assert.Contains(c.Facts, f => f.Note == "원격 데스크톱 서비스입니다.");
        Assert.Contains("공격 여부", c.Caution);
    }

    [Fact]
    public void Shadow_copy_errors_during_shutdown_are_informational()
    {
        var shutdown = Classifier.Classify(Ev.Make("VSS", 8193, channel: "Application",
            message: "볼륨 섀도 복사본 서비스 오류: CoCreateInstance 루틴을 호출하는 동안 예기치 못한 오류가 발생했습니다. hr = 0x8007045b, 시스템을 종료하고 있습니다.",
            data: [(null, "CoCreateInstance"), (null, "0x8007045b, 시스템을 종료하고 있습니다.\n"), (null, "")]));
        Assert.Equal(DiagSeverity.Info, shutdown.Severity);
        Assert.EndsWith("시스템 종료 중", shutdown.Title, StringComparison.Ordinal);
        Assert.Contains("PC를 끌 때", shutdown.Meaning);

        var other = Classifier.Classify(Ev.Make("VSS", 8193, channel: "Application", message: "… hr = 0x80042302 …",
            data: [(null, "CoCreateInstance"), (null, "0x80042302")]));
        Assert.Equal(DiagSeverity.Warning, other.Severity);
        Assert.NotEqual(shutdown.GroupKey, other.GroupKey);
    }

    [Fact]
    public void Windows_search_failure_reads_the_hresult_not_the_format_placeholder()
    {
        var c = Classifier.Classify(Ev.Make("Microsoft-Windows-Search", 7042, channel: "Application",
            message: "인덱서 복구 단계 실패에 문제가 있어 Windows Search 서비스를 중지하는 중입니다.",
            data: [("ExtraInfo", "\n\n컨텍스트:  응용 프로그램, SystemIndex 카탈로그\n\n자세히:\n\t0x%08x (0x80040d23 - Gatherer를 종료하는 중입니다.  (HRESULT : 0x80040d23))"), ("Reason", "복구 단계 실패")]));
        Assert.Equal(EventCategories.ServiceFailure, c.Category);
        Assert.Equal(DiagSeverity.Error, c.Severity);
        Assert.Equal("id=7042; hr=0x80040d23", c.Signature);
        Assert.Contains(c.Facts, f => f.Label == "사유" && f.Value == "복구 단계 실패");
    }

    [Fact]
    public void Dcom_timeouts_of_one_app_group_together_across_versions()
    {
        var a = Classifier.Classify(Ev.Make("Microsoft-Windows-DistributedCOM", 10010, data: [("param1", "Microsoft.WindowsFeedbackHub_2.2608.303.0_x64__8wekyb3d8bbwe!App.AppX1.mca")]));
        var b = Classifier.Classify(Ev.Make("Microsoft-Windows-DistributedCOM", 10010, data: [("param1", "Microsoft.WindowsFeedbackHub_2.2609.11.0_x64__8wekyb3d8bbwe!App.AppX1.mca")]));
        Assert.Equal(a.GroupKey, b.GroupKey);
        Assert.Equal(DiagSeverity.Warning, a.Severity);

        var service = Classifier.Classify(Ev.Make("Microsoft-Windows-DistributedCOM", 10005,
            data: [("param1", "87"), ("param2", "GamingServices"), ("param3", "사용할 수 없음"), ("param4", "{3E8C9ABE-9226-4609-BF5B-60288A391DEE}")]));
        Assert.Equal("service=gamingservices; error=87", service.Signature);
    }

    [Fact]
    public void Blocked_device_removal_names_the_program_and_device()
    {
        var c = Classifier.Classify(Ev.Make("Microsoft-Windows-Kernel-PnP", 225, level: 3,
            data: [("ProcessId", "13080"), ("ProcessName", @"\Device\HarddiskVolume3\Windows\System32\SearchIndexer.exe"), ("DeviceInstance", @"USB\VID_1234&PID_5678\TESTSERIAL01")]));
        Assert.Equal("프로그램이 장치 제거·꺼내기를 막음 (Kernel-PnP 225): SearchIndexer.exe → USB\\VID_1234&PID_5678\\TESTSERIAL01", c.Title);
        Assert.Contains("고장이 아닙니다", c.Caution);
    }

    [Fact]
    public void Noisy_providers_add_their_errors_but_not_their_warnings_to_collection()
    {
        var settings = new WinDiag.Core.Settings.AppSettings();
        settings.Normalize();
        var system = WinDiag.Collection.Sources.EventFilter.Build("System", EventCatalog.Default, settings.Collection);
        Assert.True(system.Matches(Ev.Make("Schannel", 36874, level: 2)));
        Assert.False(system.Matches(Ev.Make("Schannel", 36888, level: 3)));
        Assert.True(system.Matches(Ev.Make("Microsoft-Windows-Kernel-PnP", 225, level: 3)));
        Assert.True(system.Matches(Ev.Make("Microsoft-Windows-DistributedCOM", 10016, level: 3))); // the 10016 rule still collects DCOM warnings
    }
}
