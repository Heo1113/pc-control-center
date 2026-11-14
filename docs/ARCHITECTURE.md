# 구조

PC Control Center는 네 프로그램(A·B·C·E)의 **검증된 코드를 그대로 실행**하고, 그 위에 공통 셸·공통 센서·통합 기록·데이터 이전·Operator 격리를 더한 앱입니다.
화면·엔진·저장소를 새로 만든 것이 아니라, 각 프로그램의 엔진과 뷰모델·화면(XAML)을 한 프로세스(Operator 실행부만 별도 프로세스)에서 모듈로 돌립니다.

## 실행 구성

```
PcControlCenter.exe (WPF, 일반 사용자 권한)
├─ Shell: 메뉴 8개, 공통 상태 표시줄, 통합 대시보드, 공통 설정, 통합 기록, 데이터·이전
├─ GpuMemoryMonitor ─ Windows GPU 성능 카운터(PDH)·DXGI로 프로세스별·어댑터별 GPU 메모리 (별도 스레드, NVML 미사용, 하드웨어 › GPU 메모리)
├─ SharedHardwareHub ─ A의 HardwareMonitor(NVML·Windows CPU/RAM) 하나만 실제로 읽음
│    ├─▶ 로컬 AI 모듈 (A MonitoringService: Strata 조회는 A 자신의 루프, lapc.db)
│    ├─▶ GPU 경제성 모듈 (B ProfitEngine: 전력 적산·세션·비용, gpc.db)
│    └─▶ 시스템 진단 모듈 (C DiagnosticsEngine: 이벤트 로그는 C 자신의 수집기, wdl.db)
├─ AI Operator 모듈 (E OperatorContext: 계획·정책·승인·실행기·기록, wao.db)
│    └─ RemoteDesktop ══ stdin/stdout (JSON 한 줄씩, 닫힌 요청 목록) ══╗
└─ ModuleSupervisor: 모듈별 독립 시작·종료·재시작, 장애는 그 모듈의 '실패' 상태로만 끝남   ║
                                                                                         ║
PcControlCenter.OperatorHost.exe (창 없음, 일반 사용자, Windows 작업 개체 안) ◀══════════╝
└─ E의 FlaUiDesktop (UIA3·Win32 입력·화면 캡처·프로그램 실행) + HostGuard(독립 거부 검사)
```

## 프로젝트

| 프로젝트 | 내용 |
|---|---|
| `modules/<원본 저장소>/` | A·B·C·E 소스(고정 커밋). 바뀐 파일은 [REUSE.md](REUSE.md) |
| `src/PcControlCenter.Hardware` | `SharedHardwareHub`: 센서 한 번 읽기, 모듈별 소비 뷰(`HardwareMonitor.Shared`), 원래 측정 시각 유지, 상태 |
| `src/PcControlCenter.GpuMemory` | `GpuMemoryMonitor`: 프로세스별·어댑터별 GPU 메모리(PDH `GPU Process Memory`·`GPU Adapter Memory`, DXGI 이름, 프로세스 시작 시각, 지정 포트의 TCP 소유자), `ServerProcessLinker`(Strata 포트 → 엔진 프로세스), MOCK 공급자 |
| `src/PcControlCenter.Core` | 데이터 폴더(`ShellPaths`), 기존 프로그램 폴더 보호, 단일 실행 잠금, 공통 설정, 모듈 감독 |
| `src/PcControlCenter.Modules` | A·B·C·E 모듈 어댑터, 센서 간격 조정, 대시보드 요약, `ShellRuntime`(조립) |
| `src/PcControlCenter.Operator` | Operator 격리: 프로토콜, 서버(`DesktopServer`), 독립 검사(`HostGuard`), 클라이언트(`RemoteDesktop`), 프로세스 감독 |
| `src/PcControlCenter.OperatorHost` | `PcControlCenter.OperatorHost.exe` |
| `src/PcControlCenter.History` | 통합 기록: 네 DB를 읽기 전용으로 같은 시간축에 |
| `src/PcControlCenter.Migration` | 기존 프로그램 데이터 이전(검사·리허설·적용) |
| `src/PcControlCenter.Presentation` | 셸 뷰모델(WPF 비의존, Linux에서 테스트) |
| `src/PcControlCenter.App` | `PcControlCenter.exe` (WPF) |
| `tests/PcControlCenter.Tests` | 통합 테스트 / `tests/PcControlCenter.MockOperatorHost` 테스트 전용 호스트 |

