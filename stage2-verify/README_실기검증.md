# IRON Stage 2 실기 검증 키트 (사용자 PC)

이 키트는 **사용자 PC에서 직접** 실행합니다. 모든 단계는 읽기 전용입니다. 다음은 하지 않습니다.
- 프로세스 종료, 관리자 권한 상승(UAC) 요구, 드라이버 설치
- HWiNFO·WireView·BIOS 설정 변경, FLUX 송신, WireView USB 열기
- LAN 노출

> 메모리 안정성 테스트가 끝난 뒤에 시작하세요. 측정 중 다른 부하(게임·AI 작업)가 있으면 결과에 함께 적어 주세요.

## 0. 준비

1. 키트를 격리 폴더에 풉니다. 예: `C:\IRON\Stage2-Kit`
   - 기존 운영 폴더나 `main` 체크아웃과는 분리합니다.
   - Basalt(`127.0.0.1:8080`)는 건드리지 않습니다.
2. PowerShell 창에서 이 창에만 스크립트 실행을 허용합니다.
   ```powershell
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
   cd C:\IRON\Stage2-Kit\scripts
   ```
3. 키트 구성:

| 폴더 | 내용 |
|---|---|
| `IRON\Iron.App.exe` | 통합 빌드(단일 파일). 기본은 모의 데이터이고, `--source real`일 때 실제 데이터 |
| `hwinfo\` | HWiNFO REST 검증 도구 RC3.2: 수집기 `poc\Collect-HWiNfoRest.ps1`, `HWiNfoRestPoC.exe`, `config\`, `docs\` |
| `bench\Iron.ProcessIdentity.Bench.exe` | 식별 모듈 단독 측정·장시간(soak) 도구 |
| `scripts\` | 아래 절차의 스크립트 |
| `results\` | 결과가 쌓이는 곳(처음엔 비어 있음) |

## 1. HWiNFO REST — G1~G5 (RC3.2)

절차는 `hwinfo\docs\REAL_HARDWARE_VERIFICATION_RUNBOOK.md`를 그대로 따릅니다. 순서는 인벤토리 → 기준선 → G1 → G2 → G3A → G4S → G5입니다. 바뀐 점은 다음과 같습니다.

- **RC3.2 동작 변경:**
  - HWiNFO가 끊겼다가 돌아와도 밀린 샘플을 몰아서 요청하지 않습니다.
  - 주기 전체를 놓친 샘플은 `SKIPPED_LATE`로 기록하고, 실행 기록 `run_manifest.json`의 `skippedLate`에 개수를 남깁니다.
  - 수집 중 요청 시간 제한은 간격의 2배(1~5초)입니다.
- **.NET SDK가 없어도 됩니다.** 판정에 쓰던 `dotnet …HWiNfoRestPoC.dll analyze`는 다음으로 바꿔 실행합니다.
  ```powershell
  ..\hwinfo\HWiNfoRestPoC.exe analyze <수집 폴더> --root ..\hwinfo
  ```
- **G3A(WireView 단독 접근) 전 확인:**
  - WireView 제조사 앱과 iUnity는 **직접** 종료합니다. 스크립트는 종료하지 않습니다.
  - IRON도 WireView USB를 열지 않습니다. HWiNFO만 WireView를 읽어야 합니다.
- **G4-L1(12시간 경계):** 아래 5단계의 12시간 실행에 함께 기록됩니다.

## 2. IRON 실제 데이터 탐침 — 한글 MUI · WebView2 · 아이콘 · 민감정보

1. HWiNFO REST 서버를 켭니다(1단계와 같은 설정, `127.0.0.1:8764`).
2. WebView2 확인용으로 WebView2를 쓰는 앱을 하나 이상 엽니다. 예: Microsoft Teams, 위젯, Clipchamp, Outlook(새 버전).
3. 실행:
   ```powershell
   .\Run-IronProbe.ps1 -Seconds 30
   ```
4. `results\probe_<시각>\summary.md`에서 확인합니다.

| 항목 | 확인 기준 |
|---|---|
| 소스 상태 | NVML `Live`(GPU 2개), Windows CPU·RAM `Live`, WDDM `Live`, 식별 `Live`, HWiNFO REST `Live` |
| 실제 값 | GPU 0 = RTX 5080, GPU 1 = RTX 5060 Ti. CPU Tctl·NVMe 온도가 HWiNFO 센서 창과 같은 값. WireView 6핀이 모두 있음 |
| 한글 MUI | `cmd.exe → Windows 명령 처리기`처럼 한글 파일 설명이 나옴. 영어로만 나온 항목은 목록으로 확인 |
| svchost | 역할 칸에 한글 서비스 설명(예: "이 컴퓨터의 오디오를 관리합니다") |
| WebView2 | `WebView2 · <호스트 앱>` + 공유 호스트 `True` |
| 아이콘 | 일반 앱 대부분 아이콘 있음. 시스템 프로세스(권한 부족)는 글자 표시가 정상 |
| 민감정보 | "가려지지 않은 비밀값 후보 0개" |

## 3. 실제 프로그램 아이콘 — 화면으로 확인

```powershell
..\IRON\Iron.App.exe --source real
```

- 프로세스 탭에서 확인합니다.
  - 이름 셀: 아이콘 + 식별 이름 + 흐린 exe 이름. 추정이면 작은 `~`, 공유 호스트면 아이콘 오른쪽 아래 배지
  - 툴팁: 역할·근거
- 제목 표시줄의 "실측 데이터" 단추(비활성) 툴팁에 소스별 상태가 나옵니다.
- 전체 화면과 절반 화면(◧)을 각각 캡처해 결과에 넣어 주세요. 레이아웃은 Stage 1과 같아야 합니다.

## 4. (선택) 식별 모듈 단독 장시간 측정

```powershell
..\bench\Iron.ProcessIdentity.Bench.exe soak --minutes 720 --churn-seconds 5 --out ..\results\bench_soak.json
```

- 12시간 동안 5초마다 짧은 `cmd.exe`를 직접 띄워 식별·캐시 정리를 반복합니다. 이 도구가 띄운 것만 종료합니다.
- 마지막 줄에 핸들·GDI·메모리의 시간당 기울기(처음 10분 제외)와, 핸들 종류별 변화가 나옵니다. 판단 기준은 5단계와 같습니다.

## 5. IRON 12시간 연속 실행 (실제 데이터 창)

```powershell
.\Start-IronSoak.ps1
```

- 12시간 동안 평소처럼 PC를 씁니다. 트레이로 내려도 됩니다.
- 12시간 뒤 다음을 실행합니다.
  ```powershell
  .\Analyze-SoakLog.ps1 -Csv ..\results\soak_<시각>.csv
  ```
- 참고 기준(시간당 기울기, 처음 10분 제외): 핸들 ≤ 20, GDI ≤ 1, USER ≤ 1, Private ≤ 5 MB. 넘으면 "증가 추세"로 표시합니다.
- Private는 GC가 아직 수거하지 않은 메모리 때문에도 오릅니다. `전체GC후관리힙` 줄(마지막 전체 GC 뒤에 남은 관리 힙)이 평탄하면 누수가 아니라 수거 시점 차이로 봅니다. 이 경우 판정 줄에 따로 표시됩니다.
- HWiNFO 무료판이 12시간 뒤 REST를 멈추는지는 CSV의 `sources` 열과 "HWiNFO실측아님" 분 수로 확인합니다(G4-L1).

## 6. 결과 전달

```powershell
.\Collect-Results.ps1
```

- 공유 전에 직접 확인할 것:
  - `summary.md`의 "민감정보 자체 점검"
  - `probe.json`의 `commandLine` 값(IRON이 이미 가린 형태만 담김)
- 사용자 프로필 이름은 경로에서 지워집니다. 그래도 회사·프로젝트 폴더 이름처럼 공개하기 어려운 이름이 있으면 지우고 보내 주세요.
