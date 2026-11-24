using System.Globalization;

namespace WinDiag.Core.Crash;

public sealed record BugCheckInfo(uint Code, string Name, string Description);

/// <summary>
/// Names and short descriptions of common bug check (stop) codes, from Microsoft's public "Bug Check Code Reference".
/// A code describes the kind of stop, not its cause: the UI always shows this as reference information only.
/// </summary>
public static class BugCheckCatalog
{
    private static readonly Dictionary<uint, BugCheckInfo> Known = new BugCheckInfo[]
    {
        new(0x0A, "IRQL_NOT_LESS_OR_EQUAL", "커널 모드에서 너무 높은 IRQL로 페이지 가능 메모리나 잘못된 주소에 접근했습니다."),
        new(0x19, "BAD_POOL_HEADER", "풀 헤더가 손상되었습니다."),
        new(0x1A, "MEMORY_MANAGEMENT", "메모리 관리 오류가 발생했습니다. 매개변수 1이 세부 유형을 나타냅니다."),
        new(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", "커널 모드 프로그램이 처리하지 못한 예외를 일으켰습니다."),
        new(0x24, "NTFS_FILE_SYSTEM", "NTFS 파일 시스템 드라이버에서 문제가 발생했습니다."),
        new(0x3B, "SYSTEM_SERVICE_EXCEPTION", "시스템 서비스 루틴 실행 중 예외가 발생했습니다."),
        new(0x4E, "PFN_LIST_CORRUPT", "메모리 페이지 프레임 번호(PFN) 목록이 손상되었습니다."),
        new(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", "잘못된 시스템 메모리를 참조했습니다."),
        new(0x77, "KERNEL_STACK_INPAGE_ERROR", "커널 스택 페이지를 페이지 파일에서 읽지 못했습니다."),
        new(0x7A, "KERNEL_DATA_INPAGE_ERROR", "커널 데이터 페이지를 페이지 파일에서 읽지 못했습니다."),
        new(0x7B, "INACCESSIBLE_BOOT_DEVICE", "부팅 중 시스템 파티션에 접근하지 못했습니다."),
        new(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "시스템 스레드가 처리하지 못한 예외를 일으켰습니다."),
        new(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", "커널이 처리하지 못한 트랩이 발생했습니다."),
        new(0x9C, "MACHINE_CHECK_EXCEPTION", "치명적인 머신 체크 예외가 발생했습니다."),
        new(0x9F, "DRIVER_POWER_STATE_FAILURE", "드라이버가 일관되지 않거나 잘못된 전원 상태에 있습니다."),
        new(0xA0, "INTERNAL_POWER_ERROR", "전원 정책 관리자에서 치명적인 오류가 발생했습니다."),
        new(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", "드라이버가 읽기 전용 메모리에 쓰려고 했습니다."),
        new(0xC2, "BAD_POOL_CALLER", "잘못된 풀 요청이 있었습니다."),
        new(0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION", "드라이버 검증 도구(Driver Verifier)가 위반을 감지했습니다."),
        new(0xC5, "DRIVER_CORRUPTED_EXPOOL", "잘못된 IRQL에서 시스템 풀에 접근했습니다."),
        new(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "커널 모드 드라이버가 너무 높은 IRQL에서 페이지 가능 메모리에 접근했습니다."),
        new(0xE2, "MANUALLY_INITIATED_CRASH", "사용자가 의도적으로 일으킨 충돌입니다."),
        new(0xEA, "THREAD_STUCK_IN_DEVICE_DRIVER", "장치 드라이버의 스레드가 끝없이 반복 실행되었습니다."),
        new(0xEF, "CRITICAL_PROCESS_DIED", "중요한 시스템 프로세스가 종료되었습니다."),
        new(0xF4, "CRITICAL_OBJECT_TERMINATION", "중요한 프로세스나 스레드가 종료되었습니다."),
        new(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "실행할 수 없는 메모리에서 코드를 실행하려 했습니다."),
        new(0x101, "CLOCK_WATCHDOG_TIMEOUT", "지정된 시간 안에 보조 프로세서에서 예상된 클럭 인터럽트를 받지 못했습니다."),
        new(0x109, "CRITICAL_STRUCTURE_CORRUPTION", "커널의 핵심 코드나 데이터 손상이 감지되었습니다."),
        new(0x10E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "비디오 메모리 관리자에서 복구할 수 없는 상태가 감지되었습니다."),
        new(0x113, "VIDEO_DXGKRNL_FATAL_ERROR", "DirectX 그래픽 커널에서 위반이 감지되었습니다."),
        new(0x116, "VIDEO_TDR_FAILURE", "디스플레이 드라이버 시간 초과 감지·복구(TDR)에 실패했습니다."),
        new(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", "디스플레이 드라이버가 제시간에 응답하지 않았습니다(주로 라이브 덤프)."),
        new(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "비디오 스케줄러가 치명적인 위반을 감지했습니다."),
        new(0x124, "WHEA_UNCORRECTABLE_ERROR", "하드웨어가 수정할 수 없는 오류를 보고했습니다. 매개변수 1은 오류 원본 유형, 매개변수 2는 WHEA 오류 레코드 주소입니다."),
        new(0x133, "DPC_WATCHDOG_VIOLATION", "DPC 워치독이 너무 오래 실행되는 DPC 또는 너무 오래 유지된 높은 IRQL을 감지했습니다."),
        new(0x139, "KERNEL_SECURITY_CHECK_FAILURE", "커널이 중요한 데이터 구조의 손상을 감지했습니다."),
        new(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", "커널 모드 힙 관리자가 힙 손상을 감지했습니다."),
        new(0x141, "VIDEO_ENGINE_TIMEOUT_DETECTED", "디스플레이 엔진 중 하나가 정해진 시간 안에 응답하지 않았습니다(주로 라이브 덤프)."),
        new(0x154, "UNEXPECTED_STORE_EXCEPTION", "저장소 구성 요소에서 예상하지 못한 예외가 발생했습니다."),
    }.ToDictionary(b => b.Code);

    public const string Disclaimer = "BugCheck 코드는 정지 유형을 나타내는 참고 정보이며, 원인 드라이버나 부품을 특정하지 않습니다.";

    public static BugCheckInfo? Find(uint code) => Known.TryGetValue(code, out var info) ? info : null;

    public static string Name(uint code) => Find(code)?.Name ?? "알 수 없는 코드";

    public static string Hex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    public static string Describe(uint code) => $"{Hex(code)} {Name(code)}";

    /// <summary>Parses "292", "0x124", "0x00000124" or "124" (hex when it has letters or a prefix).</summary>
    public static bool TryParseCode(string? text, bool decimalByDefault, out uint code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
        }

        if (decimalByDefault && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec <= uint.MaxValue)
        {
            code = (uint)dec;
            return true;
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }
}

public sealed record ExceptionCodeInfo(uint Code, string Name, string Description);

/// <summary>Common NTSTATUS / exception codes seen in application crash events.</summary>
public static class ExceptionCodes
{
    private static readonly Dictionary<uint, ExceptionCodeInfo> Known = new ExceptionCodeInfo[]
    {
        new(0xC0000005, "STATUS_ACCESS_VIOLATION", "잘못된 메모리 접근(접근 위반)"),
        new(0xC0000006, "STATUS_IN_PAGE_ERROR", "페이지를 읽지 못함(저장장치·네트워크 경로 문제와 함께 나타날 수 있음)"),
        new(0xC0000017, "STATUS_NO_MEMORY", "메모리 부족"),
        new(0xC000001D, "STATUS_ILLEGAL_INSTRUCTION", "잘못된 CPU 명령"),
        new(0xC0000094, "STATUS_INTEGER_DIVIDE_BY_ZERO", "정수 0으로 나누기"),
        new(0xC0000096, "STATUS_PRIVILEGED_INSTRUCTION", "권한이 필요한 명령 실행"),
        new(0xC00000FD, "STATUS_STACK_OVERFLOW", "스택 오버플로"),
        new(0xC000012D, "STATUS_COMMITMENT_LIMIT", "커밋 한도 도달(가상 메모리 부족)"),
        new(0xC0000135, "STATUS_DLL_NOT_FOUND", "필요한 DLL을 찾지 못함"),
        new(0xC0000142, "STATUS_DLL_INIT_FAILED", "DLL 초기화 실패"),
        new(0xC0000374, "STATUS_HEAP_CORRUPTION", "힙 손상 감지"),
        new(0xC0000409, "STATUS_STACK_BUFFER_OVERRUN", "스택 버퍼 오버런 또는 빠른 실패(fail fast)"),
        new(0xC000041D, "STATUS_FATAL_USER_CALLBACK_EXCEPTION", "사용자 콜백 중 처리되지 않은 예외"),
        new(0xC0000420, "STATUS_ASSERTION_FAILURE", "어설션 실패"),
        new(0xC0000602, "STATUS_FAIL_FAST_EXCEPTION", "빠른 실패 예외"),
        new(0x80000003, "STATUS_BREAKPOINT", "중단점 예외"),
        new(0x40000015, "STATUS_FATAL_APP_EXIT", "응용프로그램 치명적 종료"),
        new(0xE0434352, "CLR_EXCEPTION", ".NET(CLR) 예외"),
        new(0xE06D7363, "CPP_EH_EXCEPTION", "C++ 예외"),
    }.ToDictionary(e => e.Code);

    public static ExceptionCodeInfo? Find(uint code) => Known.TryGetValue(code, out var info) ? info : null;

    /// <summary>"c0000005", "0xc0000005" or "-1073741819" to a code.</summary>
    public static bool TryParse(string? text, out uint code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith('-') && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var negative))
        {
            code = unchecked((uint)negative);
            return true;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }

    public static string Describe(uint code)
    {
        var hex = "0x" + code.ToString("x8", CultureInfo.InvariantCulture);
        return Find(code) is { } info ? $"{hex} {info.Name} ({info.Description})" : hex;
    }
}