## 공통 센서

- A·B·C에 있던 A의 센서 코드 사본은 **A의 단일 사본**으로 합쳤습니다(B·C는 A의 프로젝트를 참조). vendor 사본은 가져오지 않았습니다.
- `SharedHardwareHub`가 A의 `HardwareMonitor`를 **전용 스레드 하나**에서 읽습니다(스레드 풀이 아님: 모듈 기록기가 다음 측정을 기다리며 풀 스레드를 붙잡고 있어도 측정 시각이 밀리지 않게). 모듈은 `HardwareMonitor.Shared(hub)` 뷰를 받아 **자기 간격·자기 공백 규칙·자기 DB**로 기록합니다.
  뷰의 `Sample()`은 허브 측정값을 **원래 시각과 문제(issue) 그대로**, 같은 값을 두 번 주지 않고 순서대로 넘깁니다. 새 측정이 제때 오지 않으면 값 없는 자리표시(이유 포함)를 줍니다.
- 허브 간격 = 실행 중 모듈이 요청한 가장 짧은 간격(기본 A 1초·B 1초·C 5초 → 1초), 공통 설정의 최대 간격 이하.
- B의 전력 적산은 허브의 **원시 측정값**을 B 자신의 기록기로 받습니다. A의 화면용 버퍼나 통합 기록의 표시용 묶음은 비용 계산에 쓰이지 않습니다.
- Strata 조회(A)와 이벤트 로그 수집(C)은 각 모듈의 별도 루프 그대로입니다. 센서 장애·Strata 종료·이벤트 로그 접근 제한은 서로 영향을 주지 않습니다.
- 검증: 통합 테스트에서 A·B·C가 함께 돌 때 장치 읽기 횟수 = 허브 측정 횟수, A의 프레임·B의 `gpu_samples`·C의 `sensor_samples`의 모든 시각이 허브 측정 시각.

## GPU 메모리 (하드웨어 › GPU 메모리)

- `GpuMemoryMonitor`는 공통 센서와 **별도의 전용 스레드**에서 Windows GPU 성능 카운터를 읽습니다. NVML은 열지 않습니다(공통 센서 소유). WDDM에서 NVML은 프로세스별 메모리를 주지 않으므로 프로세스별 값의 출처는 Windows 카운터뿐입니다.
- 화면을 보는 동안 2초, 그 밖에는 10초 간격으로 읽습니다. 값은 메모리에 30분만 두고 DB에는 쓰지 않습니다(기존 스키마 변경 없음).
- 공급자 예외·카운터 없음은 이 화면의 N/A와 이유로만 끝나고, 공통 센서·모듈·대시보드에는 영향이 없습니다. 공통 설정에서 끌 수 있습니다.
- 값마다 출처를 붙입니다: 측정(Windows), 계산(합·차), 런타임 보고(Strata). 가중치·KV 캐시·작업 버퍼로 나누지 않습니다. 근거와 한계: [GPU_MEMORY.md](GPU_MEMORY.md).

## 장애 격리

- `ShellModuleBase`: 모듈 시작은 병렬·독립, 예외나 시간 초과(90초)는 그 모듈의 '실패'로 끝나고 다른 모듈은 계속 동작합니다. 실패한 모듈 화면에는 이유와 [모듈 다시 시작].
- B·C의 복원(백업에서 되돌리기)은 그 모듈만 멈췄다 다시 시작합니다(원래 프로그램은 앱 전체를 재시작했음).
- 각 모듈은 원래처럼 자기 데이터 폴더 잠금·자기 로그 파일을 씁니다.

