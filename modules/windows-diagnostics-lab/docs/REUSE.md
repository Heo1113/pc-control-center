# 프로젝트 A 재사용 기록

Windows Diagnostics Lab(프로젝트 C)은 GPU·CPU·RAM 센서, 로그, SQLite 보조 코드, 통계, CSV, 차트를 새로 만들지 않고
**Local AI Performance Center(프로젝트 A)** 의 코드를 가져와 씁니다. 이 문서는 무엇을, 어느 버전에서, 어떻게 가져왔는지와
라이선스 판단 근거를 기록합니다.

## 원칙과 지킨 방법

| 원칙 | 지킨 방법 |
|---|---|
| A 저장소는 읽기 전용 | A를 읽기만 했고, A 저장소에 커밋·푸시·이슈·PR을 만들지 않았습니다. 이 작업 세션의 GitHub 쓰기 권한은 C 저장소로 한정되어 있었습니다. |
| A 코드를 무단 수정·리팩터링하지 않음 | 가져온 파일은 C 저장소의 `vendor/` 아래 복사본입니다. 18개 중 17개는 바이트 단위로 원본과 같고, 1개(`AppLog.cs`)만 최소 수정했으며 패치 파일을 남겼습니다. |
| 공통 구성요소를 분리해도 A에 적용하지 않음 | A를 공용 라이브러리로 바꾸지 않았습니다. C가 필요한 파일을 복사해 컴파일합니다. |
| C 실행에 A 설치가 필요 없음 | C의 EXE에 A의 코드가 컴파일되어 들어갑니다. A 프로그램, A의 DLL, A의 설정 파일을 찾거나 읽지 않습니다. |
| A의 실행 중 DB를 수정하지 않음 | C는 자기 데이터 폴더(`WindowsDiagnosticsLab`)의 `wdl.db`만 엽니다. A의 데이터 폴더·DB 경로를 참조하는 코드가 없습니다. 열려는 DB가 C의 것이 아니면 열지 않습니다(`Newer_or_foreign_databases_are_not_opened` 테스트). |
| 출처와 라이선스 보존 | 아래 매니페스트(커밋, 파일별 SHA-256, 용도), 각 C 파일 주석의 출처 표기, [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). |
| A와 C의 독립 업데이트 | C는 A의 특정 커밋에 고정된 복사본을 씁니다. A가 바뀌어도 C는 그대로이며, 갱신은 아래 절차로만 합니다. |

## 출처

- 저장소: https://github.com/Heo1113/local-ai-performance-center
- 커밋: `5b6aae12c28efbdbebe9a7df280c02f5494b037e` (2026-11-02 12:01 +09:00)
- 소유자: Heo1113 (이 저장소와 같은 소유자)
- 기계가 읽는 기록: [`vendor/local-ai-performance-center/manifest.json`](../vendor/local-ai-performance-center/manifest.json)

## 그대로 가져온 파일 (vendor)

경로는 `vendor/local-ai-performance-center/` 기준이며 A 저장소의 경로와 같습니다. 각 C 프로젝트의 `.csproj`가 이 파일들을 직접 컴파일합니다.

