# IRON Stage 1 — WPF 실기 체감용 실행 파일 (모의 데이터)

설계 패키지 `PCControlCenter-ProductSplit-DesignPackage-20261124 (3)`의 **4단 A+B 절충안**을 실제 `.NET 10 WPF` 프로그램(`Iron.App.exe`)으로 만든 단계입니다.
값은 모두 **모의 데이터**입니다. 실제 센서·USB 장치(Antec FLUX PRO, WireView Pro II)·레지스트리·데이터 파일에는 접근하지 않습니다.
Stage 2(백엔드 연결, 외부 장치 통합)는 사용자가 Stage 1 UI를 승인한 뒤에만 시작합니다.

## 실행

### 받은 파일로 실행

1. GitHub Actions의 **IRON Stage 1** 실행 결과에서 `Iron.App-win-x64` 아티팩트를 내려받아 압축을 풉니다.
2. `Iron.App.exe`를 실행합니다. 단일 파일이며 .NET을 따로 설치하지 않아도 됩니다(자체 포함, win-x64).
   - 서명되지 않은 파일이라 SmartScreen이 경고할 수 있습니다. 같은 폴더의 `Iron.App.exe.sha256`으로 해시를 확인할 수 있습니다.
3. 처음에는 창이 모니터 작업 영역을 가득 채웁니다. 제목 표시줄의 `◧` 버튼이나 제목 표시줄 두 번 클릭으로 절반 배치로 바뀝니다.

### 소스에서 빌드해 실행

```powershell
git switch feat/iron-ui-stage1
```

```powershell
dotnet run --project src/Iron.App -c Release
```

```powershell
./scripts/iron-publish.ps1 -OutputDir dist
```

마지막 명령은 `dist\Iron.App-win-x64\Iron.App.exe`를 만듭니다. .NET SDK 10이 필요합니다.

### 명령줄 옵션

| 옵션 | 설명 |
|---|---|
| `--tab overview\|processes\|power\|devices\|settings` | 시작 화면 |
| `--layout full\|half` | 작업 영역 전체 / 절반(오른쪽) |
| `--mock-state normal\|alert\|disconnected\|loading` | 시작 모의 상태 |
| `--usb-released` | WireView USB 해제 상태로 시작 (모의) |
| `--tray` | 창 없이 트레이에서 시작 |
| `--no-jitter` | 모의 값 흔들림 없이 설계 값 그대로 |
| `--render-screens DIR` | 모든 화면을 2194×1186 · 1097×1186 DIP, 175 %로 그려 PNG와 레이아웃 점검 보고서(`layout-report.json`)를 만든 뒤 종료 |
| `--smoke-test DIR` | 실제 창으로 탭·상태·USB 해제·반폭·트레이·닫기를 차례로 실행하고 보고서(`smoke-report.json`)를 만든 뒤 종료 |

같은 사용자 세션에서 두 번째로 실행하면 새 창을 열지 않고 이미 실행 중인 IRON 창을 앞으로 가져옵니다.

## 모의 상태 전환 테스트

상태는 제목 표시줄의 **`모의 데이터 ▾`** 버튼, **설정 › 모의 데이터** 영역, 단축키로 바꿉니다.

| 상태 | 방법 | 확인할 것 |
|---|---|---|
| 정상 | `F5` 또는 [정상] | 올리브 LED + `ONLINE`, 적색 없음, GPU 보드 전력 `126 W` |
| 오류/불균형 | `F6` 또는 [오류/불균형] | 곧바로 RTX 5080 온도(76°C)와 사용률 막대만 적색, LED `GPU 온도 주의`. WireView P3(7.94 A) 막대는 즉시 적색, 요약줄에 `확인 중 1/3`. **3회(3초) 연속**이면 LED가 `핀 불균형 · GPU 온도 주의`로 바뀜. RTX 5060 Ti 전력 `N/A`, GPU 보드 전력 `일부 미확인`, 시간당 요금 `미합산`. 배너·적색 테두리 없음 |
| 미연결 | `F7` 또는 [미연결] | 회색 LED + `미연결`, GPU 값 `—`, 전력 추세는 빈 축 없이 `측정값 없음` |
| 로딩 | `F8` 또는 [로딩] | 앰버 LED + `센서 초기화 중...` |
| USB 해제 | `F9`, FLUX 바의 [Release USB], 전력 · WireView의 [RELEASE USB], 트레이 팝업 | `해제 중...` 뒤 `USB 해제됨`. **핀 경고 색은 즉시 사라지고 GPU 온도 경고는 그대로 남음**(독립 검수 반영). 다시 누르면 `RESUME`으로 재연결, 핀 경고는 새로 3회 확인 |
| 1.5초 불균형 플래시 | `Shift+F9` 또는 [1.5초 불균형 플래시] | P3 막대만 1.5초 적색으로 깜박이고 LED는 `ONLINE` 그대로 (3회 연속이 아니라 경고로 굳지 않음) |