## AI Operator 격리

- 실제 입력이 일어나는 부분 — E의 `IDesktopAutomation` 구현(FlaUI UIA3, Win32 SendInput, 화면 캡처, 프로그램 실행) — 만 `PcControlCenter.OperatorHost.exe`에서 실행합니다.
- 계획 검증·안전 정책(`PolicyEngine`)·승인·실행기(`TaskExecutor`)·기록(`TaskJournal`)은 **E의 코드 그대로** 셸 프로세스에서 `RemoteDesktop` 위에서 동작합니다.
  E의 화면(작업 실행·창 검사기·워크플로·기록·모델·안전 설정)과 승인 창도 그대로입니다.
- 통신: 호스트 프로세스의 표준 입출력(부모만 접근, 이름 있는 파이프·포트 없음). 요청은 E의 인터페이스 멤버와 1:1인 닫힌 목록 + 취소·긴급 중단·재개·종료. 명령·스크립트 실행 경로 없음.
- `HostGuard`(호스트 자체 검사, 정책보다 더 거부만 가능): PC Control Center 자신의 프로세스, E의 내장 차단 목록, 관리자 권한/보호 창, 확인할 수 없는 창(닫혔거나 창이 아닌 핸들), 관리자 권한을 쓸 수 있는 호스트(관리자 그룹 활성 또는 보통보다 높은 무결성 — E가 창에 쓰는 것과 같은 무결성 기준), 긴급 중단 잠금.
- E의 내장 차단 목록에 `PcControlCenter`, `PcControlCenter.OperatorHost`를 추가했습니다(이제 승인 버튼이 이 창에 있으므로).
- 권한: 호스트는 항상 일반 사용자 권한입니다. PC Control Center가 관리자 권한으로 실행된 경우("관리자 권한으로 실행", 모든 프로세스가 관리자 권한인 CI 러너 등)에는
  셸 자신의 토큰에서 Windows Safer "일반 사용자" 수준 토큰(보통 무결성)을 만들어 호스트를 시작합니다(`runas /trustlevel:0x20000`과 같은 방식).
  이 토큰을 만들 수 없으면 호스트는 관리자 권한으로 남고, 그 상태에서는 화면을 전혀 조작하지 않습니다(안전한 실패).
- Windows 작업 개체: 셸이 어떻게 끝나든 호스트도 종료, 호스트가 실행한 프로그램(메모장 등)은 작업 개체에서 빠짐, Windows 종료·시스템 매개변수·디스플레이 설정·데스크톱 전환 금지, 메모리 상한 2 GB.
- 긴급 중단(상태 표시줄 버튼, Ctrl+Alt+F12, Operator 화면): ① E의 `EmergencyStop`(실행 중 작업 중단, 열린 승인은 거부) ② 호스트 잠금·진행 중 요청 취소 ③ 입력이 진행 중이거나 1초 안에 확인이 없으면 호스트 프로세스 종료. 잠금은 호스트가 재시작돼도 유지되며 [재개]를 눌러야 풀립니다.
- 호스트가 죽으면 자동 재시작(기본 3회, 공통 설정). 응답 없이 끝난 동작은 '실행 여부 불확실'로 기록되어 다시 시도되지 않습니다.
- 대안으로 E의 `OperatorContext` 전체를 호스트로 옮기는 방식도 검토했으나, E의 뷰모델 8개를 원격 호출용으로 다시 써야 해서(검증된 코드 재작성) 택하지 않았습니다.

## 데이터

