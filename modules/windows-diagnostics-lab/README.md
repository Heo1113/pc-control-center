# Windows Diagnostics Lab

Windows 11에서 일어나는 시스템 오류, 하드웨어 오류(WHEA), GPU 드라이버 재설정, 비정상 종료·BSOD, 서비스·프로그램 충돌,
드라이버·업데이트 변경을 **자동으로 수집하고 시간 순서로 엮어**, 사용자가 원인을 조사하도록 돕는 독립형 데스크톱 진단 프로그램입니다.

- 단순한 이벤트 로그 목록이 아닙니다. **언제, 무엇이 일어났고, 그때 시스템에 어떤 변화가 있었는지**를 타임라인·센서 기록·구성 스냅샷과 함께 보여 줍니다.
- 분석 결과는 항상 **확인된 사실 / 관측된 연관성 / 추가 검증이 필요한 원인 후보**로 나눠 표시하고, 원인을 확정하지 않습니다.
- 이벤트 ID만으로 판단하지 않습니다. 공급자(Provider)·채널·이벤트 ID·이벤트 데이터를 함께 보고, 규칙마다 "이 이벤트만으로 알 수 없는 것"을 함께 표시합니다
  (예: Kernel-Power 41은 파워서플라이 고장을 입증하지 않음). 규칙 목록: [docs/EVENT_CATALOG.md](docs/EVENT_CATALOG.md).
- **진단 프로그램이지 수리 프로그램이 아닙니다.** BIOS·레지스트리·드라이버·업데이트·이벤트 로그를 바꾸거나 지우지 않고, 관리자 권한을 요구하지 않습니다.
- GPU·CPU·RAM 센서는 이미 개발된 **프로젝트 A(Local AI Performance Center)의 센서 코드를 그대로 재사용**합니다([docs/REUSE.md](docs/REUSE.md)).

> **검증 상태**: 자동 테스트(Linux·Windows)와 Windows CI(Windows Server 2025)에서 실제 이벤트 로그 수집, WMI 스냅샷, 게시된 EXE의 GUI 스모크 테스트,
> 실행 중 강제 종료 후 기록 보존·재시작 확인을 통과했습니다.
> **사용자 PC(RTX 5080, Ryzen 7 9800X3D)와 실제 BSOD·WHEA 오류, Windows 11 클라이언트, 일반 사용자 계정으로는 검증하지 않았습니다.**
> 가상 데이터로 통과한 기능을 실기 검증으로 보지 마세요. 항목별 결과: [docs/VERIFICATION.md](docs/VERIFICATION.md)

## 주요 기능

| 영역 | 내용 |
|---|---|
| 오류 자동 수집 | System·Application·Setup·Kernel-PnP/Configuration 로그(설정에서 추가 가능)에서 WHEA(CPU·메모리·PCIe), GPU 드라이버 오류·TDR, 디스크·NVMe·NTFS, Kernel-Power 41·EventLog 6008, BugCheck, 서비스 충돌, 응용프로그램 충돌·응답 없음, WER, 드라이버 설치·로드 실패, Windows 업데이트 성공·실패, 부팅·종료·절전, 시스템 시각 변경, 로그 지움을 분류 |
| 실시간·과거 | 실행 중 새 이벤트를 주기적으로 확인(기본 10초), 처음 실행 때 과거 30일, 이후 원하는 기간을 추가로 가져오기. 재시작 후 이어서 수집(중복 저장 없음) |
| 알림 | 오류 이상(설정 가능) 새 이벤트를 화면 배너와 알림 영역 풍선으로 표시, 같은 문제 반복은 묶음 |
| 반복·새 오류 | 같은 공급자·ID·서명(프로그램/모듈/예외 코드, 서비스, 장치 등)으로 묶어 횟수·일수·추세 표시, '확인함' 이후 새 오류 구분 |
| 누락 기록 | 권한 부족·로그 없음·로그 덮어씀·로그 지움·로그 재생성·Windows 보관 기간 밖·앱 비정상 종료·센서 없음·시스템 시각 변경을 '수집 누락·제한'으로 기록 |
| 하드웨어 | 프로젝트 A의 NVML(GPU 사용률·온도·전력·VRAM·클럭·팬·스로틀 사유)과 Windows API(CPU·RAM) 센서를 기록. 없는 값은 N/A, 추정하지 않음. CPU 온도·메모리 전압은 관리자 권한/드라이버가 필요해 수집하지 않음 |
| 오류-센서 연결 | 이벤트 전후(기본 ±10분) 센서 그래프와 이전/이후 평균·최대, 그래프에 이벤트 세로선 표시. 그 시각에 앱이 기록 중이 아니었으면 그렇게 표시 |
| 구성 변경 | Windows 버전·빌드, BIOS/메인보드, CPU, 메모리 모듈(SMBIOS 보고 속도·전압), GPU·NVIDIA 드라이버, 저장장치, 장치 드라이버 목록, 핫픽스 스냅샷. 바뀌면 '이전 스냅샷 ~ 이번 스냅샷 사이'로 표시(정확한 시각을 추측하지 않음) |
| 사용자 기록 | BIOS 설정·EXPO·전압처럼 읽을 수 없는 변경을 직접 기록. 항상 '사용자 입력'으로 표시되어 측정값과 구분 |
| 기준선 비교 | 기준선 전후의 분류별 하루당 빈도와 정확 검정(p값), 새로 나타난/사라진 문제, 센서 통계, 구성 차이, 같은 기간의 다른 변경 |
| BSOD·충돌 | 다음 부팅 때 기록되는 Kernel-Power 41·6008·BugCheck 1001·WHEA 18·덤프 기록을 한 사건으로 묶고 BugCheck 코드·매개변수, 충돌 시각 근거, 마지막 확인 시각, 직전 30분 기록, 직전 센서, 덤프 헤더(읽기 전용)를 표시 |
| 상관관계 | 변경 시점 전후 빈도 변화, 무작위 기대치 대비 동시 발생, 충돌 직전 반복 기록, 고부하·고온 경향을 '관측된 연관성'으로, 지식 규칙에 따른 원인 후보를 근거 강도·검증 방법·주의와 함께 표시 |
| 저장·내보내기 | 앱 전용 SQLite(WAL, 동기화 FULL), 백업·검증·복원, CSV/JSON(이벤트·타임라인·센서·비교), HTML/Markdown 진단 보고서, 구성 비교, 진단 자료 묶음 |