| 파일 | 상태 | 사용하는 C 프로젝트 | 용도 |
|---|---|---|---|
| `src/LocalAiPerf.Core/Hardware/HardwareModels.cs` | 원본 그대로 | WinDiag.Core | 센서 값·상태 모델 (`GpuSample`, `SystemSample`, `HardwareSnapshot`, `SensorStatus`) |
| `src/LocalAiPerf.Core/Hardware/SensorProviders.cs` | 원본 그대로 | WinDiag.Core | 센서 공급자 인터페이스 |
| `src/LocalAiPerf.Core/Metrics/MetricCatalog.cs` | 원본 그대로 | WinDiag.Core | 지표 키 (A의 Strata 지표 정의도 들어 있으나 C는 GPU·시스템 지표만 사용) |
| `src/LocalAiPerf.Core/Metrics/NumberText.cs` | 원본 그대로 | WinDiag.Core | 수치 표시 형식 |
| `src/LocalAiPerf.Core/Diagnostics/AppLog.cs` | **수정 1곳** | WinDiag.Core | 앱 로그, 비밀정보 제거기(`SecretRedactor`) |
| `src/LocalAiPerf.Telemetry/HardwareMonitor.cs` | 원본 그대로 | WinDiag.Collection | GPU+시스템 센서 결합, 예외 없이 샘플링 |
| `src/LocalAiPerf.Telemetry/Nvml/NvmlApi.cs` | 원본 그대로 | WinDiag.Collection | NVML 읽기 전용 바인딩 (System32에서만 로드) |
| `src/LocalAiPerf.Telemetry/Nvml/NvmlGpuProvider.cs` | 원본 그대로 | WinDiag.Collection | NVIDIA GPU 센서 |
| `src/LocalAiPerf.Telemetry/Platform/CpuUsageCalculator.cs` | 원본 그대로 | WinDiag.Collection | CPU 사용률 계산 |
| `src/LocalAiPerf.Telemetry/Platform/WindowsSystemProvider.cs` | 원본 그대로 | WinDiag.Collection | Windows CPU·RAM (`GetSystemTimes`, `GlobalMemoryStatusEx`) |
| `src/LocalAiPerf.Telemetry/Platform/LinuxProcSystemProvider.cs` | 원본 그대로 | WinDiag.Collection | Linux `/proc` (빌드 머신 테스트용) |
| `src/LocalAiPerf.Telemetry/Platform/UnsupportedSystemProvider.cs` | 원본 그대로 | WinDiag.Collection | 꺼짐·미지원 공급자 |
| `src/LocalAiPerf.Telemetry/Mock/MockProviders.cs` | 원본 그대로 | WinDiag.Collection | 테스트용 가상 센서 (`--mock-sensors`) |
| `src/LocalAiPerf.Storage/SqliteHelpers.cs` | 원본 그대로 | WinDiag.Storage | SQLite 연결 문자열, 파일 헤더 검사, 손상 판별, 버전 예외 |
| `src/LocalAiPerf.Analytics/Statistics.cs` | 원본 그대로 | WinDiag.Analysis | 요약 통계, 백분위, 변화율 |
| `src/LocalAiPerf.Export/CsvWriter.cs` | 원본 그대로 | WinDiag.Export | RFC 4180 CSV, 스프레드시트 수식 주입 방지 |
| `src/LocalAiPerf.Presentation/Charts/ChartModel.cs` | 원본 그대로 | WinDiag.Presentation | 차트 모델, 검증된 다크 팔레트 |
| `src/LocalAiPerf.Presentation/Charts/ChartRenderer.cs` | 원본 그대로 | WinDiag.Presentation | ScottPlot 렌더링, 한글 글꼴 선택 |

### 수정한 파일 1개

- `AppLog.cs`: `FileAppLogger` 생성자에 선택 인자 `filePrefix`(기본값 `"lapc"`)를 추가해 C의 로그 파일 이름을 `wdl-*.log`로 구분합니다.
  기본값이 A와 같으므로 A의 기존 동작은 바뀌지 않습니다. 변경 내용은 [`patches/AppLog.cs.patch`](../vendor/local-ai-performance-center/patches/AppLog.cs.patch)에 있습니다.
- 원본 SHA-256 `673e014b…c340e`, 현재 SHA-256 `38e0f510…a778b` (전체 값은 매니페스트).

### 호환용으로 C에 새로 둔 코드

- `src/WinDiag.Collection/Sensors/SamplingSettingsShim.cs`: A의 `HardwareMonitor`가 참조하는 `LocalAiPerf.Core.Settings.SamplingSettings`만
  C 쪽에 같은 이름으로 정의했습니다. A의 설정 시스템 전체를 가져오지 않기 위해서입니다.

### 무결성 확인

`VendorIntegrityTests.Vendored_files_match_the_manifest` 테스트가 빌드마다 각 파일의 SHA-256을 매니페스트와 비교합니다.
누군가 vendor 파일을 고치면 매니페스트·패치를 함께 고치지 않는 한 테스트가 실패합니다.

## 구조를 참고해 C에 맞게 다시 쓴 파일

아래 파일은 A의 같은 역할 파일을 출발점으로 C의 요구에 맞게 고쳐 쓴 것입니다. 파일 머리 주석에 출처를 적었습니다.

