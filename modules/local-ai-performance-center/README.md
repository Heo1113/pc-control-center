# Local AI Performance Center

Windows 11에서 로컬 AI 추론 환경(우선 **Strata**)을 관측하는 독립형 데스크톱 프로그램입니다.
GPU·CPU·RAM 사용량과 AI 추론 성능을 실시간으로 보여 주고, SQLite에 장기간 기록해 **설정 변경·엔진 업데이트 전후**를 비교합니다.

- Strata를 수정·재패키징·실행하지 않습니다. 공개 HTTP API를 **읽기 전용(GET)** 으로만 조회합니다.
- Strata가 꺼져 있어도 하드웨어 모니터링과 과거 기록 조회는 그대로 동작합니다.
- **관리자 권한이 필요 없습니다.** GPU는 NVIDIA 드라이버의 공식 NVML, CPU·RAM은 Windows API로 읽습니다.
- 센서나 엔진이 주지 않는 값은 추정하지 않고 `N/A`로 표시합니다. 모든 값에 출처(실측 / 엔진 보고 / 계산값)를 붙입니다.
- 프롬프트 원문, 모델 응답, 인증 정보는 저장하지 않습니다.

> 검증 상태: 자동 테스트와 Windows CI(GitHub Actions, GPU 없는 가상 머신)에서 빌드·테스트·GUI 스모크 테스트를 수행합니다.
> **RTX 5080 실기에서는 아직 검증하지 않았습니다.** 자세한 내용은 [docs/VERIFICATION.md](docs/VERIFICATION.md)를 보세요.

## 화면

| 화면 | 내용 |
|---|---|
| 대시보드 | GPU 사용률·온도·전력/제한·VRAM·클럭, CPU·RAM, Strata 상태·모델·엔진 버전·답변/프롬프트 속도·요청·컨텍스트, 실시간 그래프 4개, 측정 시각과 갱신 상태 |
| 하드웨어 | GPU 코어/메모리 컨트롤러 사용률, 온도, 전력, VRAM, 클럭, PCIe, CPU, RAM 그래프(1·5·15·60분), 현재 값, 장치 정보, **센서 지원 표(관리자 권한 필요 항목 구분)** |
| AI 런타임 | 연결 대상·상태·오류 분류(연결 오류 / API 호환성 오류 / 인증 / 시간 초과 / 서버 오류)·안내, 서버·모델 정보, 기본 생성 설정, 실시간 추론 성능, 슬롯별 컨텍스트, 최근 요청(수치만), 요청 활동 |
| 기록 | 세션 목록(검색·기간), 세션 이름·태그·메모 편집, 통계 표(평균/최소/최대/P95/구간 변화율/기울기), 지표별 그래프(성능 저하 후보·클럭 제한 구간 표시), 엔진·모델 조건 이력, 이벤트·누락, CSV/JSON 내보내기, 삭제 |
| 비교 | 2~8개 세션 비교, 기준 세션 대비 변화율과 개선/저하 표시, **조건이 다른 비교 경고**(엔진 버전·모델·컨텍스트·설정·드라이버·전력 제한·기록 간격·MOCK), 경과 시간 기준 겹쳐 그리기, 내보내기 |
| 설정 | 기록 간격, 보존 기간, 저장 공간 상한, 자동 기록/자동 분할, Strata 주소·키 환경 변수·제한 시간, 분석 임계값, 데이터 폴더, 백업·복원·무결성 검사·보존 정책 적용·진단 정보 내보내기 |

## 설치와 실행 (Windows 11 x64)

1. GitHub Actions의 **CI** 실행 결과에서 아티팩트 `LocalAiPerformanceCenter-win-x64`를 내려받습니다
   (또는 아래 "소스에서 빌드"로 직접 만듭니다).
2. 압축을 풀고 `LocalAiPerformanceCenter.zip.sha256`의 해시와 비교합니다.
   ```powershell
   Get-FileHash .\LocalAiPerformanceCenter-win-x64.zip -Algorithm SHA256
   ```
3. 압축을 푼 폴더에서 `LocalAiPerformanceCenter.exe`를 실행합니다. .NET을 따로 설치할 필요가 없습니다(자체 포함 단일 EXE).
   - 서명되지 않은 EXE라 Windows SmartScreen 경고가 나올 수 있습니다.
