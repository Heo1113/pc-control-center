using System.Globalization;
using LocalAiPerf.Core.Hardware;
using WinDiag.Core;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Core.Snapshots;
using WinDiag.Storage;

namespace WinDiag.Collection.Sources;

/// <summary>
/// A synthetic 30-day history used by automated tests, the CLI "demo" command and GUI checks on machines without
/// errors: a GPU driver update followed by display driver resets and app crashes, an EXPO change followed by
/// corrected WHEA errors, a BugCheck 0x116, a power-loss style restart, a repeating service crash, disk retries,
/// a clock change and known noise. Every event is stored as virtual and every message starts with "[가상]".
/// It is not a measurement of any real PC.
/// </summary>
public static class VirtualScenario
{
    public const string Marker = "[가상] ";

    public static DateTimeOffset DriverUpdate(DateTimeOffset now) => Day(now, -14, 20, 0);
    public static DateTimeOffset ExpoChange(DateTimeOffset now) => Day(now, -10, 19, 30);
    public static DateTimeOffset BugCheckCrash(DateTimeOffset now) => Day(now, -6, 22, 41);

    public static MemoryEventSource BuildSource(DateTimeOffset now)
    {
        var source = new MemoryEventSource("가상 시나리오", isVirtual: true);
        var events = new List<RawEvent>();
        for (var day = -30; day <= 0; day++)
        {
            var boot = Day(now, day, 9, 0);
            if (boot > now)
            {
                break;
            }

            events.Add(Ev("System", "EventLog", 6005, 4, boot, "이벤트 로그 서비스가 시작되었습니다."));
            events.Add(Ev("System", "Microsoft-Windows-Kernel-General", 12, 4, boot.AddSeconds(-20), "운영 체제가 시작되었습니다.",
                ("StartTime", IsoUtc(boot.AddSeconds(-25)))));
            events.Add(Ev("System", "Microsoft-Windows-DistributedCOM", 10016, 3, boot.AddMinutes(3),
                "응용 프로그램별 권한 설정이 로컬 활성화 권한을 부여하지 않습니다.", ("param1", "응용 프로그램별"), ("param2", "로컬"), ("param3", "활성화")));
            var shutdown = Day(now, day, 23, 30);
            if (shutdown < now && day != -6 && day != -2)
            {
                events.Add(Ev("System", "User32", 1074, 4, shutdown.AddSeconds(-5), "프로세스가 컴퓨터의 전원 끄기를 시작했습니다.",
                    ("param1", @"C:\Windows\System32\RuntimeBroker.exe"), ("param2", "VIRTUAL-PC"), ("param3", "기타(계획되지 않음)"),
                    ("param4", "0x0"), ("param5", "전원 끄기"), ("param6", ""), ("param7", @"VIRTUAL-PC\tester")));
                events.Add(Ev("System", "EventLog", 6006, 4, shutdown, "이벤트 로그 서비스가 중지되었습니다."));
            }
        }

        // A Windows update.
        var update = Day(now, -25, 21, 0);
        events.Add(Ev("System", "Microsoft-Windows-WindowsUpdateClient", 43, 4, update, "업데이트 설치를 시작했습니다: 2026-10 누적 업데이트 (KB5070001)",
            ("updateTitle", "2026-10 x64 기반 시스템용 Windows 11 누적 업데이트 (KB5070001)"), ("updateGuid", "{11111111-2222-3333-4444-555555555555}")));
        events.Add(Ev("System", "Microsoft-Windows-WindowsUpdateClient", 19, 4, update.AddMinutes(12), "설치 성공: 2026-10 누적 업데이트 (KB5070001)",
            ("updateTitle", "2026-10 x64 기반 시스템용 Windows 11 누적 업데이트 (KB5070001)"), ("updateGuid", "{11111111-2222-3333-4444-555555555555}")));
        events.Add(Ev("System", "Microsoft-Windows-WindowsUpdateClient", 20, 2, Day(now, -11, 21, 5),
            "설치 실패: 0x80070643 오류로 업데이트를 설치하지 못했습니다 (KB5070999)",
            ("errorCode", "0x80070643"), ("updateTitle", "2026-10 보안 인텔리전스 업데이트 (KB5070999)"), ("updateGuid", "{99999999-2222-3333-4444-555555555555}")));

        // GPU driver update (14 days ago).
        var driver = DriverUpdate(now);
        events.Add(Ev("System", "Microsoft-Windows-UserPnp", 20001, 4, driver,
            @"드라이버 관리가 PCI\VEN_10DE&DEV_2C02\4&1a2b3c4d&0&0008 장치에 대해 oem42.inf 드라이버 설치를 마쳤습니다. 상태: 0x0",
            ("DriverName", "oem42.inf"), ("DriverVersion", "32.0.15.8115"), ("DriverProvider", "NVIDIA"),
            ("DeviceInstanceID", @"PCI\VEN_10DE&DEV_2C02&SUBSYS_00000000&REV_A1\4&1a2b3c4d&0&0008"), ("DriverDescription", "NVIDIA GeForce RTX 5080"),
            ("SetupClass", "{4d36e968-e325-11ce-bfc1-08002be10318}"), ("InstallStatus", "0x0")));
        events.Add(Ev("Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-Kernel-PnP", 400, 4, driver.AddSeconds(30),
            "장치가 구성되었습니다.", ("DeviceInstanceId", @"PCI\VEN_10DE&DEV_2C02&SUBSYS_00000000&REV_A1\4&1a2b3c4d&0&0008"),
            ("DriverName", "oem42.inf"), ("ClassGuid", "{4d36e968-e325-11ce-bfc1-08002be10318}"), ("DriverDate", "10/20/2026"),
            ("DriverVersion", "32.0.15.8115"), ("DriverProvider", "NVIDIA"), ("DriverInbox", "false")));

        // Display driver resets after the driver update, with an NVIDIA driver event just before and app crashes after.
        foreach (var (day, hour, minute, crashApp) in new[] { (-12, 22, 5, false), (-9, 21, 47, true), (-6, 22, 30, true), (-3, 22, 12, false), (-1, 21, 55, true) })
        {
            var tdr = Day(now, day, hour, minute);
            events.Add(Ev("System", "nvlddmkm", 153, 2, tdr.AddSeconds(-2), @"\Device\Video3 Error occurred on GPUID: 100", (null, @"\Device\Video3"), (null, "Error occurred on GPUID: 100")));
            events.Add(Ev("System", "Display", 4101, 3, tdr, "디스플레이 드라이버 nvlddmkm이(가) 응답을 중지했지만 성공적으로 복구되었습니다.", (null, "nvlddmkm"), (null, "")));
            events.Add(Ev("Application", "Windows Error Reporting", 1001, 4, tdr.AddSeconds(20), "오류 버킷 LKD_0x141_Tdr:6_IMAGE_nvlddmkm.sys, 유형 0",
                (null, "LKD_0x141_Tdr:6_IMAGE_nvlddmkm.sys"), (null, "0"), (null, "LiveKernelEvent"), (null, "사용할 수 없음"), (null, "0"),
                (null, "141"), (null, "ffffd00000000000"), (null, "fffff80000000000"), (null, "0"), (null, "")));
            if (crashApp)
            {
                events.Add(Ev("Application", "Application Error", 1000, 2, tdr.AddSeconds(35),
                    "오류 있는 응용 프로그램 이름: game.exe, 오류 있는 모듈 이름: nvwgf2umx.dll, 예외 코드: 0xc0000005",
                    ("AppName", "game.exe"), ("AppVersion", "1.4.2.0"), ("AppTimeStamp", "66f0a1b2"), ("ModuleName", "nvwgf2umx.dll"),
                    ("ModuleVersion", "32.0.15.8115"), ("ModuleTimeStamp", "66f00000"), ("ExceptionCode", "c0000005"), ("FaultingOffset", "0000000000a1b2c3"),
                    ("ProcessId", "0x1f40"), ("ProcessCreationTime", "0x1db00000000"), ("AppPath", @"C:\Users\tester\Games\game.exe"),
                    ("ModulePath", @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64\nvwgf2umx.dll"), ("IntegratorReportId", "{00000000-0000-0000-0000-000000000001}")));
            }
        }

        // Corrected WHEA processor errors after the EXPO change (10 days ago).
        foreach (var (day, hour, minute) in new[] { (-8, 20, 10), (-6, 21, 2), (-6, 21, 40), (-6, 22, 20), (-3, 20, 15), (-1, 21, 30), (-1, 21, 50) })
        {
            events.Add(Ev("System", "Microsoft-Windows-WHEA-Logger", 19, 3, Day(now, day, hour, minute),
                "수정된 하드웨어 오류가 발생했습니다. 보고 구성 요소: Processor Core, 오류 원본: Corrected Machine Check, 오류 유형: Bus/Interconnect Error",
                ("ErrorSource", "1"), ("ApicId", "0"), ("MCABank", "5"), ("MciStat", "0x9c20000000000000"), ("MciAddr", "0x0"), ("ErrorType", "9"),
                ("TransactionType", "256"), ("Participation", "0"), ("RequestType", "0"), ("MemorIO", "0"), ("MemHierarchyLvl", "3")));
        }

        // A BugCheck 0x116 (VIDEO_TDR_FAILURE) 6 days ago, recorded at the next boot.
        var crash = BugCheckCrash(now);
        var reboot = crash.AddMinutes(3);
        events.Add(Ev("System", "EventLog", 6005, 4, reboot, "이벤트 로그 서비스가 시작되었습니다."));
        events.Add(Ev("System", "Microsoft-Windows-Kernel-Power", 41, 1, reboot.AddSeconds(5), "시스템을 정상적으로 종료하지 못하고 다시 부팅했습니다.",
            ("BugcheckCode", "278"), ("BugcheckParameter1", "0xffffc00012345010"), ("BugcheckParameter2", "0xfffff80412345678"),
            ("BugcheckParameter3", "0xffffffffc000009a"), ("BugcheckParameter4", "0x4"), ("SleepInProgress", "0"), ("PowerButtonTimestamp", "0"),
            ("BootAppStatus", "0"), ("LongPowerButtonPressDetected", "false")));
        events.Add(Ev("System", "EventLog", 6008, 2, reboot.AddSeconds(8), $"이전 시스템 종료({crash.ToLocalTime():HH:mm:ss})가 예기치 않게 발생했습니다.",
            (null, crash.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)), (null, crash.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        events.Add(Ev("System", "volmgr", 162, 4, reboot.AddSeconds(2), "덤프 파일 생성에 성공했습니다."));
        events.Add(Ev("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 2, reboot.AddSeconds(40),
            "버그 검사 후 컴퓨터가 다시 시작되었습니다. 버그 검사: 0x00000116",
            ("param1", "0x00000116 (0xffffc00012345010, 0xfffff80412345678, 0xffffffffc000009a, 0x0000000000000004)"),
            ("param2", @"C:\Windows\Minidump\" + crash.ToString("MMddyy", CultureInfo.InvariantCulture) + "-12000-01.dmp"),
            ("param3", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));

        // A restart without bug check information 2 days ago (power loss or hard hang).
        var powerLoss = Day(now, -2, 23, 10);
        events.Add(Ev("System", "EventLog", 6005, 4, powerLoss.AddMinutes(4), "이벤트 로그 서비스가 시작되었습니다."));
        events.Add(Ev("System", "Microsoft-Windows-Kernel-Power", 41, 1, powerLoss.AddMinutes(4).AddSeconds(5), "시스템을 정상적으로 종료하지 못하고 다시 부팅했습니다.",
            ("BugcheckCode", "0"), ("BugcheckParameter1", "0x0"), ("BugcheckParameter2", "0x0"), ("BugcheckParameter3", "0x0"), ("BugcheckParameter4", "0x0"),
            ("SleepInProgress", "0"), ("PowerButtonTimestamp", "0"), ("BootAppStatus", "0"), ("LongPowerButtonPressDetected", "false")));
        events.Add(Ev("System", "EventLog", 6008, 2, powerLoss.AddMinutes(4).AddSeconds(8), "이전 시스템 종료가 예기치 않게 발생했습니다.",
            (null, powerLoss.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)), (null, powerLoss.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

        // A service that keeps crashing, disk retries and a clock change.
        foreach (var day in new[] { -20, -15, -10, -5 })
        {
            events.Add(Ev("System", "Service Control Manager", 7031, 2, Day(now, day, 14, 20), "Example Update Service 서비스가 예기치 않게 종료되었습니다.",
                ("param1", "Example Update Service"), ("param2", "1"), ("param3", "60000"), ("param4", "1"), ("param5", "서비스 다시 시작")));
        }

        foreach (var day in new[] { -18, -4 })
        {
            events.Add(Ev("System", "disk", 153, 3, Day(now, day, 11, 5), @"\Device\Harddisk1\DR1 디스크의 논리 블록 주소 0x1234에 대한 IO 작업이 다시 시도되었습니다.",
                (null, @"\Device\Harddisk1\DR1"), (null, "0x1234"), (null, "0")));
        }

        var clock = Day(now, -8, 3, 0);
        events.Add(Ev("System", "Microsoft-Windows-Kernel-General", 1, 4, clock, "시스템 시간이 변경되었습니다.",
            ("NewTime", IsoUtc(clock.AddHours(1))), ("OldTime", IsoUtc(clock)), ("Reason", "1")));

        foreach (var e in events.OrderBy(e => e.TimeCreated))
        {
            source.Add(e with { Message = e.Message is null ? null : Marker + e.Message });
        }

        source.EnsureChannel("Setup");
        return source;
    }

    /// <summary>
    /// Adds the scenario's non-event data: virtual snapshots (before/after the driver update), the user's EXPO record,
    /// a baseline, and mock sensor samples around every display driver reset (stored in a mock app run).
    /// </summary>
    public static void SeedSideData(DiagnosticsStore store, DateTimeOffset now)
    {
        var driver = DriverUpdate(now);
        var before = MockSnapshot(driver.AddDays(-16), "32.0.15.7680", "581.80");
        var after = MockSnapshot(driver.AddMinutes(30), "32.0.15.8115", "581.15");
        store.SaveSnapshot(before, "virtual");
        store.SaveSnapshot(after, "virtual");

        var expo = ExpoChange(now);
        var recordId = store.AddUserRecord(new UserRecord
        {
            Time = expo,
            Kind = UserRecordKinds.MemoryExpo,
            Title = Marker + "EXPO 프로필 1 적용 (DDR5-6000)",
            Detail = "BIOS에서 EXPO를 켰습니다. (가상 시나리오의 사용자 입력 예)",
            Values = [new UserValueChange("메모리 속도", "4800", "6000", "MT/s"), new UserValueChange("VDD", "1.10", "1.35", "V")],
        });
        store.AddBaseline(new Baseline { Name = Marker + "GPU 드라이버 581.15 설치", Time = driver, Description = "가상 시나리오 기준선" });
        store.AddBaseline(new Baseline { Name = Marker + "EXPO 적용", Time = expo, UserRecordId = recordId });

        // Closed from the start: if seeding is interrupted, the virtual session must not look like a real unclean end.
        var run = store.AddFinishedRun(now.AddDays(-13), now.AddDays(-1).AddHours(2), RunEndReasons.Normal, AppInfo.Version + "+virtual", isMock: true,
            sensorIntervalMs: 5000, elevated: false);
        var samples = new List<HardwareSnapshot>();
        foreach (var (day, hour, minute) in new[] { (-12, 22, 5), (-9, 21, 47), (-6, 22, 30), (-3, 22, 12), (-1, 21, 55) })
        {
            var tdr = Day(now, day, hour, minute);
            for (var t = tdr.AddMinutes(-20); t <= tdr.AddMinutes(10); t = t.AddSeconds(5))
            {
                var load = t < tdr ? Math.Min(1, (t - tdr.AddMinutes(-20)).TotalMinutes / 5) : 0.15;
                samples.Add(new HardwareSnapshot
                {
                    Timestamp = t,
                    IsMock = true,
                    System = new SystemSample { CpuUtilizationPercent = 20 + 35 * load, MemoryUsedBytes = (long)(24e9 + 6e9 * load), MemoryTotalBytes = 64L << 30 },
                    Gpus =
                    [
                        new GpuSample
                        {
                            Index = 0, Name = "MOCK GPU (가상)", UtilizationPercent = 10 + 88 * load, TemperatureC = 45 + 33 * load, PowerW = 40 + 300 * load,
                            PowerLimitW = 360, MemoryUsedBytes = (long)(3e9 + 9e9 * load), MemoryTotalBytes = 16L << 30, GraphicsClockMhz = 600 + 2100 * load,
                            MemoryClockMhz = 15000, FanSpeedPercent = 30 + 50 * load,
                        },
                    ],
                });
            }
        }

        // Ordinary evenings (18:00-21:00) with light load, so the recorded distribution is not only the busy periods.
        for (var day = -13; day <= -1; day++)
        {
            for (var t = Day(now, day, 18, 0); t < Day(now, day, 21, 0); t = t.AddSeconds(30))
            {
                var load = 0.12 + 0.1 * Math.Sin(t.ToUnixTimeSeconds() / 600.0);
                samples.Add(new HardwareSnapshot
                {
                    Timestamp = t,
                    IsMock = true,
                    System = new SystemSample { CpuUtilizationPercent = 8 + 20 * load, MemoryUsedBytes = (long)(20e9 + 4e9 * load), MemoryTotalBytes = 64L << 30 },
                    Gpus =
                    [
                        new GpuSample
                        {
                            Index = 0, Name = "MOCK GPU (가상)", UtilizationPercent = 5 + 40 * load, TemperatureC = 40 + 15 * load, PowerW = 30 + 120 * load,
                            PowerLimitW = 360, MemoryUsedBytes = (long)(2e9 + 3e9 * load), MemoryTotalBytes = 16L << 30, GraphicsClockMhz = 300 + 1500 * load,
                            MemoryClockMhz = 810, FanSpeedPercent = 30,
                        },
                    ],
                });
            }
        }

        store.WriteSensorSamples(run, samples);
    }

    public static SystemSnapshot MockSnapshot(DateTimeOffset at, string gpuDriver, string nvidiaVersion) => new()
    {
        CollectedAt = at,
        AppVersion = AppInfo.Version,
        IsMock = true,
        Os = new OsInfo { ProductName = "Windows 10 Pro", DisplayVersion = "25H2", CurrentBuild = "26200", Ubr = 7019, EditionId = "Professional", Architecture = "X64" },
        Bios = new BiosInfo { Vendor = "American Megatrends International, LLC.", Version = "3.25 (가상)", ReleaseDate = "09/01/2026", BoardManufacturer = "VIRTUAL", BoardProduct = "VIRTUAL X870E" },
        Cpu = new CpuInfo { Name = "MOCK CPU (가상)", LogicalProcessors = 16 },
        Memory = new MemoryInfo
        {
            TotalBytes = 64L << 30,
            Modules =
            [
                new MemoryModuleInfo { Slot = "DIMM 1", CapacityBytes = 32L << 30, SpeedMts = 6000, ConfiguredSpeedMts = 6000, Manufacturer = "VIRTUAL", PartNumber = "VIRT-6000" },
                new MemoryModuleInfo { Slot = "DIMM 2", CapacityBytes = 32L << 30, SpeedMts = 6000, ConfiguredSpeedMts = 6000, Manufacturer = "VIRTUAL", PartNumber = "VIRT-6000" },
            ],
        },
        Gpus = [new GpuAdapterInfo { Name = "MOCK GPU (가상)", DriverVersion = gpuDriver, DriverDate = "2026-10-20", Status = "OK" }],
        NvidiaDriverVersion = nvidiaVersion,
        Disks = [new DiskInfo { Model = "VIRTUAL NVMe 2TB", InterfaceType = "SCSI", SizeBytes = 2_000_000_000_000, FirmwareRevision = "1.0", Status = "OK" }],
        Drivers = [new DriverInfo { DeviceName = "MOCK GPU (가상)", DeviceClass = "DISPLAY", DriverVersion = gpuDriver, DriverDate = "2026-10-20", Provider = "NVIDIA", InfName = "oem42.inf" }],
        Hotfixes = [new HotfixInfo { HotFixId = "KB5070001", Description = "Update", InstalledOn = "2026-10-08" }],
    };

    private static DateTimeOffset Day(DateTimeOffset now, int day, int hour, int minute)
        => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(day).AddHours(hour).AddMinutes(minute);

    private static string IsoUtc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z", CultureInfo.InvariantCulture);

    private static RawEvent Ev(string channel, string provider, int id, int level, DateTimeOffset time, string? message, params (string? Name, string Value)[] data)
        => new()
        {
            Channel = channel,
            Provider = provider,
            EventId = id,
            Level = level,
            RecordId = 0,
            TimeCreated = time,
            Computer = "VIRTUAL-PC",
            Message = message,
            Data = data.Select(d => new EventDataItem(d.Name, d.Value)).ToList(),
        };
}