개별 조건(RTX 5080 과열, 핀 불균형, 5060 Ti 전력 누락, FLUX PRO 미연결, iUnity 충돌, WireView 포트 사용 중)은 서로 독립된 스위치입니다. 예를 들어 RTX 5080 과열과 USB 해제를 함께 켜서 GPU 경고가 유지되는지 볼 수 있습니다.

**UI 주의 기준**은 설정에서 바꿉니다. 입력하는 즉시 반영됩니다.
- GPU 온도: 55~95 °C의 정수만 받습니다. 기본값은 72입니다.
- WireView 핀 전류: 4.0~9.0 A이고 소수점 한 자리까지 받습니다. 기본값은 7.0입니다.

범위를 벗어난 값, 숫자가 아닌 값, 허용 자릿수를 넘는 값을 넣으면 입력 칸이 적색이 되고 이유가 표시됩니다. 이때 직전의 올바른 값이 그대로 유지됩니다.

| 단축키 | 동작 |
|---|---|
| `Ctrl+1`~`Ctrl+5` | 개요 · 프로세스 · 전력 · WireView · 외부 장치 · 설정 |
| `F5` `F6` `F7` `F8` | 정상 · 오류/불균형 · 미연결 · 로딩 |
| `F9` / `Shift+F9` | USB 해제·재개 / 1.5초 불균형 플래시 |

## 독립 검수 반영

| 지적 | 반영 |
|---|---|
| 반폭 시스템 요약 3+1 배치 | 1200 DIP 이상과 반폭(960~1199 DIP)은 4열, 960 DIP 미만은 2×2. 3+1은 나오지 않음(`LayoutRules.SystemColumns`, 테스트) |
| 반폭 프로세스·WireView 배치 | 1097 DIP에서는 나란히 배치(60:40)합니다. 표의 다섯 열이 잘리지 않고 핀 막대도 전체 높이를 유지하기 때문입니다. 위아래로 쌓으면 핀 막대가 절반 높이가 되므로 960 DIP 미만에서만 쌓습니다 |
| GPU 온도 경고 / WireView 핀 경고 독립 | `AlertMonitor`가 두 경고를 따로 관리합니다. USB 해제나 WireView 미연결은 핀 경고만 지웁니다(테스트 `Releasing_the_wireview_usb_clears_only_the_pin_warning_at_once`) |
| UI 주의 기준 범위 검증 | `NumericSetting`: 범위·숫자 형식·소수 자릿수 검사, 잘못된 값은 적용하지 않음(테스트 `ThresholdTests`) |
| 자동 실행·USB 해제·외부 장치 제어는 모의 | Run 키·작업 스케줄러 등록 없음, COM 포트·HID 장치 열지 않음. 화면에 `(모의)` 표시 |
| 작업 영역·DPI 대응, 크기 하드코딩 금지 | 창 위치는 그 창이 있는 모니터의 작업 영역(`MonitorFromWindow`/`GetMonitorInfo`)에서 계산합니다. 전체 = 작업 영역 크기로 최대화(`WM_GETMINMAXINFO`), 절반 = 작업 영역의 왼쪽 또는 오른쪽 절반입니다. 모니터·DPI·작업 표시줄이 바뀌면 다시 배치하고, 레이아웃은 창 폭으로 전환합니다. 2194×1186·1097×1186은 렌더 점검의 입력값으로만 씁니다 |

## 구조

| 경로 | 내용 |
|---|---|
| `src/Iron.App/Presentation` | WPF 비의존: 모의 데이터(`MockTelemetryProvider`), 경고(`AlertMonitor`, `StatusRules`), 입력 검증(`NumericSetting`), 전력 합계(`PowerTotal`, PARTIAL), FLUX 패킷 미리보기, 뷰모델 |
| `src/Iron.App/Theme` | `IronTokens.xaml`(tokens/iron.tokens.json), 컨트롤 스타일, 데이터 템플릿, 전체/컴팩트 치수 |
| `src/Iron.App/Controls` | 분절 LED 바, 핀 미터, 추세 그래프, 스파크라인(픽셀 정렬), 간격 그리드·비율 분할·맞춤 스택 패널 |
| `src/Iron.App/Views` | 셸(44 DIP 제목 표시줄), 개요·프로세스·전력·외부 장치·설정, 트레이 팝업 |
| `src/Iron.App/Hosting` | 작업 영역·DPI, 트레이 아이콘, 오프스크린 렌더·레이아웃 점검, 실제 창 스모크 |
| `tests/Iron.App.Tests` | 화면 로직 테스트 (Linux·Windows) |
| `.github/workflows/iron-stage1.yml` | 이 브랜치 전용 CI: 빌드·테스트·`Iron.App.exe` 게시·175 % 화면 렌더·실제 창 스모크 |

