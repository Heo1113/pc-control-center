# 프로젝트 A·C 재사용 기록

GPU Profit & Compute Center(프로젝트 B)는 센서, 로그, SQLite 보조 코드, 통계, CSV, 차트를 새로 만들지 않고 프로젝트 A의 코드를 가져와 씁니다.
저장소 열기·복구·백업, 샘플링 루프, WPF 구성, 스모크 테스트는 프로젝트 C의 코드를 출발점으로 삼았습니다.

## 원칙과 지킨 방법

| 원칙 | 지킨 방법 |
|---|---|
| A·C는 읽기 전용 | 두 저장소를 읽기만 했습니다. 커밋·푸시·이슈·PR을 만들지 않았습니다. |
| 필요한 소스만 선택 | A에서 18개 파일만 가져왔습니다. A의 Strata 연동, 세션 기록기, 저장소 전체, 화면은 가져오지 않았습니다. |
| 센서 코드를 다시 만들지 않음 | NVML GPU 센서, Windows CPU·RAM 센서, mock 센서, `HardwareMonitor`를 A의 원본 그대로 컴파일합니다. |
| B는 독립 실행·업데이트 | A·C의 코드는 B의 EXE에 컴파일되어 들어갑니다. A·C 프로그램, DLL, 설정, 데이터베이스를 찾거나 읽지 않습니다. B는 자기 데이터 폴더(`GpuProfitCenter`)와 `gpc.db`만 씁니다. |
| 원본 커밋과 수정 이력 보존 | 매니페스트(커밋, 파일별 SHA-256, 용도), 패치 파일, 각 B 파일 머리 주석의 출처 표기, 이 문서. |
| C의 결함은 필요한 부분만 | C 코드는 복사본을 B에 맞게 고쳤고, C 저장소에는 아무것도 반영하지 않았습니다. |

## 출처

| | 저장소 | 커밋 |
|---|---|---|
| A | https://github.com/Heo1113/local-ai-performance-center | `5b6aae12c28efbdbebe9a7df280c02f5494b037e` (main, 2026-11-02, PR #1 병합 커밋) |
| C | https://github.com/Heo1113/windows-diagnostics-lab | `06cc9e5da2dda126968e1af8f8edb9d2ff5ab691` (PR #1 브랜치 `claude/ecstatic-newton-cfosml`; main은 초기 커밋만 있음) |

기계가 읽는 기록: [`vendor/local-ai-performance-center/manifest.json`](../vendor/local-ai-performance-center/manifest.json)

## A에서 그대로 가져온 파일 (vendor)

경로는 `vendor/local-ai-performance-center/` 기준이며 A 저장소의 경로와 같습니다. B 프로젝트의 `.csproj`가 이 파일들을 직접 컴파일합니다.