4. 처음 실행하면 자동 기록 세션이 시작됩니다. 업데이트 전후처럼 이름을 붙여 기록하려면 아래 상태 표시줄의 **[수동 기록 시작]** 을 누릅니다.

### 데이터 위치

- 포터블 모드(기본 패키지): 실행 파일 옆 `portable.txt`가 있으면 `data\` 폴더에 저장합니다.
- `portable.txt`를 지우면 `%LOCALAPPDATA%\LocalAiPerformanceCenter`에 저장합니다.
- 폴더 내용: `lapc.db`(SQLite, WAL), `settings.json`, `logs\`, `backups\`, `exports\`.
- 명령줄 `--data-dir 경로`로 다른 폴더를 쓸 수 있습니다.

### Strata 연결

- 기본 주소는 `http://127.0.0.1:8080`입니다(`strata launch` 기본 포트). 설정에서 바꿀 수 있습니다.
- 사용하는 엔드포인트(모두 GET): `/v1/telemetry`(schema v1), 404일 때만 `/metrics` + `/v1/status`(구버전), `/health`, `/v1/status`, `/props`, `/v1/models`.
  `/status`는 마지막 답변 일부를 포함할 수 있어 호출하지 않습니다. 자세한 근거는 [docs/STRATA_API.md](docs/STRATA_API.md).
- 키는 **읽기 전용 텔레메트리 키**를 사용자 환경 변수 `STRATA_TELEMETRY_KEY`(이름은 설정에서 변경 가능)에 넣습니다.
  키 값은 이 앱의 설정·DB·로그·내보내기에 저장되지 않습니다. 텔레메트리 키로는 엔진 버전·모델 파일·기본 생성 설정·별칭(`/v1/status`, `/props`, `/v1/models`)을 Strata가 제공하지 않으므로 이 항목들은 화면에서 숨기고, 한 번 거부되면 다시 묻지 않습니다.
- 생성 중이 아닐 때는 답변·프롬프트 처리 속도 자리에 **마지막 완료 요청**의 값(엔진이 보고한 토큰 수와 시간으로 계산)을 출처와 함께 표시합니다.
- **포트 자동 찾기**(기본 켜짐): 설정한 주소가 응답하지 않으면 Strata 데스크톱 앱이 엔진에 쓰는 포트 `127.0.0.1:18500~18520`에서
  인증 없는 `/health`로 Strata를 찾아 연결합니다. 모델을 다시 로드해 포트가 바뀌어도 따라가며, 저장된 설정 주소는 바꾸지 않습니다.
- 원격 서버는 https만 허용합니다(평문 http는 127.0.0.1/localhost만).

#### Strata 데스크톱 앱에서 실시간 값 보기 (사용자 PC에서 연결 확인)

데스크톱 앱은 모델을 로드할 때마다 엔진 포트와 엔진 API 키를 새로 만들지만, 엔진은 시작할 때 환경 변수 `STRATA_TELEMETRY_KEY`를
읽기 전용 텔레메트리 키로 받습니다(Strata `serve/server.py`, `docs/TERMINAL.md`). 앱은 이 변수를 엔진에 그대로 넘깁니다.

1. 무작위 키를 사용자 환경 변수로 만듭니다(키 값은 화면에 나오지 않습니다).
   ```powershell
   $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Environment]::SetEnvironmentVariable("STRATA_TELEMETRY_KEY", (-join ($b | ForEach-Object { $_.ToString('x2') })), "User")
   ```
2. Strata 앱을 트레이까지 완전히 종료한 뒤 다시 실행하고 모델을 로드합니다.
3. 이 프로그램을 다시 시작합니다. 포트 자동 찾기가 엔진을 찾아 "연결됨"이 됩니다.

되돌리기: `[Environment]::SetEnvironmentVariable("STRATA_TELEMETRY_KEY", $null, "User")` 후 Strata 앱 재시작.
이 키는 `/v1/telemetry`만 허용하며 추론·관리 기능에는 쓸 수 없습니다. Serving API 키와 같은 값이면 엔진이 거부합니다.

### 명령줄 옵션 (GUI)

| 옵션 | 설명 |
|---|---|
| `--data-dir DIR` | 데이터 폴더 지정 |
| `--strata-url URL` | 이번 실행만 Strata 주소 변경 |
| `--no-auto-record` | 이번 실행에서 자동 기록 안 함 |
| `--mock-sensors` | 테스트용 가상 센서(화면에 MOCK 배너 표시, 기록에도 MOCK 표시) |
| `--smoke-test --smoke-report DIR` | CI용 자동 화면 점검 후 종료 |

