# 제품 분리 — IRON (1순위, 확정 디자인 IRON A-Slim)

PC Control Center 저장소 안에서 제품을 독립 프로젝트로 나눕니다. 1순위는 하드웨어 모니터 **IRON**이고, RELAY(2순위)·VECTOR(3순위)는 IRON 실기 UAT 승인 뒤 별도 지시로 시작합니다.
기존 `PcControlCenter.App`, AI Operator(E), GPU 수익성(B) 소스는 지우지 않고 그대로 빌드·테스트합니다.

## 진행 상태

| Phase | 내용 | 상태 |
|---|---|---|
| 0 | 공유 라이브러리 분리 (`Suite.Core.Hardware`, `Suite.Core.Storage`), IRON 데이터 분리 (`Iron.Core`) | 완료 — 빌드 경고 0, 테스트 통과 (아래 "Phase 0 확인") |
| 1 | IRON 핵심 UI(01 OVERVIEW ~ 05 SETTINGS)·시스템 트레이 (`Iron.Presentation`, `Iron.App`) | 대기 — 디자인 패키지 필요 |
| 2 | 외부 장치: Antec FLUX PRO, Thermal Grizzly WireView Pro II (`Suite.Devices.FluxPro`, `Suite.Devices.WireView`) | 대기 |

