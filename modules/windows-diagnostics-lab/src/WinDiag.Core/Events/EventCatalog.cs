using static WinDiag.Core.Events.EventCategories;

namespace WinDiag.Core.Events;

/// <summary>
/// The rules that turn Windows events into diagnostic categories. Each rule names the provider, optionally the event IDs
/// and channels, a meaning and a caution (what the event does not prove). Events without a rule are still collected
/// when they are critical or errors, under "other".
/// </summary>
public sealed class EventCatalog
{
    public const string KernelPower = "Microsoft-Windows-Kernel-Power";
    public const string EventLogProvider = "EventLog";
    public const string WerSystem = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string WheaLogger = "Microsoft-Windows-WHEA-Logger";
    public const string KernelGeneral = "Microsoft-Windows-Kernel-General";
    public const string Scm = "Service Control Manager";

    /// <summary>
    /// Raise when rules or interpreters change. On start the engine re-classifies stored events once when the database
    /// was classified with an older version, so old and new events are grouped the same way.
    /// </summary>
    public const int Version = 2;

    private const string StorageCaution = "케이블·연결, 컨트롤러 드라이버, 장치 펌웨어, 장치 자체 등 원인이 여러 가지입니다. 이 이벤트만으로 장치 고장을 확정할 수 없습니다. 중요한 데이터는 백업하고 제조사 도구로 장치 상태(SMART)를 확인하세요.";
    private const string WheaCaution = "드물게 기록되는 수정된(corrected) 오류는 정상 범위일 수도 있습니다. 반복 빈도와 설정 변경(오버클럭·EXPO·전압 등) 전후 비교가 필요하며, 이 이벤트만으로 특정 부품 불량을 확정할 수 없습니다.";
    private const string GpuCaution = "GPU 하드웨어, 드라이버, 과부하, 오버클럭·언더볼트, 전원 공급, 온도 등 여러 원인에서 발생할 수 있어 이 이벤트만으로 원인을 특정할 수 없습니다.";

    public static EventCatalog Default { get; } = new(BuildRules());

    public EventCatalog(IReadOnlyList<EventRule> rules)
    {
        Rules = rules;
    }

    public IReadOnlyList<EventRule> Rules { get; }

    /// <summary>Providers that are not collected by default (privacy or noise). Settings can change this list.</summary>
    public static IReadOnlyList<string> DefaultExcludedProviders { get; } =
    [
        "Microsoft-Windows-DNS-Client", // contains visited host names
        "Microsoft-Windows-Security-Auditing",
    ];

    /// <summary>The most specific matching rule: provider+ID(+channel) beats the provider default.</summary>
    public EventRule? Find(IEventFields e)
    {
        EventRule? providerDefault = null;
        foreach (var rule in Rules)
        {
            if (!rule.Matches(e))
            {
                continue;
            }

            if (rule.EventIds is not null)
            {
                return rule;
            }

            providerDefault ??= rule;
        }

        return providerDefault;
    }