기존 `main`, `PcControlCenter.*` 프로젝트, 모듈 소스, `ci.yml`, `PcControlCenter.slnx`는 이 브랜치에서 바꾸지 않았습니다.

## 화면 크기별 배치

| 창 폭 (DIP) | 배치 | 예 |
|---|---|---|
| 1200 이상 | GPU 카드 2열, 시스템 요약 4열, 프로세스 64 % · WireView 36 % | 4K 175 % 최대화 2194 DIP, 1920×1080 100 % |
| 960 ~ 1199 | GPU 카드 세로 2단, 시스템 요약 4열, 프로세스 60 % · WireView 40 %, 글자·여백 축소 | 4K 175 % 반폭 1097 DIP |
| 720 ~ 959 | 시스템 요약 2×2, 프로세스·WireView 위아래, 탭 이름 축약(`전력`, `장치`), PID·엔진 열 숨김 | 작은 화면의 반폭 |

- 창 높이가 모자라면 세로로만 스크롤합니다(가로 스크롤 없음).
- 개요의 프로세스 표는 패널에 다 들어가는 행만 보여 줍니다. 반쯤 잘린 행이나 스크롤 막대는 나오지 않습니다.
- 최대화 상태에서 Windows는 창 테두리 몇 픽셀을 모니터 밖에 둡니다. 그만큼 내용을 안쪽으로 들여서 보이는 영역을 작업 영역과 정확히 맞춥니다.
- 반폭 배치는 작업 영역의 절반이 창 최소 폭(720 DIP)보다 좁으면 최소 폭으로 하되, 작업 영역 밖으로 나가지 않게 합니다.

## 확인 결과

GitHub Actions의 **IRON Stage 1** 워크플로(`.github/workflows/iron-stage1.yml`)에서 확인합니다. 결과물은 실행마다 두 아티팩트로 올라갑니다.
- `Iron.App-win-x64`: 실행 파일 zip과 SHA-256
- `iron-stage1-screens`: 스크린샷과 보고서

| 항목 | 결과 |
|---|---|
| `dotnet build Iron.slnx -c Release` (Linux, Windows) | 경고 0, 오류 0 |
| 화면 로직 테스트 `tests/Iron.App.Tests` (Linux, Windows) | 48개 통과 |
| `Iron.App.exe` 게시 (win-x64 자체 포함 단일 파일) | 생성됨 (zip 약 64 MB) |
| 오프스크린 화면 30장: 2194×1186·1097×1186 DIP × 175 % (3840×2076·1920×2076 px), 5개 화면 × 상태 14종 + 트레이 | 레이아웃 점검(글자 잘림·영역 밖·가로 스크롤·텍스트 겹침) 0건, 바인딩 오류 0건 |
| 실제 창 스모크(러너 바탕화면, 1024×768 · 100 %): 5개 화면, 4대 상태, USB 해제·재개, 1.5초 플래시, 전체·반폭, 트레이 팝업, 닫기 → 트레이, 다시 열기 | 21단계 모두 진행, 레이아웃 문제 0건, 바인딩 오류 0건. 최대화 시 보이는 내용 = 작업 영역(1024×720), 반폭 = 작업 영역 안 |

**스크린샷에 대한 주의:** 4K 175 % 화면은 `Iron.App.exe`가 실제 WPF 화면을 175 %(168 DPI)로 그린 것입니다(`--render-screens`). 사용자 모니터를 직접 캡처한 것은 아닙니다.
실제 창 동작은 러너의 1024×768 화면에서만 확인했습니다.
사용자 PC(Windows 11, 4K 175 %, 다중 모니터)에서의 사용감·선명도·트레이 동작은 Stage 1 UAT에서 직접 확인해야 합니다.

## Stage 1에서 하지 않은 것

- 실제 센서(NVML, WDDM PDH, CPU/RAM/SSD), Antec FLUX PRO HID 송신, WireView Pro II COM 포트, 시작 프로그램 등록, 설정·DB 파일 저장은 하지 않습니다. 모두 Stage 2 범위이며, 사용자 승인 뒤에만 시작합니다.
- 기존 `main`, 백엔드(`PcControlCenter.*`, `modules/`), `ci.yml`, 운영 배포 폴더는 바꾸지 않았습니다.