## 화면

| 화면 | 내용 |
|---|---|
| 대시보드 | 최근 7일 상태 요약, 마지막 확인 이후 새 경고·오류와 '모두 확인함', 중요 경고(충돌·WHEA·GPU·앱·디스크·서비스), GPU·CPU·RAM 현재 값, 30일 오류 추이, 최근 충돌, 로그별 수집 상태 |
| 이벤트 탐색 | 검색어·기간·중요도·로그·공급자·분류 필터, 새 이벤트만, 같은 문제끼리 묶어 보기, 상세(의미·주의·이벤트에서 읽은 값·메시지·데이터), 유형별 메모, 전후 센서·타임라인으로 이동, CSV/JSON |
| 하드웨어 | 현재 값, 6개 그래프(실시간 60분·6시간·24시간·7일·30일, 이벤트 세로선), 이벤트 중심 보기와 이전/이후 통계, 장치·시스템 정보, 센서 지원 현황(관리자 권한 필요 여부) |
| 타임라인 | 시스템 변경·오류·프로그램 충돌·시스템 충돌·시작/종료·하드웨어 관측(기록 구간·누락)·사용자 기록을 날짜별로, 변경 기록 추가, 기준선 지정, CSV/JSON |
| 진단 | 원인 후보(검증 방법 포함)·관측된 연관성·확인된 사실 탭, 시스템 충돌 상세, 반복 문제, 수집 범위와 한계, HTML/Markdown 보고서와 진단 자료 묶음 |
| 비교 | 기준선 관리(직접 입력·지금·사용자 기록에서), 전후 기간 선택(3~90일), 빈도·p값·판단, 새로 나타난/사라진 문제, 센서·구성 차이, 다른 변경, 결과 저장·내보내기, 구성 스냅샷 직접 비교 |
| 설정 | 수집 로그·간격·과거 기간·제외 공급자, 과거 로그 가져오기, 센서, 보존 기간, 알림, 개인정보, 스냅샷·덤프, 분석 기준, 백업·검증·복원·무결성 검사, 데이터 폴더, 가상 데이터 |

## 설치와 실행 (Windows 11 x64)

대상은 Windows 11 x64입니다. Windows 10(2004 이상)도 빌드 대상에 포함되지만 실행을 확인하지 않았습니다.

1. GitHub Actions **CI** 실행 결과에서 아티팩트 `WindowsDiagnosticsLab-win-x64`를 내려받거나, 아래 "소스에서 빌드"로 만듭니다.
   내려받은 파일을 풀면 `WindowsDiagnosticsLab-win-x64.zip`(패키지)과 `WindowsDiagnosticsLab-win-x64.zip.sha256`(해시)이 나옵니다.
2. 패키지를 풀기 전에 해시를 확인합니다. 아래 결과가 `.sha256` 파일의 값과 같아야 합니다(대소문자 무시).
   ```powershell
   (Get-FileHash .\WindowsDiagnosticsLab-win-x64.zip -Algorithm SHA256).Hash
   Get-Content .\WindowsDiagnosticsLab-win-x64.zip.sha256
   ```