- 데이터 폴더: `portable.txt`가 있으면 실행 파일 옆 `data\`, 아니면 `%LOCALAPPDATA%\PcControlCenter`. 기존 프로그램 폴더(`LocalAiPerformanceCenter`, `GpuProfitCenter`, `WindowsDiagnosticsLab`, `WindowsAiOperator`)와 겹치는 위치는 거부합니다.
- `modules\local-ai\lapc.db`, `modules\gpu-economics\gpc.db`, `modules\diagnostics\wdl.db`, `modules\operator\wao.db` — 원래와 같은 형식, **합치지 않음**.
- 통합 기록은 네 DB를 읽기 전용 연결로 읽습니다(WAL이라 기록 중에도 가능).
- 데이터 이전: [MIGRATION.md](MIGRATION.md).

## 화면 통합
- 1024×768이나 화면 절반으로 붙인 창처럼 폭이 1200(배율 반영) 미만이면 왼쪽 메뉴는 아이콘만 표시합니다(이름은 툴팁·화면 읽기 프로그램용 이름으로 유지). 모듈 화면이 원래 설계된 폭에 가깝게 쓰입니다.
- 대시보드는 하단 상태 표시줄과 겹치던 모듈 상태 칩을 없앴고, 페이지가 하나인 섹션은 탭 줄을 숨기며, "출처" 표시는 제목 줄에 둡니다. 상태 표시줄의 데이터 경로는 남는 폭만큼만 표시하고(말줄임표, 전체는 툴팁) 긴급 중단 버튼은 항상 보입니다.

- 메뉴: 대시보드 · 하드웨어 · 로컬 AI · GPU 경제성 · 시스템 진단 · AI Operator · 통합 기록 · 설정. 하드웨어 메뉴에 셸 화면 "GPU 메모리" 탭이 "공통 센서" 다음에 있습니다(새 메뉴 없음).
- A·B·C의 대시보드 3개·설정 3개·센서 화면은 하나의 대시보드·하드웨어 메뉴·설정 메뉴로 모았고, 각 모듈 고유 화면(세션 기록, 수익성, 이벤트 탐색 등)은 기능 그대로 탭으로 남겼습니다.
- 모듈 화면은 `ModulePageHost`가 모듈별 컨테이너에 그 모듈의 `ModuleResources.xaml`(원래 테마·변환기·뷰 템플릿)을 적용해 표시합니다. 원래 XAML은 고치지 않았습니다.
  - WPF는 `{StaticResource}`를 화면 XAML을 **불러오는 순간**(생성자 안, 컨테이너에 들어가기 전) 찾으므로 그때는 화면 자신과 `Application.Resources`만 보입니다.
    그래서 시작할 때 `ModuleResourceScope`가 한 모듈만 정의하고 셸에는 없는 키(B의 `OkBrush`·`Field`·`StatusLine`, E의 `Banner`)를 앱 수준에 복사합니다.
  - 여러 모듈이 같은 이름으로 다르게 정의한 키(A·C의 `StatusBrush`)는 복사하지 않고, 셸의 상태 줄은 `LocalAiStatusBrush`·`DiagnosticsStatusBrush`처럼 명시적 이름을 씁니다.
  - 앱 수준의 공용 변환기(`BoolToVisibility`, `NotEmptyToVisibility`)는 네 모듈이 실제로 바인딩하는 값(참, 개수, 문자열, 개체)을 모두 원래 뜻대로 처리합니다.
    A의 테마와 다른 모듈 테마가 같은 키로 조금 다르게 정의한 스타일(`BigValue` 등)은 셸(A) 쪽 값이 쓰입니다(검토 목록: `scripts/xaml-resource-check.py`).
  - `scripts/xaml-resource-check.py`(CI)가 위 규칙으로 모든 화면의 StaticResource를 검사합니다.
- 각 모듈의 원래 상태 표시줄 내용(A의 수동 기록 버튼, B의 센서/세션, C의 수집 상태·알림, E의 상태·긴급 중단)은 그 모듈 화면 위 상태 줄로 옮겼습니다.
- 4K·175%: PerMonitorV2 DPI 인식(원래 프로그램과 같음), 공통 설정의 화면 배율로 추가 확대.