| 파일 | 상태 | 사용하는 B 프로젝트 | 용도 |
|---|---|---|---|
| `src/LocalAiPerf.Core/Hardware/HardwareModels.cs` | 원본 그대로 | GpuProfit.Core | 센서 값·상태 모델 |
| `src/LocalAiPerf.Core/Hardware/SensorProviders.cs` | 원본 그대로 | GpuProfit.Core | 센서 공급자 인터페이스 (테스트의 다중 GPU mock도 이것으로 구현) |
| `src/LocalAiPerf.Core/Metrics/MetricCatalog.cs` | 원본 그대로 | GpuProfit.Core | 지표 키 |
| `src/LocalAiPerf.Core/Metrics/NumberText.cs` | 원본 그대로 | GpuProfit.Core | 수치 표시 |
| `src/LocalAiPerf.Core/Diagnostics/AppLog.cs` | **수정 1곳** | GpuProfit.Core | 앱 로그, 비밀정보 제거기 |
| `src/LocalAiPerf.Telemetry/HardwareMonitor.cs` | 원본 그대로 | GpuProfit.Recording | GPU+시스템 센서 결합, 예외 없이 샘플링 |
| `src/LocalAiPerf.Telemetry/Nvml/NvmlApi.cs` | 원본 그대로 | GpuProfit.Recording | NVML 읽기 전용 바인딩 (System32에서만 로드) |
| `src/LocalAiPerf.Telemetry/Nvml/NvmlGpuProvider.cs` | 원본 그대로 | GpuProfit.Recording | 전력·누적 에너지 카운터·온도·사용률·VRAM·클럭 |
| `src/LocalAiPerf.Telemetry/Platform/CpuUsageCalculator.cs` | 원본 그대로 | GpuProfit.Recording | CPU 사용률 |
| `src/LocalAiPerf.Telemetry/Platform/WindowsSystemProvider.cs` | 원본 그대로 | GpuProfit.Recording | Windows CPU·RAM |
| `src/LocalAiPerf.Telemetry/Platform/LinuxProcSystemProvider.cs` | 원본 그대로 | GpuProfit.Recording | 빌드 머신 테스트용 |
| `src/LocalAiPerf.Telemetry/Platform/UnsupportedSystemProvider.cs` | 원본 그대로 | GpuProfit.Recording | 꺼짐·미지원 공급자 |
| `src/LocalAiPerf.Telemetry/Mock/MockProviders.cs` | 원본 그대로 | GpuProfit.Recording | `--mock-sensors` |
| `src/LocalAiPerf.Storage/SqliteHelpers.cs` | 원본 그대로 | GpuProfit.Storage | 연결 문자열, 헤더 검사, 손상 판별 |
| `src/LocalAiPerf.Analytics/Statistics.cs` | 원본 그대로 | GpuProfit.Accounting | 요약 통계 |
| `src/LocalAiPerf.Export/CsvWriter.cs` | 원본 그대로 | GpuProfit.Export | RFC 4180 CSV, 수식 주입 방지 |
| `src/LocalAiPerf.Presentation/Charts/ChartModel.cs` | 원본 그대로 | GpuProfit.Presentation | 차트 모델, 다크 팔레트 |
| `src/LocalAiPerf.Presentation/Charts/ChartRenderer.cs` | 원본 그대로 | GpuProfit.Presentation | ScottPlot 렌더링, 한글 글꼴 |

**수정한 파일 1개** — `AppLog.cs`: C가 만든 패치(`FileAppLogger`에 선택 인자 `filePrefix`, 기본값 `"lapc"`)가 적용된 C의 복사본을 가져왔습니다.
B는 로그 파일을 `gpc-*.log`로 구분합니다. 기본값이 A와 같아 A의 동작은 바뀌지 않습니다. 변경 내용: [`patches/AppLog.cs.patch`](../vendor/local-ai-performance-center/patches/AppLog.cs.patch).
나머지 17개 파일은 A 원본, 그리고 C의 vendor 복사본과 바이트 단위로 같음을 확인했습니다.

**호환용 코드** — `src/GpuProfit.Recording/SamplingSettingsShim.cs`: A의 `HardwareMonitor.Create`가 참조하는 `SamplingSettings` 두 스위치만 같은 이름으로 정의합니다(C와 같은 방식).

**무결성 확인** — `VendorIntegrityTests.Vendored_files_match_the_manifest` 테스트가 빌드마다 각 파일의 SHA-256을 매니페스트와 비교합니다.

## C(와 A)를 출발점으로 B에서 고쳐 쓴 파일

각 파일 머리 주석에 출처를 적었습니다.