    private static List<EventRule> BuildRules() =>
    [
        // ---------------------------------------------------------------- unexpected shutdown, crashes
        new()
        {
            Provider = KernelPower, EventIds = [41], Category = UnexpectedShutdown, Severity = DiagSeverity.Critical,
            Title = "정상 종료 없이 재시작됨 (Kernel-Power 41)",
            Meaning = "시스템이 정상 종료 절차 없이 다시 시작되었음을 다음 부팅 때 기록한 이벤트입니다. 시스템 정지(응답 없음), 전원 차단, 전원 버튼 강제 종료, BugCheck(블루스크린) 후 재부팅 등에서 기록될 수 있습니다.",
            Caution = "이 이벤트만으로 파워서플라이(PSU) 고장이나 특정 부품 불량이 입증되지 않습니다. 기록 시각은 문제가 난 시각이 아니라 다음 부팅 시각입니다.",
            Interpret = EventInterpreters.KernelPower41,
        },
        new()
        {
            Provider = EventLogProvider, EventIds = [6008], Category = UnexpectedShutdown, Severity = DiagSeverity.Error,
            Title = "예기치 않은 이전 종료 (EventLog 6008)",
            Meaning = "이전 시스템 종료가 예기치 않게 일어났다고 다음 부팅 때 기록됩니다. 이벤트 데이터에 Windows가 기록한 이전 종료 시각이 들어 있습니다.",
            Caution = "원인 정보는 포함되지 않습니다. 같은 부팅의 Kernel-Power 41, BugCheck 기록과 함께 확인하세요.",
            Interpret = EventInterpreters.EventLog6008,
        },
        new()
        {
            Provider = WerSystem, EventIds = [1001], Category = BugCheck, Severity = DiagSeverity.Critical,
            Title = "BugCheck(블루스크린) 후 재부팅",
            Meaning = "BugCheck(블루스크린)로 시스템이 중단된 뒤 재부팅되었고, Windows가 BugCheck 코드·매개변수·덤프 저장 위치를 기록했습니다.",
            Caution = "BugCheck 코드는 정지 유형을 나타낼 뿐 원인 드라이버나 부품을 특정하지 않습니다. 반복 패턴, 같은 시각의 다른 이벤트, 덤프 분석이 필요합니다.",
            Interpret = EventInterpreters.BugCheck1001,
        },
        new()
        {
            Provider = "volmgr", EventIds = [46], Category = CrashDump, Title = "충돌 덤프 초기화 실패 (volmgr 46)",
            Meaning = "부팅 시 충돌 덤프 기능을 초기화하지 못했습니다. 이후 BugCheck가 나도 덤프가 저장되지 않을 수 있습니다.",
        },
        new()
        {
            Provider = "volmgr", EventIds = [49], Category = CrashDump, Title = "덤프용 페이지 파일 구성 실패 (volmgr 49)",
            Meaning = "충돌 덤프에 쓸 페이지 파일을 구성하지 못했습니다. 덤프가 저장되지 않을 수 있습니다.",
        },
        new()
        {
            Provider = "volmgr", EventIds = [161], Category = CrashDump, Title = "덤프 파일 생성 실패 (volmgr 161)",
            Meaning = "충돌 시 덤프 파일을 만드는 중 오류가 발생했습니다.",
        },
        new()
        {
            Provider = "volmgr", EventIds = [162], Category = CrashDump, Severity = DiagSeverity.Warning, CollectInformational = true,
            Title = "덤프 파일 생성 기록 (volmgr 162)",
            Meaning = "충돌 덤프 파일 생성이 기록되었습니다. 같은 시각의 BugCheck 기록과 함께 확인하세요.",
        },

        // ---------------------------------------------------------------- WHEA hardware errors
        new()
        {
            Provider = WheaLogger, EventIds = [17], Category = HardwarePcie, Title = "수정된 PCIe 하드웨어 오류 (WHEA 17)",
            Meaning = "PCI Express 장치나 링크에서 하드웨어가 스스로 바로잡은 오류가 보고되었습니다 (AER).",
            Caution = WheaCaution, Interpret = EventInterpreters.Whea,
        },
        new()
        {
            Provider = WheaLogger, EventIds = [18], Category = HardwareCpu, Severity = DiagSeverity.Critical,
            Title = "치명적 하드웨어 오류 (WHEA 18)",
            Meaning = "수정할 수 없는 하드웨어 오류(머신 체크)가 보고되었습니다. 보통 시스템 중단 뒤 다음 부팅 때 기록됩니다.",
            Caution = "머신 체크는 프로세서가 보고하지만 원인이 메모리, 메모리 컨트롤러, 전압·클럭 설정 등 다른 곳일 수도 있습니다. MCA 뱅크와 반복 여부를 함께 확인하세요.",
            Interpret = EventInterpreters.Whea,
        },
        new()
        {
            Provider = WheaLogger, EventIds = [19], Category = HardwareCpu, Title = "수정된 프로세서 하드웨어 오류 (WHEA 19)",
            Meaning = "프로세서 머신 체크 구조가 하드웨어 스스로 바로잡은 오류(Corrected Machine Check)를 보고했습니다.",
            Caution = WheaCaution, Interpret = EventInterpreters.Whea,
        },
        new()
        {
            Provider = WheaLogger, EventIds = [47], Category = HardwareMemory, Title = "수정된 메모리 하드웨어 오류 (WHEA 47)",
            Meaning = "메모리 관련 수정된(corrected) 하드웨어 오류가 보고되었습니다.",
            Caution = WheaCaution, Interpret = EventInterpreters.Whea,
        },
        new()
        {
            Provider = WheaLogger, Category = HardwareOther, CollectInformational = true, Title = "WHEA 하드웨어 오류 보고",
            Meaning = "Windows 하드웨어 오류 아키텍처(WHEA)가 하드웨어 오류를 보고했습니다. 구성 요소는 이벤트 데이터에서 판정합니다.",
            Caution = WheaCaution, Interpret = EventInterpreters.Whea,
        },
        new()
        {
            Provider = "Microsoft-Windows-Kernel-Processor-Power", EventIds = [37], Category = HardwareCpu, Severity = DiagSeverity.Warning,
            Title = "펌웨어의 프로세서 속도 제한 (Kernel-Processor-Power 37)",
            Meaning = "시스템 펌웨어(BIOS/UEFI)가 프로세서 속도를 제한하고 있다고 보고했습니다 (전력·온도 관리 등).",
            Caution = "일시적인 전력 관리 동작일 수 있으며 단독으로 고장을 뜻하지 않습니다.",
            SignatureFields = [],
        },
        new()
        {
            Provider = "Microsoft-Windows-MemoryDiagnostics-Results", EventIds = [1101], Category = HardwareMemory,
            Severity = DiagSeverity.Info, CollectInformational = true, Title = "Windows 메모리 진단 결과: 오류 없음",
            Meaning = "Windows 메모리 진단 도구가 메모리를 검사했고 오류를 발견하지 못했다고 기록했습니다.",
            Caution = "한 번의 검사에서 오류가 없어도 특정 부하·온도·설정에서만 나타나는 불안정성은 발견되지 않을 수 있습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-MemoryDiagnostics-Results", EventIds = [1102], Category = HardwareMemory,
            Severity = DiagSeverity.Critical, CollectInformational = true, Title = "Windows 메모리 진단 결과: 하드웨어 오류 감지",
            Meaning = "Windows 메모리 진단 도구가 메모리 검사 중 하드웨어 오류를 감지했다고 기록했습니다.",
            Caution = "메모리 모듈, 슬롯, 메모리 설정(EXPO·전압 등) 중 어디의 문제인지는 추가 검사가 필요합니다.",
        },

        // ---------------------------------------------------------------- GPU
        new()
        {
            Provider = "Display", EventIds = [4101], Category = GpuDriver, Severity = DiagSeverity.Error,
            Title = "디스플레이 드라이버 응답 중지 후 복구 (TDR)",
            Meaning = "GPU가 정해진 시간 안에 응답하지 않아 Windows가 디스플레이 드라이버를 재설정(TDR)했고 복구에 성공했습니다.",
            Caution = GpuCaution, Interpret = EventInterpreters.Display4101,
        },
        new()
        {
            Provider = "nvlddmkm", Category = GpuDriver, Title = "NVIDIA 디스플레이 드라이버(nvlddmkm) 오류 보고",
            Meaning = "NVIDIA 디스플레이 드라이버가 오류를 기록했습니다. 메시지에 Xid나 TDR 관련 정보가 포함될 수 있습니다(드라이버 버전에 따라 다름).",
            Caution = GpuCaution, Interpret = EventInterpreters.MessageFirstLine,
        },
        new()
        {
            Provider = "amdkmdag", Category = GpuDriver, Title = "AMD 디스플레이 드라이버 오류 보고",
            Meaning = "AMD 디스플레이 드라이버가 오류를 기록했습니다.", Caution = GpuCaution, Interpret = EventInterpreters.MessageFirstLine,
        },
        new()
        {
            Provider = "Microsoft-Windows-DxgKrnl", Category = GpuDriver, Title = "DirectX 그래픽 커널 오류 보고",
            Meaning = "DirectX 그래픽 커널(dxgkrnl)이 오류나 경고를 기록했습니다.", Caution = GpuCaution, Interpret = EventInterpreters.MessageFirstLine,
        },

        // ---------------------------------------------------------------- storage
        new() { Provider = "disk", EventIds = [7], Category = Storage, Title = "디스크 불량 블록 보고 (disk 7)", Meaning = "장치에 불량 블록이 있다고 보고되었습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "disk", EventIds = [11], Category = Storage, Title = "디스크 컨트롤러 오류 (disk 11)", Meaning = "드라이버가 장치의 컨트롤러 오류를 감지했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "disk", EventIds = [51], Category = Storage, Title = "페이징 중 디스크 오류 (disk 51)", Meaning = "페이징 작업 중 장치 오류가 감지되었습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "disk", EventIds = [153], Category = Storage, Title = "디스크 IO 재시도 (disk 153)", Meaning = "장치에 대한 IO 작업이 재시도되었습니다 (일시적인 지연·연결 문제에서도 발생 가능).", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "disk", EventIds = [154], Category = Storage, Title = "하드웨어 오류로 IO 실패 (disk 154)", Meaning = "장치 IO 작업이 하드웨어 오류로 실패했다고 보고되었습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "disk", Category = Storage, Title = "디스크 오류 보고", Meaning = "디스크 드라이버가 오류나 경고를 기록했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "stornvme", EventIds = [129], Category = Storage, Title = "NVMe 장치 재설정 (stornvme 129)", Meaning = "저장장치가 응답하지 않아 드라이버가 장치 재설정(Reset to device)을 요청했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "stornvme", Category = Storage, Title = "NVMe 드라이버 오류 보고", Meaning = "NVMe 저장장치 드라이버가 오류나 경고를 기록했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "storahci", EventIds = [129], Category = Storage, Title = "SATA 장치 재설정 (storahci 129)", Meaning = "저장장치가 응답하지 않아 드라이버가 장치 재설정(Reset to device)을 요청했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "storahci", Category = Storage, Title = "AHCI 드라이버 오류 보고", Meaning = "SATA(AHCI) 드라이버가 오류나 경고를 기록했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "iaStorAC", Category = Storage, Title = "Intel RST 드라이버 오류 보고", Meaning = "Intel 저장장치 드라이버가 오류나 경고를 기록했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.Device },
        new() { Provider = "Ntfs", Category = Storage, Title = "NTFS 파일 시스템 오류 보고", Meaning = "NTFS 파일 시스템이 오류나 경고를 기록했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.MessageFirstLine },
        new() { Provider = "Microsoft-Windows-Ntfs", Category = Storage, Title = "NTFS 파일 시스템 오류 보고", Meaning = "NTFS 파일 시스템이 오류나 경고를 기록했습니다.", Caution = StorageCaution, Interpret = EventInterpreters.MessageFirstLine },

        // ---------------------------------------------------------------- services
        new()
        {
            Provider = Scm, EventIds = [7031, 7034], Category = ServiceFailure, Severity = DiagSeverity.Error,
            Title = "서비스가 예기치 않게 종료됨",
            Meaning = "Windows 서비스가 예기치 않게 종료되었습니다 (7031은 복구 동작이 설정된 경우).",
            Caution = "서비스 종료는 그 서비스 자체 문제일 수도, 다른 문제의 결과일 수도 있습니다. 반복 횟수와 같은 시각의 다른 이벤트를 확인하세요.",
            Interpret = EventInterpreters.ServiceControlManager,
        },
        new()
        {
            Provider = Scm, EventIds = [7000, 7001, 7009, 7011, 7022, 7023, 7024], Category = ServiceFailure,
            Title = "서비스 시작 실패·시간 초과·오류 종료",
            Meaning = "Windows 서비스가 시작에 실패했거나, 응답 시간을 넘겼거나, 오류로 종료되었습니다.",
            Interpret = EventInterpreters.ServiceControlManager,
        },
        new()
        {
            Provider = Scm, EventIds = [7026], Category = DriverFailure, Title = "부팅 시 드라이버 로드 실패 (SCM 7026)",
            Meaning = "부팅 또는 시스템 시작 단계의 드라이버가 로드되지 않았습니다.",
            Caution = "사용하지 않는 장치의 드라이버처럼 영향이 없는 경우도 있습니다.", Interpret = EventInterpreters.ServiceControlManager,
        },
        new()
        {
            Provider = Scm, EventIds = [7045], Category = DriverInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "서비스·드라이버 설치됨 (SCM 7045)", Meaning = "시스템에 새 서비스 또는 커널 드라이버가 설치되었습니다.",
            Interpret = EventInterpreters.ServiceControlManager,
        },

        // ---------------------------------------------------------------- applications
        new()
        {
            Provider = "Application Error", EventIds = [1000], Category = AppCrash, Severity = DiagSeverity.Error,
            Title = "응용프로그램 비정상 종료",
            Meaning = "응용프로그램이 처리되지 않은 예외로 종료되었습니다. 오류가 난 프로그램, 오류가 드러난 모듈, 예외 코드가 기록됩니다.",
            Caution = "오류 모듈이 원인 모듈이라는 뜻은 아닙니다 (다른 곳에서 손상된 메모리가 이 모듈에서 드러날 수 있음). 같은 프로그램·모듈·예외 코드의 반복 여부를 확인하세요.",
            Interpret = EventInterpreters.ApplicationError1000,
        },
        new()
        {
            Provider = "Application Hang", EventIds = [1002], Category = AppHang, Severity = DiagSeverity.Error,
            Title = "응용프로그램 응답 없음으로 종료", Meaning = "응용프로그램이 응답하지 않아 종료되었습니다 (사용자가 직접 종료한 경우 포함).",
            Interpret = EventInterpreters.ApplicationHang1002,
        },
        new()
        {
            Provider = ".NET Runtime", EventIds = [1026], Category = AppCrash, Severity = DiagSeverity.Error,
            Title = ".NET 프로그램의 처리되지 않은 예외", Meaning = ".NET 응용프로그램이 처리되지 않은 예외로 종료되었습니다.",
            Interpret = EventInterpreters.DotNet1026,
        },
        new()
        {
            Provider = "Windows Error Reporting", EventIds = [1001], Category = WerReport, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "Windows 오류 보고(WER) 기록",
            Meaning = "Windows 오류 보고가 문제 보고서를 만들었습니다. 이벤트 이름(예: APPCRASH, LiveKernelEvent, BlueScreen)과 매개변수가 기록됩니다.",
            Interpret = EventInterpreters.WindowsErrorReporting1001,
        },

        // ---------------------------------------------------------------- drivers and devices
        new()
        {
            Provider = "Microsoft-Windows-UserPnp", EventIds = [20001, 20003], Category = DriverInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "장치 드라이버 설치 처리 (UserPnp)",
            Meaning = "장치 드라이버 설치 처리가 완료되었습니다. 메시지의 상태 코드가 0x0이 아니면 실패를 뜻할 수 있습니다.",
            Interpret = EventInterpreters.UserPnp,
        },
        new()
        {
            Provider = "Microsoft-Windows-Kernel-PnP", EventIds = [400], Channels = ["Microsoft-Windows-Kernel-PnP/Configuration"],
            Category = DriverInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "장치 구성 (드라이버 적용)", Meaning = "장치가 구성되었습니다. 적용된 드라이버의 이름·버전·날짜·제공자가 기록됩니다.",
            Interpret = EventInterpreters.KernelPnpDevice,
        },
        new()
        {
            Provider = "Microsoft-Windows-Kernel-PnP", EventIds = [411], Category = DriverFailure,
            Title = "장치 시작 문제 (Kernel-PnP 411)", Meaning = "장치를 시작하는 중 문제가 발생했습니다. 문제 코드와 상태가 기록됩니다.",
            Interpret = EventInterpreters.KernelPnpDevice,
        },
        new()
        {
            Provider = "Microsoft-Windows-Kernel-PnP", EventIds = [219], Category = DriverFailure, Severity = DiagSeverity.Warning,
            Title = "장치 드라이버 로드 실패 (Kernel-PnP 219)", Meaning = "장치의 드라이버가 로드되지 않았습니다.",
            Caution = "일부 장치에서는 부팅 때마다 기록되지만 실제 기능에 문제가 없는 경우도 있습니다. 해당 장치에 이상이 있을 때 참고하세요.",
            Interpret = EventInterpreters.KernelPnp219,
        },

        // ---------------------------------------------------------------- updates and software
        new()
        {
            Provider = "Microsoft-Windows-WindowsUpdateClient", EventIds = [19], Category = UpdateInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "Windows 업데이트 설치 성공", Meaning = "Windows 업데이트가 설치되었다고 기록되었습니다.", Interpret = EventInterpreters.WindowsUpdate,
        },
        new()
        {
            Provider = "Microsoft-Windows-WindowsUpdateClient", EventIds = [43], Category = UpdateInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "Windows 업데이트 설치 시작", Meaning = "Windows 업데이트 설치가 시작되었습니다.", Interpret = EventInterpreters.WindowsUpdate,
        },
        new()
        {
            Provider = "Microsoft-Windows-WindowsUpdateClient", EventIds = [20], Category = UpdateFailure, Severity = DiagSeverity.Error,
            Title = "Windows 업데이트 설치 실패", Meaning = "Windows 업데이트 설치가 실패했습니다. 오류 코드가 기록됩니다.", Interpret = EventInterpreters.WindowsUpdate,
        },
        new()
        {
            Provider = "Microsoft-Windows-WindowsUpdateClient", Category = UpdateFailure, Title = "Windows 업데이트 오류 보고",
            Meaning = "Windows 업데이트 클라이언트가 오류나 경고를 기록했습니다.", Interpret = EventInterpreters.WindowsUpdate,
        },
        new()
        {
            Provider = "Microsoft-Windows-Servicing", Category = UpdateInstall, CollectInformational = true, Channels = ["Setup"],
            Title = "Windows 구성 요소 패키지 상태 변경 (Servicing)", Meaning = "업데이트 패키지의 설치 상태가 바뀌었거나 재부팅이 필요하다고 기록되었습니다.",
            Interpret = EventInterpreters.Servicing,
        },
        new()
        {
            Provider = "MsiInstaller", EventIds = [1033, 11707], Category = SoftwareInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "프로그램 설치 (Windows Installer)", Meaning = "Windows Installer가 제품 설치를 기록했습니다.", Interpret = EventInterpreters.Msi,
        },
        new()
        {
            Provider = "MsiInstaller", EventIds = [1034, 11724], Category = SoftwareInstall, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "프로그램 제거 (Windows Installer)", Meaning = "Windows Installer가 제품 제거를 기록했습니다.", Interpret = EventInterpreters.Msi,
        },
        new()
        {
            Provider = "MsiInstaller", EventIds = [11708], Category = SoftwareInstall, Severity = DiagSeverity.Warning, CollectInformational = true,
            Title = "프로그램 설치 실패 (Windows Installer)", Meaning = "Windows Installer 설치 작업이 실패했습니다.", Interpret = EventInterpreters.Msi,
        },

        // ---------------------------------------------------------------- boot, shutdown, power, time
        new()
        {
            Provider = EventLogProvider, EventIds = [6005], Category = Boot, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "부팅 (이벤트 로그 서비스 시작)", Meaning = "부팅 과정에서 이벤트 로그 서비스가 시작되었습니다. 시스템 시작 시점의 지표로 쓰입니다.",
        },
        new()
        {
            Provider = EventLogProvider, EventIds = [6006], Category = Boot, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "정상 종료 (이벤트 로그 서비스 중지)", Meaning = "정상 종료 과정에서 이벤트 로그 서비스가 중지되었습니다.",
        },
        new()
        {
            Provider = EventLogProvider, EventIds = [6009], Category = Boot, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "부팅 시 운영체제 버전 기록", Meaning = "부팅 때 운영체제 버전과 빌드가 기록되었습니다.", Interpret = EventInterpreters.OsVersion6009,
        },
        new()
        {
            Provider = KernelGeneral, EventIds = [12], Category = Boot, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "운영체제 시작", Meaning = "운영체제가 시작되었습니다.", Interpret = EventInterpreters.KernelGeneralStartStop,
        },
        new()
        {
            Provider = KernelGeneral, EventIds = [13], Category = Boot, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "운영체제 종료", Meaning = "운영체제가 종료되었습니다.", Interpret = EventInterpreters.KernelGeneralStartStop,
        },
        new()
        {
            Provider = KernelGeneral, EventIds = [1], Category = TimeChange, CollectInformational = true,
            Title = "시스템 시각 변경", Meaning = "시스템 시각이 변경되었습니다 (시간 동기화, 사용자 변경, 하드웨어 시계 동기화 등).",
            Caution = "큰 시각 변경이 있으면 이 시점 전후 이벤트의 순서·간격 비교가 왜곡될 수 있습니다.",
            Interpret = EventInterpreters.KernelGeneral1,
        },
        new()
        {
            Provider = "User32", EventIds = [1074], Category = Boot, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "종료·재시작 요청", Meaning = "프로그램 또는 사용자가 시스템 종료나 재시작을 요청했습니다 (정상 절차).",
            Interpret = EventInterpreters.User32Shutdown,
        },
        new()
        {
            Provider = KernelPower, EventIds = [42], Category = Power, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "절전 진입", Meaning = "시스템이 절전 상태로 들어갔습니다.",
        },
        new()
        {
            Provider = KernelPower, EventIds = [107], Category = Power, Severity = DiagSeverity.Info, CollectInformational = true,
            Title = "절전에서 재개", Meaning = "시스템이 절전 상태에서 재개되었습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-Eventlog", EventIds = [104], Category = LogCleared, Severity = DiagSeverity.Warning, CollectInformational = true,
            Title = "이벤트 로그가 지워짐", Meaning = "이벤트 로그가 지워졌습니다. 그 이전의 기록은 Windows에 남아 있지 않습니다.",
            Interpret = EventInterpreters.LogCleared,
        },

        // ---------------------------------------------------------------- resources and known noise
        new()
        {
            Provider = "Microsoft-Windows-Resource-Exhaustion-Detector", EventIds = [2004], Category = Resource, Severity = DiagSeverity.Warning,
            Title = "가상 메모리 부족 진단", Meaning = "Windows가 가상 메모리(커밋) 부족 상태를 진단했습니다. 메시지에 메모리를 많이 사용한 프로그램이 기록될 수 있습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-DistributedCOM", EventIds = [10016], Category = Other, Severity = DiagSeverity.Info,
            Title = "DCOM 권한 경고 (10016)", Meaning = "구성 요소가 필요한 권한 없이 DCOM 서버에 접근하려 했다고 기록되었습니다.",
            Caution = "Microsoft는 Windows 구성 요소가 기록하는 이 이벤트가 일반적으로 기능에 영향을 주지 않으며 무시해도 된다고 안내합니다.",
        },
        // ---------------------------------------------------------------- frequent Windows errors that are usually not faults
        // Added after the first run on the owner's PC (2026-11-05): these made up most of the uncatalogued errors.
        new()
        {
            Provider = "Schannel", EventIds = [36874, 36887, 36888], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "TLS 연결 실패 (Schannel)", Interpret = EventInterpreters.Schannel,
            Meaning = "보안 연결(TLS) 협상이 실패했습니다. 36874는 상대가 지원하는 암호화 방식 중 이 PC가 허용하는 것이 없을 때, 36887·36888은 협상 중 치명적 경고를 받거나 보냈을 때 기록됩니다. 연결을 처리한 프로세스가 함께 기록됩니다.",
            Caution = "대부분 오래된 프로그램이나 다른 기기가 낮은 TLS 버전으로 접속을 시도한 경우이며 시스템 안정성과는 관계가 없습니다. 처리한 프로세스가 원격 데스크톱(TermService)이면 원격 데스크톱이 켜져 있다는 뜻이므로, 쓰지 않는다면 끄고 공유기에서 외부에 열려 있지 않은지 확인하세요. 이 기록만으로 공격 여부나 접속한 곳을 알 수 없습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-DistributedCOM", EventIds = [10010], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "DCOM 서버 등록 시간 초과 (10010)", Interpret = EventInterpreters.Dcom10010,
            Meaning = "COM 서버(주로 스토어 앱이나 백그라운드 구성 요소)가 정해진 시간 안에 DCOM에 등록하지 않았습니다.",
            Caution = "앱이 백그라운드에서 늦게 시작되거나 종료되는 중에 흔히 기록되며, 그 앱이 실제로 동작하지 않을 때만 의미가 있습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-DistributedCOM", EventIds = [10005], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "DCOM이 서비스를 시작하지 못함 (10005)", Interpret = EventInterpreters.Dcom10005,
            Meaning = "COM 서버를 실행하기 위해 서비스를 시작하려 했지만 오류가 반환되었습니다. 서비스 이름과 오류 코드가 기록됩니다.",
            Caution = "서비스가 사용 안 함으로 설정되었거나 앱 업데이트 중일 때도 기록됩니다. 해당 서비스를 쓰는 기능이 실제로 안 될 때만 확인하세요.",
        },
        new()
        {
            Provider = "VSS", EventIds = [13, 8193], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "볼륨 섀도 복사본(VSS) 오류", Interpret = EventInterpreters.Vss,
            Meaning = "볼륨 섀도 복사본 서비스(복원 지점·백업에 사용)가 구성 요소를 시작하거나 호출하지 못했습니다. 결과 코드가 기록됩니다.",
            Caution = "결과 코드가 0x8007045b(시스템을 종료하고 있음)이면 PC를 끄는 중에 남은 기록입니다. 그 밖의 코드가 PC 사용 중에 반복되면 복원 지점 만들기나 백업이 되는지 확인하세요. 이 기록만으로 디스크 이상을 뜻하지 않습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-Search", EventIds = [3602, 7040, 7042], Category = ServiceFailure, CollectWarnings = false,
            Title = "Windows Search 색인 서비스 오류", Interpret = EventInterpreters.WindowsSearch,
            Meaning = "Windows Search 색인기가 복구 단계에서 실패해 서비스를 멈췄습니다. 사유와 결과 코드가 기록됩니다.",
            Caution = "파일·시작 메뉴 검색과 색인에만 영향이 있고 시스템 안정성과는 별개입니다. 반복되면 서비스 재시작이나 색인 다시 만들기(설정 > 개인 정보 및 보안 > Windows 검색)가 안내됩니다. 이동식 드라이브 연결·분리, 색인 대상 변경과 함께 나타나기도 하지만 이 기록만으로 원인을 알 수 없습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-Kernel-PnP", EventIds = [225], Category = Other, Severity = DiagSeverity.Warning,
            Title = "프로그램이 장치 제거·꺼내기를 막음 (Kernel-PnP 225)", Interpret = EventInterpreters.KernelPnp225,
            Meaning = "장치(주로 USB 저장장치)를 제거하거나 꺼내려 할 때 어떤 프로그램이 그 장치를 사용 중이어서 막았습니다. 막은 프로그램과 장치가 기록됩니다.",
            Caution = "장치 고장이 아닙니다. 막은 프로그램이 장치의 파일을 열어 두었던 것입니다.",
        },
        new()
        {
            Provider = ".NET Runtime", EventIds = [1022], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = ".NET 프로파일러 연결 초기화 실패 (1022)",
            Meaning = ".NET 프로그램이 시작될 때 진단용 프로파일러 연결 기능을 초기화하지 못했습니다. 프로그램 자체는 계속 실행될 수 있습니다.",
            Caution = "보안·모니터링 프로그램이 .NET 프로그램에 연결하려 할 때 흔히 기록되며, 대개 동작에 영향이 없습니다.",
            Interpret = EventInterpreters.MessageFirstLine,
        },
        new()
        {
            Provider = "Tcpip", EventIds = [4207], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "TCP/IP 인터페이스 바인딩 실패 (4207)", Interpret = EventInterpreters.MessageFirstLine,
            Meaning = "네트워크 인터페이스가 TCP/IP 공급자에 연결되지 못했다고 기록되었습니다.",
            Caution = "부팅·절전 해제 중 네트워크 어댑터가 준비되기 전에 흔히 기록됩니다. 네트워크가 실제로 끊길 때 같은 시각에 반복되는지만 확인하세요.",
        },
        new()
        {
            Provider = "NetBT", EventIds = [4307], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "NetBIOS over TCP/IP 초기화 실패 (4307)", Interpret = EventInterpreters.MessageFirstLine,
            Meaning = "NetBIOS over TCP/IP가 초기 주소를 열지 못해 초기화되지 않았습니다.",
            Caution = "부팅 중 네트워크가 준비되기 전에 흔히 기록됩니다. 오래된 방식의 파일 공유 이름 찾기에만 쓰이며 일반 인터넷 연결과는 대개 관계가 없습니다.",
        },
        new()
        {
            Provider = "Microsoft-Windows-DeviceAssociationService", EventIds = [3503], Category = Other, Severity = DiagSeverity.Warning, CollectWarnings = false,
            Title = "장치 연결 서비스 검색 오류 (3503)", Interpret = EventInterpreters.MessageFirstLine,
            Meaning = "장치 연결 서비스가 네트워크·무선 장치를 찾는 중 오류를 감지했습니다.",
            Caution = "주변 장치 검색 과정의 기록으로 대개 동작에 영향이 없습니다. 특정 블루투스·네트워크 장치 연결이 안 될 때만 같은 시각과 비교하세요.",
        },
    ];
}
