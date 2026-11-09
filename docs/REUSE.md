# 재사용과 변경 기록

## 원본

| | 저장소 | 브랜치 | 커밋 |
|---|---|---|---|
| A | https://github.com/Heo1113/local-ai-performance-center | main | `5b6aae12c28efbdbebe9a7df280c02f5494b037e` |
| B | https://github.com/Heo1113/gpu-profit-center | main | `5af1902c59fbcb8f559920b69645a411cb8c0bf7` |
| C | https://github.com/Heo1113/windows-diagnostics-lab | claude/ecstatic-newton-cfosml | `0b4aa14ccf674a54285e7ae1d7c85b2fdbb4859c` |
| E | https://github.com/Heo1113/windows-ai-operator | claude/nifty-pascal-8te367 | `a0562eec6284e71589128b763db26b7e236476d8` |

네 저장소는 읽기만 했습니다(커밋·푸시·PR 병합 없음). 각 저장소의 `src/`, `tests/`, `docs/`, `scripts/`, README, THIRD-PARTY-NOTICES를
`modules/<저장소 이름>/`에 **바이트 그대로** 가져온 커밋이 이 저장소의 첫 작업 커밋입니다. 이후 변경은 모두 별도 커밋으로 남아 있어 `git log -p modules/`로 볼 수 있습니다.

- `modules/IMPORTS.json`: 가져온 파일 481개의 원본 SHA-256
- `python3 scripts/module-manifest.py verify`: 지금 바뀐/추가된/삭제된 파일 목록 (CI에서 매번 실행)
- B·C의 `vendor/local-ai-performance-center/`(A 파일 18개의 사본)는 가져오지 않았습니다. B·C는 A의 단일 사본을 참조합니다.

## 바뀐 파일 (현재 31개 수정, 5개 추가, 2개 삭제 / 원본 그대로 448개)

| 원본 | 파일 | 이유 |
|---|---|---|
| A | `LocalAiPerf.Core/Diagnostics/AppLog.cs` | C가 만든 패치(로그 파일 접두어 선택 인자, 기본값 `lapc`)를 A의 사본에 적용. B·C의 vendor 사본과 같은 내용이 되어 하나로 합칠 수 있게 함 |
| A | `LocalAiPerf.Telemetry/HardwareMonitor.cs` | 공통 센서용 공유 보기 모드(`HardwareMonitor.Shared`). 기존 경로는 그대로 |
| A | `LocalAiPerf.Telemetry/Shared/SharedHardwareSource.cs` (추가) | 공유 센서 원본 인터페이스와 GPU/시스템 제공자 보기 |
| A | `LocalAiPerf.Recording/MonitoringService.cs` | `MonitoringOptions.HardwareFactory` 주입점 (공통 센서 뷰 사용) |
| A | `LocalAiPerf.Storage.csproj`, `LocalAiPerf.Export.csproj` | B·C의 저장소·내보내기가 A의 internal 보조 코드(SqliteHelpers, CsvWriter)를 쓰도록 InternalsVisibleTo |
| A | `LocalAiPerf.App.csproj`, `ModuleResources.xaml` (추가) | 앱 → 화면 라이브러리. 원래 App/MainWindow/스모크 실행기는 컴파일에서 제외(파일은 남김), 화면이 기대하던 자원을 모은 사전 |
| B | `*.csproj` 6개 | vendor 컴파일 → A 프로젝트 참조 |
| B | `GpuProfit.Recording/SamplingSettingsShim.cs` (삭제) | A의 실제 `SamplingSettings`와 같은 이름의 호환용 정의라 충돌. A의 것을 씀 |
| B | `GpuProfit.Recording/ProfitEngine.cs` | `using` 별칭 한 줄 (위 삭제로 인한 이름 모호성) |
| B | `GpuProfit.App.csproj`, `ModuleResources.xaml` (추가) | A와 같음 |
| B | `GpuProfit.Presentation.csproj` | 화면 라이브러리 이름 변경에 맞춘 InternalsVisibleTo |
| B | 테스트 2개 | vendor 매니페스트 검사 → "A 코드가 B에 두 번 컴파일되지 않음" 검사로 대체, 이름 모호성 별칭 |
| C | `*.csproj` 6개, `SamplingSettingsShim.cs` (삭제), `DiagnosticsEngine.cs` | B와 같음 + `EngineOptions.Monitor` 주입점 |
| C | `WinDiag.App.csproj`, `ModuleResources.xaml` (추가) | A와 같음. 알림 영역 아이콘(TrayNotifier)은 제외 — 알림은 셸 화면에 표시 |
| C | 테스트 3개 | vendor 검사 대체, 저장소 루트 찾기(docs 폴더 기준), 이름 모호성 별칭 |
| E | `WindowsAiOperator.Core/Policy/RiskRules.cs` | 내장 차단 목록에 `PcControlCenter`, `PcControlCenter.OperatorHost` 추가 (승인 버튼이 이제 통합 창에 있음) |
| E | `WindowsAiOperator.App.csproj`, `ModuleResources.xaml` (추가), `Services/WpfServices.cs` | 화면 라이브러리화, 글자 배율을 통합 창에 적용하는 연결점 |
| E | `tests/WindowsAiOperator.UiaTests/UiaTestSupport.cs` | 테스트 앱 찾기(`WindowsAiOperator.slnx` 대신 E의 src 폴더) |

## 그대로 재사용한 것

- 엔진: A `MonitoringService`(+Strata 어댑터·세션 기록기), B `ProfitEngine`(에너지 적산·회계·시나리오), C `DiagnosticsEngine`(이벤트 수집·분류·스냅샷·덤프), E `OperatorContext`/`TaskExecutor`/`PolicyEngine`/`ModelPlanner`/`FlaUiDesktop`.
- 뷰모델과 화면: 네 프로그램의 Presentation 프로젝트와 Views/Controls XAML 전부(원래 메인 창 제외).
- 계산: 통합 대시보드의 시간당 전력비는 B의 `ElectricityCostCalculator`, 오늘 비용은 B의 `SessionService.Compute`·`PeriodAggregator`, 최근 오류는 C의 이벤트 질의.
- 데이터 이전 검증: A·B·C의 `ValidateBackup`·`RestoreFrom`.
- 테스트: 원본 테스트 525개(+E UIA 11개)를 그대로 실행합니다.

## 원본 저장소에서 확인된 사항 (원본은 고치지 않음)

- C의 테스트 `Event_6008_localized_time_is_parsed_only_when_plausible`는 Ubuntu 26.04(새 ICU)에서 실패하고 Ubuntu 24.04·Windows에서는 통과합니다.
  ko-KR 날짜 해석이 ICU 버전에 따라 다르기 때문으로 보입니다. Linux CI는 원본이 검증된 Ubuntu 24.04로 고정했습니다(테스트를 건너뛰지 않음). 자세한 내용은 VERIFICATION.md.