| B 파일 | 출발점 | 바꾼 점 |
|---|---|---|
| `GpuProfit.Core/AppPaths.cs` | C `WinDiag.Core/AppPaths.cs` (A의 포터블 규칙) | 폴더 `GpuProfitCenter`, DB `gpc.db` |
| `GpuProfit.Core/DataFolderLock.cs` | C 같은 파일 | 잠금 파일 이름 `gpc.lock` |
| `GpuProfit.Core/Settings/AppSettings.cs` (`SettingsStore`) | C 설정 저장 | B의 설정 항목, 원자적 저장, 깨진 파일 보관 |
| `GpuProfit.Storage/ProfitStore.cs` (열기·격리·백업·검증·복원) | C `DiagnosticsStore` (A `TelemetryStore`) | B 스키마, 앱 식별자로 다른 앱 DB 거부, 보존 기간 정리에서 세션 구간 보존 |
| `GpuProfit.Recording/TelemetryRecorder.cs` | C `SensorRecorder` (A `TelemetryCollector`) | 최대 간격 초과·GPU 측정 끊김·절전 구간을 공백으로 기록, 세션 경계 샘플 |
| `GpuProfit.Recording/ProfitEngine.cs` | C `DiagnosticsEngine` 구조 | 실행 복구 후 세션 복구, 정상 종료 시 세션 일시정지 |
| `GpuProfit.Presentation/ViewModels/MainViewModel.cs` (`PageViewModel`, `Busy`) | C 같은 파일 | B의 8개 화면, 입력 오류 처리(`Act`) |
| `GpuProfit.Presentation/Services.cs` | C `Services.cs` | 복원·글자 크기 호스트 |
| `GpuProfit.App/App.xaml.cs`, `StartupOptions.cs` | C 같은 파일 | B 엔진, 절전 알림, `--start-test-session` |
| `GpuProfit.App/Themes/Theme.xaml`, `Converters.cs` | C (A의 검증된 다크 팔레트) | 입력 칸·경고 스타일 추가 |
| `GpuProfit.App/Controls/ChartView.xaml(.cs)` | C `ChartView` | C의 이벤트 세로선 제거, 비시간 X축 레이블 |
| `GpuProfit.App/Services/WpfServices.cs` | C 같은 파일 | 무인 실행용 열기 응답 |
| `GpuProfit.App/SmokeTestRunner.cs` | C `SmokeTestRunner` | B의 8개 화면과 회계 명령 점검 |
| `scripts/publish.ps1`, `scripts/smoke-gui.ps1`, `.github/workflows/ci.yml` | C 같은 파일 | 두 EXE(GUI·CLI), 기록 중 강제 종료 후 세션 복구 확인. 액션 버전 고정값은 C와 같음 |

에너지 적산, 전력 산정 기준, 전기요금(누진 추정 포함), 비용 배분, 감가상각, 수익 분류와 손익 지표, 시나리오·민감도, 세션 비교, 기간 집계, 세션 수명주기와 복구, 데이터 모델, 지급 내역 가져오기, 내보내기, 8개 화면 뷰모델과 XAML은 B에서 새로 작성했습니다.

## 라이선스 판단

- A와 C에는 LICENSE 파일이 없습니다. A·B·C는 같은 소유자(Heo1113)의 저장소이며, 재사용은 소유자가 프로젝트 B 요구사항으로 지시한 것입니다. 따라서 별도 라이선스 허락이 필요한 상황은 아니라고 판단했습니다. B를 제3자에게 배포할 때의 라이선스는 소유자가 정해야 합니다.
- A는 "다른 프로젝트에서 복사한 소스 코드가 없다"고 밝힙니다. 가져온 파일에서 제3자 저작권 표기를 찾지 못했습니다.
- A·C가 쓰는 외부 구성요소(NVML은 드라이버의 `nvml.dll`을 실행 시 읽기만 함, NuGet 패키지)는 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)에 적었습니다.

## A가 갱신되었을 때

1. A의 새 커밋을 읽기 전용으로 내려받습니다.
2. 필요한 파일만 `vendor/local-ai-performance-center/` 아래 같은 경로에 덮어씁니다(덮어쓰기 전에 차이 검토).
3. `AppLog.cs`는 패치를 다시 적용합니다.
4. 매니페스트의 `commit`, `commitDate`, 각 파일의 `sourceSha256`을 새 값으로 고치고 `python3 scripts/vendor-manifest.py`로 `currentSha256`을 다시 계산합니다.
5. `dotnet test`로 무결성 테스트와 전체 테스트를 확인하고 B 저장소에서만 커밋합니다.
