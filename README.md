# PC Control Center

네 Windows 프로그램을 하나의 WPF 창으로 통합한 프로그램입니다. 단일 실행 파일 `PcControlCenter.exe` 하나에서 모든 기능을 씁니다.

| 모듈 | 원본 프로그램 | 통합 방식 |
|---|---|---|
| 로컬 AI | A · Local AI Performance Center (`5b6aae1`) | A의 MonitoringService·화면을 그대로 실행, Strata 조회는 A의 별도 루프 |
| GPU 경제성 | B · GPU Profit & Compute Center (`5af1902`) | B의 ProfitEngine·화면을 그대로 실행, 전력 적산은 원래 측정 시각으로 |
| 시스템 진단 | C · Windows Diagnostics Lab (`0b4aa14`) | C의 DiagnosticsEngine·화면을 그대로 실행, 이벤트 로그는 C의 별도 수집기 |
| AI Operator | E · Windows AI Operator (`a0562ee`) | 진행 중 — 실행 엔진을 별도 권한 제한 프로세스로 분리해 연결 |

- **센서는 한 곳에서만 읽습니다.** A·B·C에 중복으로 있던 A의 센서 코드는 A의 단일 사본으로 합쳤고, 공통 센서 리더(`SharedHardwareHub`)가
  NVML·CPU·RAM을 한 번 읽어 각 모듈에 원래 측정 시각과 품질 정보 그대로 나눠 줍니다. 각 모듈은 자기 간격과 공백 규칙으로 자기 DB에 기록합니다.
- **데이터베이스를 합치지 않습니다.** 모듈마다 자기 폴더에 원래 형식의 DB(`lapc.db`, `gpc.db`, `wdl.db`, `wao.db`)를 둡니다.
  기존 프로그램의 데이터 폴더는 데이터 폴더로 쓸 수 없게 막혀 있으며, 기존 데이터는 읽기 전용 검사 후 복사본으로만 가져옵니다.
- **한 모듈의 장애가 다른 모듈을 멈추지 않습니다.** 모듈은 따로 시작하고, 실패한 모듈 화면에는 이유와 [다시 시작]이 나옵니다.
- 관리자 권한이 필요 없고, 시스템 설정·BIOS를 바꾸지 않습니다.

> 검증 상태: Linux에서 전체 빌드(WPF 컴파일 포함)와 원본 4개 프로그램의 회귀 테스트·통합 테스트를 실행합니다. Windows GUI·실기(RTX 5080,
> 4K 175%, 다중 모니터, 실제 Strata)는 Windows CI와 사용자 PC 확인이 필요합니다. 자세한 내용은 [docs/VERIFICATION.md](docs/VERIFICATION.md)를 보세요.

## 화면

| 메뉴 | 내용 |
|---|---|
| 대시보드 | GPU·CPU·RAM(공통 센서), Strata 모델·추론 상태(A), 현재 GPU 보드 전력과 추정 전력비·오늘 세션 비용(B), 최근 오류·WHEA·BSOD(C), Operator 상태(E), 모듈 상태 |
| 하드웨어 | 공통 센서(리더 상태·소비 모듈·센서 지원 표·장치), 실시간 센서·그래프(A), GPU 전력·장치(B), 오류 시점 센서 기록(C) |
| 로컬 AI | 추론 개요, Strata 런타임, 세션 기록, 조건별 비교 (A) |
| GPU 경제성 | 현황·전력비, 세션·에너지, 수익성(실현/예상 구분), 시나리오·손익, 기간별 기록, 세션 비교 (B) |
| 시스템 진단 | 현황, 이벤트 탐색, 타임라인, 진단(WHEA·GPU·BSOD), 기준선 비교 (C) |
| AI Operator | 자연어 작업, 계획 확인, 단계별 승인, 일시정지·재개·긴급 중단, 창 검사기, 작업 기록, 모델 연결 (E) |
| 통합 기록 | 같은 시간대의 센서·추론·전력·오류·작업을 출처와 관측 공백과 함께 표시 (인과관계를 단정하지 않음) |
| 설정 | 공통(모듈 켜기/끄기, 공통 센서, 화면 배율), 데이터·이전, 모듈별 설정, Operator 안전 권한 |

## 실행

```powershell
.\PcControlCenter.exe
```

| 옵션 | 설명 |
|---|---|
| `--data-dir DIR` | 데이터 폴더 지정 (기존 프로그램의 폴더와 겹치면 거부) |
| `--mock-sensors` | 테스트용 가상 센서 (모든 화면·기록에 MOCK 표시) |
| `--demo-data` | B·C의 표시된 데모 데이터 적재 (실제 값 아님) |
| `--section KEY` | 시작 화면 (`dashboard`, `hardware`, `local-ai`, `economics`, `diagnostics`, `operator`, `history`, `settings`) |
| `--smoke-test --smoke-report DIR` | CI용 자동 화면 점검 후 종료 |

데이터 위치: 실행 파일 옆 `portable.txt`가 있으면 `data\`, 없으면 `%LOCALAPPDATA%\PcControlCenter`.
그 아래 `modules\local-ai`, `modules\gpu-economics`, `modules\diagnostics`, `modules\operator`에 모듈별 데이터가 있습니다.

## 소스에서 빌드

필요: .NET SDK 10.0. Linux에서도 전체 솔루션 컴파일과 테스트가 됩니다(WPF는 컴파일만).

```bash
dotnet build PcControlCenter.slnx -c Release
```

```bash
dotnet test --project tests/PcControlCenter.Tests -c Release
```

```powershell
./scripts/publish.ps1 -OutputDir dist
```

## 저장소 구조

| 경로 | 내용 |
|---|---|
| `modules/<원본 저장소 이름>/` | A·B·C·E의 소스(고정 커밋에서 가져옴). 변경 내역은 `python3 scripts/module-manifest.py verify`와 [docs/REUSE.md](docs/REUSE.md) |
| `src/PcControlCenter.Hardware` | 공통 센서 리더 |
| `src/PcControlCenter.Core` | 데이터 폴더, 공통 설정, 모듈 감독(장애 격리) |
| `src/PcControlCenter.Modules` | A·B·C 엔진을 모듈로 실행하는 어댑터, 대시보드 요약 |
| `src/PcControlCenter.Presentation` | 셸 뷰모델(메뉴, 통합 대시보드, 공통 센서, 설정) — WPF 비의존, Linux에서 테스트 |
| `src/PcControlCenter.App` | `PcControlCenter.exe` (WPF) |
| `tests/PcControlCenter.Tests` | 통합 테스트 |
