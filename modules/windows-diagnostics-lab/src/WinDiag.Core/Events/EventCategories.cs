namespace WinDiag.Core.Events;

/// <summary>Which timeline lane an event category belongs to.</summary>
public enum TimelineKind
{
    SystemChange,
    Error,
    ProgramCrash,
    SystemCrash,
    Boot,
    Hardware,
    UserRecord,
    Info,
}

public sealed record CategoryInfo(string Key, string Name, TimelineKind Kind, string Description);

public static class EventCategories
{
    public const string HardwareCpu = "hardware.cpu";
    public const string HardwareMemory = "hardware.memory";
    public const string HardwarePcie = "hardware.pcie";
    public const string HardwareOther = "hardware.other";
    public const string GpuDriver = "gpu.driver";
    public const string Storage = "storage";
    public const string UnexpectedShutdown = "power.unexpected";
    public const string BugCheck = "crash.bugcheck";
    public const string CrashDump = "crash.dump";
    public const string ServiceFailure = "service.failure";
    public const string AppCrash = "app.crash";
    public const string AppHang = "app.hang";
    public const string WerReport = "wer.report";
    public const string DriverInstall = "driver.install";
    public const string DriverFailure = "driver.failure";
    public const string UpdateInstall = "update.install";
    public const string UpdateFailure = "update.failure";
    public const string SoftwareInstall = "software.install";
    public const string Boot = "system.boot";
    public const string Power = "system.power";
    public const string TimeChange = "system.time";
    public const string Resource = "system.resource";
    public const string LogCleared = "log.cleared";
    public const string Other = "other";

    public static IReadOnlyList<CategoryInfo> All { get; } =
    [
        new(HardwareCpu, "CPU·프로세서 오류", TimelineKind.Error, "WHEA 프로세서(머신 체크) 오류, 펌웨어의 프로세서 속도 제한 등"),
        new(HardwareMemory, "메모리 오류", TimelineKind.Error, "WHEA 메모리 오류, Windows 메모리 진단 결과"),
        new(HardwarePcie, "PCIe 오류", TimelineKind.Error, "WHEA PCI Express(AER) 오류"),
        new(HardwareOther, "기타 하드웨어 오류(WHEA)", TimelineKind.Error, "구성 요소를 확인할 수 없는 WHEA 하드웨어 오류"),
        new(GpuDriver, "GPU 드라이버 오류·재설정", TimelineKind.Error, "디스플레이 드라이버 응답 중지·복구(TDR), GPU 드라이버 오류, GPU 관련 라이브 커널 이벤트"),
        new(Storage, "디스크·스토리지 오류", TimelineKind.Error, "디스크·NVMe·AHCI·파일 시스템 오류와 장치 재설정"),
        new(UnexpectedShutdown, "비정상 종료", TimelineKind.SystemCrash, "Kernel-Power 41, EventLog 6008 (정상 종료 절차 없이 다시 시작됨)"),
        new(BugCheck, "BSOD·BugCheck", TimelineKind.SystemCrash, "BugCheck(블루스크린) 후 재부팅 기록"),
        new(CrashDump, "충돌 덤프 기록", TimelineKind.SystemCrash, "충돌 덤프 생성·초기화 성공/실패"),
        new(ServiceFailure, "서비스 충돌·실패", TimelineKind.Error, "Windows 서비스의 예기치 않은 종료, 시작 실패, 시간 초과"),
        new(AppCrash, "응용프로그램 비정상 종료", TimelineKind.ProgramCrash, "Application Error 1000, .NET 처리되지 않은 예외"),
        new(AppHang, "응용프로그램 응답 없음", TimelineKind.ProgramCrash, "Application Hang 1002"),
        new(WerReport, "Windows 오류 보고(WER)", TimelineKind.ProgramCrash, "Windows Error Reporting 보고서 기록"),
        new(DriverInstall, "드라이버 설치·변경", TimelineKind.SystemChange, "장치 드라이버 설치·구성, 서비스·커널 드라이버 설치"),
        new(DriverFailure, "드라이버·장치 문제", TimelineKind.Error, "드라이버 로드 실패, 장치 시작 문제"),
        new(UpdateInstall, "Windows 업데이트", TimelineKind.SystemChange, "업데이트 설치 시작·성공, 서비스 스택 패키지 상태 변경"),
        new(UpdateFailure, "Windows 업데이트 실패", TimelineKind.Error, "업데이트 설치 실패"),
        new(SoftwareInstall, "프로그램 설치·제거", TimelineKind.SystemChange, "Windows Installer 설치·제거 기록"),
        new(Boot, "시작·종료", TimelineKind.Boot, "부팅, 정상 종료, 종료·재시작 요청"),
        new(Power, "절전·재개", TimelineKind.Boot, "절전 진입과 재개"),
        new(TimeChange, "시스템 시각 변경", TimelineKind.SystemChange, "시스템 시각이 바뀜 (전후 이벤트의 시간 비교에 주의)"),
        new(Resource, "자원 부족", TimelineKind.Error, "가상 메모리 부족 등"),
        new(LogCleared, "이벤트 로그 지움", TimelineKind.Info, "이벤트 로그가 지워짐 (이전 기록 없음)"),
        new(Other, "기타 오류·경고", TimelineKind.Error, "카탈로그에 없는 공급자의 위험·오류(설정에 따라 경고 포함)"),
    ];

    private static readonly Dictionary<string, CategoryInfo> ByKey = All.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static CategoryInfo Get(string key)
        => ByKey.TryGetValue(key, out var info) ? info : new CategoryInfo(key, key, TimelineKind.Info, "");

    public static string Name(string key) => Get(key).Name;

    public static bool IsHardware(string key) => key.StartsWith("hardware.", StringComparison.Ordinal);

    public static bool IsSystemCrash(string key) => Get(key).Kind == TimelineKind.SystemCrash;

    public static bool IsChange(string key) => Get(key).Kind == TimelineKind.SystemChange;

    public static string KindName(TimelineKind kind) => kind switch
    {
        TimelineKind.SystemChange => "시스템 변경",
        TimelineKind.Error => "오류",
        TimelineKind.ProgramCrash => "프로그램 충돌",
        TimelineKind.SystemCrash => "시스템 충돌·비정상 종료",
        TimelineKind.Boot => "시작·종료·전원",
        TimelineKind.Hardware => "하드웨어 관측",
        TimelineKind.UserRecord => "사용자 기록",
        TimelineKind.Info => "기타",
        _ => kind.ToString(),
    };
}