- 기준 커밋: `main@a080a2febe079228d001c8b7febc26a9d42b1e54`. main 브랜치와 운영 배포 폴더는 바꾸지 않습니다.
- **디자인 패키지(PCControlCenter-ProductSplit-DesignPackage-20261124의 docs/01~07, tokens/*.json, prototype/index.html, mockups/*.png)는 아직 받지 못했습니다.**
  Phase 0은 착수 프롬프트에 적힌 구조·불변량·경로만으로 구현했고, `docs/07_PHASED_PLAN_AND_TEST_UAT_CRITERIA.md`의 Phase 0 기준은 패키지를 받은 뒤 다시 대조해야 합니다.

## 구조

```
Iron.App (WPF, Phase 1) ── Iron.Presentation (뷰모델, WPF 비의존, Phase 1)
        └──────────────── Iron.Core ── 설정 data\iron\iron-settings.json, DB data\iron\iron-telemetry.db
                              ├── Suite.Core.Hardware ── SharedHardwareHub (프로세스당 NVML 리더 1개) ── A의 센서 단일 사본(LocalAiPerf.Telemetry)
                              └── Suite.Core.Storage ─── 제품 데이터 폴더, JsonSettingsStore<T>, SqliteDatabase, DataFolderLock
Suite.Devices.FluxPro / Suite.Devices.WireView (Phase 2) ── Suite.Core.Hardware

PcControlCenter.App ─ PcControlCenter.Modules/Presentation ─ Suite.Core.Hardware (이전 PcControlCenter.Hardware)
```

- `src/PcControlCenter.Hardware`는 `src/Suite.Core.Hardware`로 **이름만 옮겼습니다**(`git mv`, 코드 변경 없음, 이름공간 `PcControlCenter.Hardware` → `Suite.Core.Hardware`).
  PC Control Center 쪽 변경은 `using`·프로젝트 참조 경로뿐입니다.
- IRON 쪽 어셈블리는 PC Control Center 어셈블리(특히 WDDM 메모리 리더 `PcControlCenter.GpuMemory`)를 참조하지 않습니다. 테스트가 참조 그래프를 확인합니다.
- `Iron.slnx`: IRON과 그 의존 프로젝트만. `PcControlCenter.slnx`: 전체(새 프로젝트 포함, CI가 빌드).
- 새 상주 Windows 서비스나 IPC 데몬은 만들지 않습니다. IRON은 자기 프로세스 안에서 허브 하나를 돌립니다.

## 불변량과 구현 위치

| 불변량 | 구현 | 테스트 |
|---|---|---|
| NVML은 허브 하나만 읽음 | `SharedHardwareHub` (옮김, 그대로) | `PcControlCenter.Tests/SharedHardwareHubTests`, `HubUnderLoadTests` (기존) |
| WDDM PDH VRAM과 NVML 값을 섞지 않음 | `MetricSource`, `SourcedValues.SumOfOneSource`(출처가 다르면 예외), IRON DB 표 이름이 출처(`nvml_gpu_samples`), IRON이 `PcControlCenter.GpuMemory`를 참조하지 않음 | `SourcedValueTests`, `IronIndependenceTests` |
| GPU 하나라도 `PowerW == null`이면 합계 전력·시간당 요금을 만들지 않음(PARTIAL) | `GpuPowerTotal.From` (PC Control Center 대시보드 `ModuleSummaries.Economics`와 같은 규칙, 음수·NaN·무한대도 누락으로 봄), `EstimatedCostPerHour`는 Complete일 때만 | `GpuPowerTotalTests` |
| 위 규칙을 저장소에서도 | `gpu_power_totals` 표의 CHECK 제약: PARTIAL/UNAVAILABLE에는 `total_w`가 들어갈 수 없음 | `IronTelemetryStoreTests.The_database_itself_refuses_a_total_from_an_incomplete_set` |
| 미지(중첩) JSON 필드 보존 | `JsonSettingsStore<T>`(ShellSettingsStore 패턴: 원자적 저장, 손상 파일은 이름을 바꿔 보관, 새 스키마 번호 유지) + 모든 설정 클래스의 `[JsonExtensionData]` | `JsonSettingsStoreTests`, `IronSettingsTests` |
| IRON 설정·DB를 모듈 DB와 분리 | `IronPaths`: `<데이터>\iron\iron-settings.json`, `<데이터>\iron\iron-telemetry.db`. 원래 프로그램 폴더·PC Control Center `modules` 폴더와 겹치면 거부. DB에 `application_id`(IRON) 표시, 다른 프로그램 DB는 쓰기 전에 거부 | `IronPathsTests`, `ProductDataRootTests`, `SqliteDatabaseTests` |

## 데이터 위치

| 경우 | IRON 폴더 |
|---|---|
| `--data-dir DIR` | `DIR\iron\` |
| 실행 파일 옆 `portable.txt` | `<실행 파일 폴더>\data\iron\` (PC Control Center 포터블과 같은 `data` 아래, `data\modules`와는 분리) |
| 그 밖 | `%LOCALAPPDATA%\PcControlSuite\data\iron\` |

## 다음 단계 계획

### Phase 1 — IRON 핵심 UI·트레이 (디자인 패키지 수령 후)

- `Iron.Presentation`: 01 OVERVIEW 뷰모델 — GPU 카드 2장(RTX 5080, RTX 5060 Ti; `GpuDeviceKey`로 순서 고정), 모델명 오른쪽 1줄 `TEMP`·`PWR`·`MHz`, 아래 `GPU LOAD`(주황 분절)·`VRAM`(앰버 분절) 1:1 정렬,
  하단 3칸 CPU Load·System RAM·GPU Board Power(`GpuPowerTotal`, PARTIAL 표시), `EXTERNAL DEVICES` 도크와 `DETAILS →`.
  경고 상태 기계: 이상이 생긴 수치·세그먼트와 우측 상단 상태 LED만 적색, 정상화되면 복귀(배너·프레임 테두리 없음).
  트레이 뷰모델: GPU 온도·VRAM, WireView 전력·온도, `RELEASE USB`; FLUX PRO는 미연결·iUnity 충돌 때만 한 줄.
- `Iron.App`(WPF, `net10.0-windows`): 타이틀바 통합(`IRON` 워드마크, 스위치 `01 OVERVIEW`~`05 SETTINGS`, `ONLINE` LED, 트레이·반폭 버튼),
  4K 175% 최대화 2194×1186 DIP·반폭 1097×1186 DIP 반응형, 전 화면 `Typography.NumeralAlignment="Tabular"`, 토큰(`tokens/*.json`) → 리소스 사전.
- 기록: 허브 측정값을 `IronRecordingSettings.IntervalSeconds`마다 `IronTelemetryStore.Append`, 보관 기간 지나면 정리.

### Phase 2 — 외부 장치

- `Suite.Devices.FluxPro`(VID 0x2022 / PID 0x0522, 12바이트 HID, 1 Hz): 허브의 `Sampled`를 구독(별도 폴링 없음). iUnity 실행이 감지되면 종료시키지 않고 충돌 안내 후 송출 보류. 팬 제어 없음(ASRock BIOS). MIT 참조(motiop/antec-flux-pro-display-windows)만, GPL-3.0 코드 복사 금지.
- `Suite.Devices.WireView`(VID 0x0483 / PID 0x5740, EF05/EF06, FW v05): 읽기 전용 명령 `0x01`·`0x04`만. CRC가 없으므로 길이·물리 범위·3초 디바운싱으로 거름.
  NVML 보드 전력과 WireView 커넥터 전력은 `MetricSource`로 나눠 나란히 비교(합산·대체 없음). 비동기 `RELEASE USB / RESUME`로 COM 포트 양보. MIT 참조(VolterMort/WireViewPro2_MSIAB)만, `wireview-linux` 디컴파일 코드 복사 금지.
- 실기 검증 전에는 iUnity·WireView 공식 앱을 지우거나 자동 시작을 끄지 않습니다.

## 가정과 확인할 점

1. 디자인 패키지가 없어 Phase 0의 폴더 배치(`src/Suite.*`, `src/Iron.*`), 설치 폴더 이름(`%LOCALAPPDATA%\PcControlSuite`), IRON 설정 항목(센서 간격, 기록, 창 배치, 전기 요금, 경고 임계값)은 추정으로 정했습니다. docs/01~07과 다르면 맞춥니다.
2. FLUX PRO 화면의 CPU 온도: 허브는 CPU 온도를 읽지 않습니다(A의 `HardwareMonitor.NotCollected`: 커널 드라이버·관리자 권한 필요). Phase 2 전에 표시 방식(빈칸/GPU만) 결정이 필요합니다.
3. WireView 직렬 통신에는 `System.IO.Ports`(MIT) 패키지 추가가 필요할 것으로 예상합니다(현재 `Directory.Packages.props`에 없음).
4. 같은 PC에서 PC Control Center와 IRON을 동시에 실행하면 프로세스마다 허브(NVML 리더)가 하나씩 있습니다. NVML은 여러 프로세스의 읽기를 허용하지만, 한 프로세스 안에서는 리더가 하나입니다.

## Phase 0 확인

Linux(Ubuntu 24.04, .NET SDK 10.0.115)에서 확인했습니다. WPF 프로젝트는 컴파일만 됩니다. Windows 러너 결과는 CI에서 확인합니다.

| 항목 | 결과 |
|---|---|
| `dotnet build PcControlCenter.slnx -c Release` (전체 재빌드) | 경고 0, 오류 0 |
| `dotnet build Iron.slnx -c Release` | 경고 0, 오류 0 |
| 기존 테스트 (A·B·C·E 회귀, 통합, Basalt) | 기준 커밋과 같은 결과 (아래 표) |
| 새 테스트 `Suite.Tests` / `Iron.Tests` | 37 / 19 통과 |
| `scripts/module-manifest.py verify`, `scripts/xaml-resource-check.py` | 통과 (A·B·C·E 가져온 소스 변경 없음: unchanged 442 / modified 37 / removed 2 / added 5, 기준 커밋과 같음) |

| 테스트 프로젝트 | 기준 커밋 | Phase 0 |
|---|---|---|
| A `LocalAiPerf.Tests` | 93 통과 · 1 건너뜀 | 93 통과 · 1 건너뜀 |
| B `GpuProfit.Tests` | 145 통과 | 145 통과 |
| C `WinDiag.Tests` | 123 통과 · 9 건너뜀 | 123 통과 · 9 건너뜀 |
| E `WindowsAiOperator.Tests` | 155 통과 | 155 통과 |
| `PcControlCenter.Tests` | 248 통과 · 14 건너뜀 | 248 통과 · 14 건너뜀 |
| `PcControlCenter.Basalt.Tests` | 27 통과 | 27 통과 |
| `Suite.Tests` | — | 37 통과 |
| `Iron.Tests` | — | 19 통과 |

건너뛴 테스트는 Windows 전용(이벤트 로그, Win32 프로세스·PDH 등)이며 Windows CI에서 실행됩니다. E의 실제 UIA 테스트와 GUI 스모크도 Windows CI 전용입니다.
