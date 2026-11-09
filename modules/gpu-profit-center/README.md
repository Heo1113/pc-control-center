# GPU Profit & Compute Center

GPU 작업(암호화폐 GPU 채굴, AI 추론·연산 서비스, GPU 컴퓨팅, 기타 사용자 정의 작업)의 **실제 전력 사용량, 운영비, 수익, 손익분기점, 경제적 효율**을 기록하고 분석하는 Windows 데스크톱 프로그램입니다.
특정 코인·풀·서비스에 묶이지 않습니다. 작업을 실행하거나 하드웨어 설정을 바꾸지 않는 **분석 도구**입니다.

- Windows 10/11 x64, .NET 10 · WPF (자체 포함 단일 EXE, 설치 필요 없음)
- 데이터: 앱 전용 SQLite (`gpc.db`), 외부 서비스 접속 없음
- GPU 센서: NVIDIA NVML(드라이버의 `nvml.dll`, 읽기 전용). GPU가 없거나 지원하지 않는 값은 N/A로 표시하며 앱은 그대로 동작합니다.

## 주요 기능

| 화면 | 내용 |
|---|---|
| 대시보드 | 현재 GPU·CPU·RAM, 기록 중인 세션(에너지·전력비·관측률), 오늘·이번 달 에너지·비용·실현 손익 |
| 하드웨어 | GPU 보드 전력·사용률·온도·VRAM·클럭, CPU·RAM 실시간 그래프, 센서 지원 상태(미지원은 N/A) |
| 세션 | 작업 관리, 세션 시작·일시정지·재개·종료·중단, 지난 세션 직접 입력, 검색·필터, 세션별 전력 기준·가정 편집, 구간·공백·GPU별 에너지 |
| 수익성 | 수익(실제 수령·미수령·외부 보고·예상)과 비용 입력, 지급 내역 CSV 가져오기, 출처가 붙은 비용 내역, 순수익·시간당·kWh당·손익분기·회수 기간, 계산 조건과 이력 |
| 비교 | 세션별·작업 유형별·작업별·시간대별 정규화 비교, 실제 대비 예상 수익, 조건이 다를 때 경고 |
| 시나리오 | 낙관·기준·비관·사용자 정의, 민감도 그래프(손익분기점 표시), 조건별 영향 표, 원본 데이터 변경 추적 |
| 기록 | 일·주·월별 에너지(실측·추정 구분)·비용·수익·순수익, 센서 시계열, 기록 공백 |
| 설정 | 전기요금(유효 단가·누진 단계 추정), 전력 가정, 최대 샘플 간격, 장비 감가상각, 환율, 백업·복원·무결성 검사, CSV·JSON 내보내기, 글자 크기 |

## 계산 원칙

- 에너지는 전력 샘플을 **실제 타임스탬프로 사다리꼴 적분**합니다. 최대 샘플 간격(기본 10초)을 넘는 구간이나 값이 없는 구간은 **보간하지 않고 미관측으로 보고**합니다.
- 실측·사용자 입력·외부 보고·추정 값을 데이터 모델에서 구분하고, 합산할 때는 구성요소와 근거를 남깁니다.
- GPU 보드 전력은 PC 전체 전력보다 작습니다. PC 전체 전력은 외부 전력계 입력이나 시스템 추가 전력 가정으로 반영합니다.
- 미입력 수익은 0원이 아니라 "수익 미입력"이며, 미수령·예상 수익을 확정 수익에 넣지 않습니다.
- 계산할 수 없는 값(0시간, 0 kWh, 환율 없음, 손익분기점 없음 등)은 이유와 함께 N/A로 표시합니다.
- 한국 주택용 누진제는 가구 전체 사용량 등 조건 없이는 정확히 계산할 수 없으므로, 누진 단계 계산은 항상 "추정"으로 표시합니다.

자세한 식: [docs/CALCULATIONS.md](docs/CALCULATIONS.md)

## 문서

- [사용 방법](docs/USER_GUIDE.md)
- [계산 근거](docs/CALCULATIONS.md)
- [데이터 모델·데이터 보호·내보내기 형식](docs/DATA_MODEL.md)
- [프로젝트 A·C 재사용 기록](docs/REUSE.md)
- [검증 기록 (테스트·CI·실기 미검증 항목)](docs/VERIFICATION.md)
- [제3자 라이선스](THIRD-PARTY-NOTICES.md)

## 빌드

```
dotnet build GpuProfitCenter.slnx -c Release
dotnet test --project tests/GpuProfit.Tests -c Release
pwsh ./scripts/publish.ps1 -OutputDir dist        # Windows: 포터블 패키지 (GpuProfitCenter.exe, gpc.exe)
pwsh ./scripts/smoke-gui.ps1 -Exe dist/GpuProfitCenter-win-x64/GpuProfitCenter.exe
```

Linux에서도 WPF 프로젝트까지 컴파일되고 GUI를 뺀 모든 테스트가 실행됩니다. GitHub Actions(`.github/workflows/ci.yml`)는 Windows에서 빌드, 테스트, CLI·GUI 스모크 테스트, 포터블 패키지 생성을 합니다.

## 구조

```
src/GpuProfit.Core          도메인 모델(출처 구분), 설정, 경로, 데이터 폴더 잠금, 비밀값 차단
src/GpuProfit.Accounting    에너지 적산, 전기요금, 비용·수익·손익, 시나리오·민감도, 비교, 기간 집계 (순수 계산)
src/GpuProfit.Storage       SQLite 저장소, 스키마, 백업·복원, 보존 기간
src/GpuProfit.Recording     센서 기록기(A의 센서), 세션 서비스, 앱 엔진, 데모 데이터
src/GpuProfit.Export        CSV·JSON 내보내기, 지급 내역 CSV 가져오기
src/GpuProfit.Presentation  8개 화면 뷰모델, 표시 형식, 차트 모델
src/GpuProfit.App           WPF 앱 (GpuProfitCenter.exe), GUI 스모크 테스트
src/GpuProfit.Cli           명령줄 (gpc.exe)
vendor/local-ai-performance-center   프로젝트 A에서 가져온 소스 (매니페스트로 무결성 확인)
tests/GpuProfit.Tests       단위·통합·회귀·예외·뷰모델 테스트
```

## 라이선스

이 저장소의 라이선스는 저장소 소유자가 정하지 않았습니다(LICENSE 파일 없음). 사용한 오픈소스와 라이선스: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