| C 파일 | A의 출발점 | 바꾼 점 |
|---|---|---|
| `src/WinDiag.Core/AppPaths.cs` | `LocalAiPerf.Core/AppPaths.cs` | 폴더 이름 `WindowsDiagnosticsLab`, DB `wdl.db`, 포터블 `data\` |
| `src/WinDiag.Core/Settings/AppSettings.cs` (직렬화·저장 부분) | `LocalAiPerf.Core/Settings/SettingsSerializer.cs` 등 | C의 설정 항목, 깨진 설정 파일은 옆으로 옮겨 보관 |
| `src/WinDiag.Storage/DiagnosticsStore.cs` (열기·복구·백업) | A의 `TelemetryStore` | C의 스키마, 손상 DB 격리(삭제 안 함), 다른 앱 DB 거부, 백업 검증·복원 |
| `src/WinDiag.Collection/Sensors/SensorRecorder.cs` (샘플링 루프) | A의 `TelemetryCollector` | 시계 점프 감지, 센서 없음 기록, 실행(run) 단위 저장 |
| `src/WinDiag.Presentation/Formatting.cs`, `Services.cs` | A의 `Fmt`, UI 서비스 | 진단용 표시 형식, 대화상자·셸 서비스 |
| `src/WinDiag.App/App.xaml.cs` | A의 `App.xaml.cs` | C의 엔진·알림 영역·복원 후 재시작, 데이터 폴더 잠금 |
| `src/WinDiag.App/Themes/Theme.xaml`, `Converters.cs` | A의 다크 팔레트·변환기 | 진단 상태 색상(색과 함께 항상 글자 표시) |
| `src/WinDiag.App/Controls/ChartView.xaml(.cs)` | A의 `ChartView` | 이벤트 세로선 표시 |
| `src/WinDiag.App/Services/WpfServices.cs` | `LocalAiPerf.App/Services/WpfServices.cs` | 무인 실행(스모크 테스트)용 대화상자 |
| `src/WinDiag.App/PromptWindow.xaml(.cs)` | `LocalAiPerf.App/PromptWindow` | 네임스페이스만 변경 |
| `src/WinDiag.App/SmokeTestRunner.cs` | A의 `SmokeTestRunner` | C의 7개 화면 점검 항목 |
| `scripts/publish.ps1`, `scripts/smoke-gui.ps1` | A의 배포·스모크 스크립트 | 두 EXE(GUI·CLI), 강제 종료 후 재시작 확인 |
| `.github/workflows/ci.yml` | A의 CI | 액션 버전 고정값을 A와 같게, Windows 실제 로그 테스트 추가 |
| `tests/WinDiag.Tests/TestHelpers.cs` | A의 테스트 도우미 | 가상 시각(벽시계 점프) 추가 |

이벤트 수집·분류, 충돌 분석, 타임라인, 기준선 비교, 진단 보고서, 스냅샷, 덤프 헤더 읽기, 개인정보 마스킹, 7개 화면 뷰모델은 C에서 새로 작성했습니다.

## 라이선스 판단

- A 저장소에는 LICENSE 파일이 없습니다. A와 C는 같은 소유자의 저장소이며, 재사용은 소유자가 프로젝트 C 요구사항으로 지시한 것입니다.
  따라서 C가 A의 코드를 쓰는 데 별도 라이선스 허락이 필요한 상황은 아니라고 판단했습니다. C를 제3자에게 배포할 때의 라이선스는 소유자가 정해야 합니다.
- A의 `THIRD-PARTY-NOTICES.md`는 "다른 프로젝트에서 복사한 소스 코드가 없다"고 밝힙니다. 가져온 18개 파일에서 제3자 저작권 표기나 라이선스 머리말을 찾지 못했습니다.
- A의 파일이 쓰는 외부 구성요소(NVML은 NVIDIA 드라이버가 설치한 `nvml.dll`을 실행 시 읽기만 함, ScottPlot·SkiaSharp 등 NuGet 패키지)는 C의
  [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)에 버전과 라이선스를 적었습니다. NVML은 재배포하지 않습니다.

## A가 갱신되었을 때 C를 갱신하는 절차

1. A의 새 커밋을 읽기 전용으로 내려받습니다(A 저장소에는 아무것도 쓰지 않음).
2. 필요한 파일만 `vendor/local-ai-performance-center/` 아래 같은 경로에 덮어씁니다. 덮어쓰기 전에 차이를 검토합니다.
3. `AppLog.cs`는 패치를 다시 적용합니다: `git apply` 또는 수동으로 `filePrefix` 인자를 추가.
4. 매니페스트의 `commit`, `commitDate`, 각 파일의 `sourceSha256`·`currentSha256`을 새 값으로 고칩니다.
5. `dotnet test`로 무결성 테스트와 전체 테스트를 확인하고, C 저장소에서만 커밋합니다.