3. 패키지를 푼 폴더에서 `WindowsDiagnosticsLab.exe`를 실행합니다(각 EXE 옆에도 `.sha256` 파일이 있습니다). .NET을 따로 설치할 필요가 없습니다(자체 포함 단일 EXE). 서명되지 않은 EXE라 SmartScreen 경고가 나올 수 있습니다.
4. 처음 실행하면 과거 30일 이벤트를 가져오고(로그 양에 따라 수십 초), 구성 스냅샷을 저장하고, 센서 기록을 시작합니다.
5. 사용법은 [docs/USER_GUIDE.md](docs/USER_GUIDE.md)를 보세요.

명령줄 옵션: `--data-dir 폴더`(다른 데이터 폴더), `--mock-sensors`(테스트용 가상 센서), `--demo-data`(가상 시나리오 추가), `--history-days N`.

### 데이터 위치

- 포터블 모드(배포 패키지 기본): 실행 파일 옆 `portable.txt`가 있으면 `data\` 폴더에 저장합니다. 지우면 `%LOCALAPPDATA%\WindowsDiagnosticsLab`에 저장합니다.
- 내용: `wdl.db`(SQLite, WAL), `settings.json`, `logs\`, `backups\`, `exports\`.
- 같은 데이터 폴더는 한 프로그램(GUI 또는 `wdl`)만 쓸 수 있습니다. 실행 중에는 데이터 폴더의 `wdl.lock`을 잡고 있으며, 강제 종료되어도 Windows가 자동으로 풀어 줍니다.
- 손상된 DB는 삭제하지 않고 `wdl.corrupt-날짜.db`로 옮겨 보관한 뒤 새로 만듭니다.
- 보존: 이벤트는 기본 무기한, 센서 기록은 30일(설정 가능). DB 파일 자체를 지우는 기능은 없습니다.

## 개인정보와 안전

- 외부 서버로 로그를 보내지 않고, 외부 AI API를 호출하지 않습니다. 네트워크를 쓰지 않습니다.
- 수집할 때 사용자 이름, 컴퓨터 이름, 사용자 폴더 경로, 계정 SID, 이메일 주소, 비밀값(키·토큰·비밀번호 형태)을 가립니다(기본 켜짐). 원본 이벤트 XML은 저장하지 않습니다.
  이벤트 데이터의 장치 인스턴스 ID(USB 장치는 일련번호가 들어 있을 수 있음)는 진단에 필요해 그대로 저장하고, 최소 내보내기에서 줄입니다.
- 보안(Security) 로그와 방문 호스트 이름이 담기는 DNS-Client 이벤트는 수집하지 않습니다.
- 내보내기 기본값은 '개인정보 최소'(메시지·이벤트 데이터 제외, 장치 인스턴스 ID 단축). 상세 내보내기는 확인 창을 거치며 그때도 이름·경로는 다시 가립니다.
- 일반 사용자 권한으로 동작합니다(`asInvoker`). 읽을 수 없는 로그는 "권한 부족"으로 표시하고 필요한 권한(관리자 또는 Event Log Readers 그룹)을 설명합니다. 스스로 권한을 올리지 않습니다.
- 하지 않는 일: BIOS·전압·클럭 변경, 드라이버 설치·제거, 레지스트리 쓰기, 업데이트 제거, 복원점 삭제, 이벤트 로그 지우기, 덤프 파일 수정·삭제, 다른 프로그램(프로젝트 A, Strata)의 파일·DB 수정.

## 명령줄 도구 `wdl`

GUI와 같은 데이터 폴더·분석을 씁니다. GUI가 같은 폴더로 실행 중이면 `wdl`은 실행되지 않습니다(종료 코드 3).

```powershell
.\wdl.exe collect --days 30          # 수집 (처음이면 과거 30일), 구성 스냅샷, 덤프 확인
.\wdl.exe report --out report.html   # 진단 보고서 (사실·연관성·원인 후보)
.\wdl.exe export --out .\bundle      # 진단 자료 묶음
.\wdl.exe status --json              # 스크립트용: 무결성 검사, 누락 종류, 복구된 실행 포함
.\wdl.exe import-xml system.xml      # wevtutil qe System /f:xml 출력 가져오기 (이 PC의 수집 위치는 그대로)
.\wdl.exe backup --out backup.db
```

`wdl help`로 전체 명령을 볼 수 있습니다.

## 소스에서 빌드·테스트

필요: .NET SDK 10.0.1xx (`global.json`). Windows가 아니어도 WPF까지 컴파일과 테스트(Windows 전용 통합 테스트 제외)가 됩니다.

```powershell
dotnet build WindowsDiagnosticsLab.slnx -c Release
dotnet test --project tests/WinDiag.Tests -c Release --no-build
./scripts/publish.ps1 -OutputDir dist            # 포터블 패키지 (Windows)
./scripts/smoke-cli.ps1                          # 실제 로그로 CLI 확인 (Windows)
./scripts/smoke-gui.ps1 -Exe dist/WindowsDiagnosticsLab-win-x64/WindowsDiagnosticsLab.exe
```

## 구조

| 프로젝트 | 역할 |
|---|---|
| `WinDiag.Core` | 이벤트 모델, XML 파서, 분류 카탈로그·해석기, BugCheck/예외 코드표, 개인정보 마스킹, 설정, 스냅샷 모델. A의 센서 모델·로그 코드 포함 |
| `WinDiag.Storage` | 앱 전용 SQLite 저장소(이벤트·채널 위치·누락·실행·센서·스냅샷·사용자 기록·기준선·메모·덤프), 백업·복원. A의 SqliteHelpers 포함 |
| `WinDiag.Collection` | Windows 이벤트 로그 소스, 수집기, 센서 기록기(A의 HardwareMonitor), 레지스트리·WMI 스냅샷, 덤프 헤더, 알림, 엔진, 가상 시나리오. A의 Telemetry 코드 포함 |
| `WinDiag.Analysis` | 충돌 사건, 이벤트 전후 센서, 타임라인, 반복, 구성 비교, 기준선 비교(정확 검정), 진단 보고서(3단계). A의 Statistics 포함 |
| `WinDiag.Export` | CSV/JSON/HTML/Markdown 내보내기, 진단 자료 묶음, 카탈로그 문서. A의 CsvWriter 포함 |
| `WinDiag.Presentation` | 7개 화면의 뷰모델(플랫폼 중립, 테스트됨). A의 차트 모델·렌더러 포함 |
| `WinDiag.App` | WPF GUI (`WindowsDiagnosticsLab.exe`) |
| `WinDiag.Cli` | 명령줄 도구 (`wdl.exe`) |
| `vendor/local-ai-performance-center` | 프로젝트 A에서 가져온 원본 파일(커밋 고정, 해시 검증) |
| `tests/WinDiag.Tests` | xUnit 테스트(단위·통합·가상 시나리오·뷰모델·Windows 전용) |

## 프로젝트 A 재사용

A 저장소는 읽기 전용으로만 사용했습니다(수정·삭제 없음). 필요한 18개 파일을 커밋 `5b6aae1`에서 `vendor/`로 그대로 가져왔고,
로그 파일 접두어를 바꾸는 1개 파일만 최소 수정했습니다(패치 파일 보관). 테스트가 매번 해시로 원본 일치를 확인합니다.
C는 A가 설치되어 있지 않아도 실행되며, A의 데이터베이스를 읽거나 쓰지 않습니다. 자세한 내용: [docs/REUSE.md](docs/REUSE.md).

## 알려진 한계

- 실제 RTX 5080·Ryzen 9800X3D PC, 실제 BSOD·WHEA 오류, 실제 GPU 드라이버 재설정으로는 검증하지 않았습니다(CI 러너에는 NVIDIA GPU가 없습니다).
- CI는 Windows Server 2025의 관리자 계정에서 돌기 때문에, Windows 11 클라이언트와 일반 사용자 계정에서의 동작(예: 권한 부족 로그 표시)은 실제로 확인하지 못했습니다.
- 덤프는 헤더 메타데이터만 읽습니다. 원인 드라이버를 찾는 덤프 분석(WinDbg 수준)은 하지 않습니다. `C:\Windows\Minidump`는 보통 관리자 권한이 있어야 읽을 수 있어 일반 권한에서는 "권한 부족"으로 표시될 것으로 예상하지만, 일반 사용자 계정에서 확인하지는 않았습니다.
- 센서 값은 앱이 실행 중일 때만 기록됩니다. 그 밖의 시간대는 이벤트 로그만 있습니다.
- WHEA 구성 요소 판정과 일부 이벤트 데이터 필드 이름은 공개 자료와 이벤트 구조에 근거하며, 드라이버·Windows 버전에 따라 다를 수 있습니다(판정 근거가 화면에 표시됨).
- EventLog 6008의 종료 시각은 현지 형식 문자열이라 해석되지 않으면 원문만 표시합니다.
- 코드 서명, 설치 프로그램(MSI), 자동 업데이트는 없습니다.

## 라이선스

이 저장소의 라이선스는 저장소 소유자가 정하지 않았습니다(LICENSE 파일 없음). 사용한 오픈소스와 라이선스: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