## 권한과 안전

| 항목 | 일반 사용자 | 관리자 |
|---|---|---|
| GPU 사용률·온도·전력·제한·VRAM·클럭·팬·PCIe·에너지·클럭 제한 사유 (NVML) | 가능 | 불필요 |
| CPU 사용률, RAM (Windows API) | 가능 | 불필요 |
| CPU 온도, CPU 패키지 전력 (Ryzen) | 수집 안 함 | 커널 드라이버(예: PawnIO)와 관리자 권한이 필요해 이 버전에서는 제외 |

- 외부 네트워크 포트를 열지 않고, 외부 유료 API를 호출하지 않습니다. 연결 대상은 설정한 Strata 주소뿐입니다.
- `nvml.dll`은 System32(또는 NVSMI 폴더)에서만 불러옵니다(앱 폴더에서 DLL을 불러오지 않음).
- 비정상 종료에도 이미 기록한 데이터는 보존됩니다(WAL + synchronous=FULL, 다음 실행 때 열린 세션을 마지막 샘플 시각으로 닫음).
  손상된 DB는 삭제하지 않고 `lapc.corrupt-날짜.db`로 옮긴 뒤 새 DB를 만듭니다.

## 명령줄 도구 `lapc`

GUI와 같은 데이터 폴더·DB·설정을 씁니다. 소스에서 실행하는 예:

```bash
dotnet run --project src/LocalAiPerf.Cli -- sensors
```

```bash
dotnet run --project src/LocalAiPerf.Cli -- record --seconds 600 --name "Strata 0.2.9 Q4 ctx32k"
```

```bash
dotnet run --project src/LocalAiPerf.Cli -- compare --sessions 3,4 --out compare.json
```

명령: `sensors`, `strata`, `record`, `sessions`, `export`, `compare`, `backup`, `restore --yes`, `check`, `retention` (`--help`로 전체 옵션).

## 소스에서 빌드

필요: .NET SDK 10.0 (Windows 11 x64). Linux에서도 전체 솔루션 컴파일과 테스트가 됩니다(WPF는 컴파일만).

```bash
dotnet build LocalAiPerformanceCenter.slnx -c Release
```

```bash
dotnet test --project tests/LocalAiPerf.Tests -c Release
```

포터블 패키지(Windows, PowerShell):

```powershell
./scripts/publish.ps1 -OutputDir dist
```

Strata 없이 화면을 시험하려면 테스트용 가상 Strata 서버를 띄우고 그 주소로 연결합니다(가상 데이터이므로 실측 기록과 섞지 마세요).

```bash
dotnet run --project tests/LocalAiPerf.MockStrata -- --port 18080
```

## 구조

| 프로젝트 | 역할 |
|---|---|
| `LocalAiPerf.Core` | 모델, 지표 정의(출처 구분), 설정, 경로, 로그 |
| `LocalAiPerf.Telemetry` | 하드웨어 센서: NVML(GPU), Windows/Linux(CPU·RAM), MOCK. 다른 프로그램에서 재사용 가능 |
| `LocalAiPerf.Runtimes` | AI 런타임 어댑터 인터페이스와 Strata 읽기 전용 어댑터 (Ollama·llama.cpp는 같은 인터페이스로 추가 예정) |
| `LocalAiPerf.Storage` | SQLite 저장소, 보존 정책, 백업·복원, 손상 처리 |
| `LocalAiPerf.Recording` | 수집 루프, 세션 기록기, MonitoringService |
| `LocalAiPerf.Analytics` | 통계, 변화율, 성능 저하 후보, 스로틀 구간, 에너지/토큰, 세션 비교 |
| `LocalAiPerf.Export` | CSV/JSON 내보내기, 진단 정보 |
| `LocalAiPerf.Presentation` | 화면별 뷰모델(플랫폼 중립, 테스트 가능) |
| `LocalAiPerf.App` | WPF GUI (다크 Fluent 테마, ScottPlot 그래프) |
| `LocalAiPerf.Cli` | `lapc` 명령줄 도구 |
| `tests/` | xUnit 테스트, 가상 Strata 서버 |

사용한 오픈소스와 라이선스: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
이 저장소 자체의 라이선스는 저장소 소유자가 정하지 않았습니다(LICENSE 파일 없음).
