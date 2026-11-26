# CHANGELOG — IRON Stage 2 HWiNFO REST API 독립 조사 및 검증 패키지

## [RC3.2] - 2026-11-26 — 연결 끊김 중 요청 누적 수정

통합 브랜치 `feat/iron-stage2-integration`에서 수정했습니다. `rc3/windows-ci-check`는 그대로 둡니다.

### 원인 (수정 전 코드 기준, 시간표는 CI `tests/disconnect_timeline.py`)
1. **절대 일정 + 따라잡기:** PoC `live`와 `Collect-HWiNfoRest.ps1` 모두 샘플 i를 `(i-1)×간격`에 맞추고, 이미 늦었으면 기다리지 않고 바로 보냈다. 앞 요청이 오래 걸리면 밀린 샘플이 재연결 직후 연달아(한 간격 안에 여러 개) 나가, 같은 순간의 값이 여러 샘플로 기록되고 HWiNFO에 순간 부하가 몰린다.
2. **긴 시간 제한:** 요청 시간 제한이 5초(연결 시간 제한 없음)라 서버가 꺼진 동안 요청 하나가 실패할 때까지 여러 주기를 붙잡았다. Windows는 닫힌 포트에 연결할 때 RST를 받고도 다시 시도해 실패까지 시간이 걸린다(실측값은 CI 로그 `refused connect`).

### Fixed
- 샘플 주기 전체가 이미 지났으면 요청하지 않고 `SKIPPED_LATE`로 기록(샘플 수·시각 정렬 유지, 따라잡기 없음). 실행 기록에 `skippedLate` 개수.
- 수집 루프의 요청 시간 제한 = 간격의 2배(1~5초). PoC는 연결 시간 제한(간격, 0.25~1초)을 따로 둠. 준비 단계(ping·SDB)는 5초 유지.
- PoC: 실패한 요청도 시작·끝 시각을 기록(수집기와 같게) — 실패까지 걸린 시간을 보고서에서 볼 수 있음.
- PoC `probe-refused <url>`: 닫힌 루프백 포트에 GET이 실패하기까지 걸리는 시간을 RC3/RC3.2 설정으로 잰다(읽기 전용).

### Verified (Windows 러너, 모의 서버 = SYNTHETIC, 같은 끊김 시나리오: 500 ms × 24샘플, 수집 시작 2.8초 뒤 서버 종료, 4.4초 뒤 재시작)

| 클라이언트 | RC3 (수정 전) | RC3.2 (수정 후) |
|---|---|---|
| PoC (.NET) | 재연결 직후 18 ms 안에 요청 7개 | 몰림 없음(한 간격에 1개), 실패 7건 각 0.5초 |
| 수집기 PS 5.1 | 216 ms 안에 8개, 요청 하나가 4,040 ms 붙잡음 | 몰림 없음, 실패 4건 각 1.0초, 건너뜀 4개 |
| 수집기 PS 7 | 53 ms 안에 7개, 실패 하나 2,019 ms | 몰림 없음, 실패 4건 각 1.0초, 건너뜀 4개 |

- 닫힌 루프백 포트에 GET이 실패하기까지: .NET 기본 2.0초, PS 5.1(TimeoutSec 5) 2.1초, PS 7 2.3초, Python 2.0초 · RC3.2 연결 제한 0.5초.
- RC3 Windows 묶음 22/22 통과(PS 5.1·7, PoC 정상·오류·끊김·재연결).

## [RC3.1] - 2026-11-26 — Windows 러너 호환성 검증 반영

임시 브랜치 `rc3/windows-ci-check`의 GitHub Actions Windows 러너(Windows PowerShell 5.1.26100, PowerShell 7.6.10, .NET 10, 로캘 ko-KR·de-DE)에서 **모의 HWiNFO 서버(SYNTHETIC)**로 검증했습니다. 실제 하드웨어 측정이 아닙니다. `main`·`feat/iron-ui-stage1`은 변경하지 않았습니다.

### Fixed (검증 중 발견)
- **Baseline 모드가 실행되자마자 실패**: 지역 변수 `$samples`가 매개변수 `[int]$Samples`와 같은 변수(PowerShell 변수 이름은 대소문자 무시)여서 배열 대입이 형 변환 오류를 냈음. 실기 절차 3단계에서 그대로 막혔을 결함 → `$cpuSamples`로 변경.
- **Baseline 측정 중 "REST 서버가 켜졌다" 오판**: `@(Get-Listener …)`가 빈 배열을 원소 1개로 셈 → 반환값을 그대로 비교.
- **CPU 측정 경로**: 출처가 `LIVE_CAPTURE`일 때만 HWiNFO CPU를 쟀음 → 포트 소유자가 HWiNFO로 확인되면 항상 측정(HWiNFO가 아닌 소유자는 그 전에 중단).
- 기준선 파일이 없으면 수집 폴더를 만들기 전에 중단.
- PoC `live`: 응답에 SYNTHETIC 표시가 있으면 출처를 SYNTHETIC으로 기록(수집기·HTML과 같게).

### Verified (Windows, 모의 서버)
- PS 5.1: 정책 거부(8080), 비-HWiNFO 소유자 중단, Inventory(포트 소유자·버전·°C 보존·한글), Baseline, Collect 정상(CPU 표본·오버헤드 계산), G3A 형식, 503·잘린 JSON, SDB Version 2 거부, Analyze, 연결 해제·재연결, WireView 클라이언트 감지(코드 5, 프로세스 유지).
- PS 7: Windows 전용 경로(소유자·CPU·PnP·레지스트리)와 연결 해제·재연결, WireView 감지.
- PowerShell G1 == C# analyze G1 (모든 수집 폴더), de-DE 로캘에서도 JSON 숫자·G1 일치.
- C# PoC: selftest(JS 기대값 일치), live 정상, live 연결 해제·재연결(종료 코드 3, 표본 누락 없음).

### Known (미수정, 승인 시 수정)
- 연결이 끊긴 동안 요청 하나가 끊긴 시간 내내 막히고, 재연결 뒤 밀린 표본을 간격 없이 몰아서 보냄. 실패가 1건이라도 있으면 G1·G3A는 미달이므로 판정은 안전하지만, 실패 수가 끊긴 시간보다 적게 기록됨. 수정안: 일정이 한 간격 이상 밀리면 다시 기준을 잡고 건너뛴 칸 수를 기록.

## [RC3] - 2026-11-24

RC2 독립 검수의 잔여 결함 10건을 고쳤습니다. Stage 1(`feat/iron-ui-stage1`), `main`, 기존 백엔드, 하드웨어 보호 설정은 변경하지 않았고 격리 폴더에서만 작업했습니다. **실측은 하지 못했습니다**(사용자 PC 미연결) — G1~G5는 모두 미검증이며 `results/`에는 합성 자체 시험 출력만 있습니다.

| # | RC2 결함 | RC3 수정 | 확인 방법 |
|---|---|---|---|
| 1 | UI 기준이 Stage 1과 다름(7.5/9.0 A, 35 %, 75 °C) | Stage 1 소스에서 추출한 72 °C·7.0 A·0.72·3회만 사용(`config/iron_stage1_thresholds.json`). Stage 1에 없는 불균형 %·커넥터 온도 기준 삭제 | `tools/extract_stage1_thresholds.py` 비교, 테스트 |
| 2 | 핀 일부만 있어도 `OK` | 6/6일 때만 `COMPLETE`, 아니면 `INCOMPLETE (n/6)`, 센서 없으면 `N/A`. 편차는 6핀일 때만 | Node·.NET 같은 기대값, 합성 RC2 픽스처 |
| 3 | GPU 합계 부분 누락을 합산, 핫스팟을 코어 대신 사용 | 일부 GPU 전력이 없으면 `PARTIAL`(합계 없음). 코어·핫스팟·메모리 정션 최대값 분리, 경고는 코어만 | 같은 |
| 4 | STALE을 브라우저 시계로만 판정 | HWiNFO `Timestamp ms` 기준: ping 시계 보정 나이 > 6 s 또는 타임스탬프 정지 > 6 s → STALE, `RS≤TS≤RE` 위반·역행 → INVALID | `seq_frozen`·`seq_age`·`seq_invalid` |
| 5 | 오프라인·합성 JSON을 `LIVE`로 표시 | 출처 5종 구분, HTML은 SNAPSHOT 모드(이력 없음)로 분리, 모의 서버 응답은 LIVE 경로라도 SYNTHETIC 표시 | 브라우저 렌더 확인 |
| 6 | PS 통계와 PASS 기준 불일치, STUCK 정의 오류 | `config/gates.json` 단일 기준, PS·C#가 같은 G1 정의(최근접 순위 P95 등). 정지는 서로 다른 타임스탬프 기준 | PS G1 == C# G1(합성 폴더·모의 서버 수집) |
| 7 | CPU 오버헤드 기준선 없음 | `-Mode Baseline`(REST 꺼진 상태)과 수집 중 평균의 차. 기준선 없으면 UNVERIFIED | 합성 폴더 0.44 %p |
| 8 | REST 센서 감지만으로 `HWINFO_OWNED` | 소유권 기본 `UNCONFIRMED`, 운영자 확인 + 다른 클라이언트 0 + PnP 기록일 때만 `OPERATOR_ATTESTED`. 다른 WireView 클라이언트가 있으면 수집 중단(코드 5) | 가짜 프로세스 시험 |
| 9 | 이름 정규식만으로 매핑 | 이름은 후보, 채택은 SDB `Unit` 일치일 때만. 거부 사유·후보·채택을 모두 보고, 소수점 쉼표 의심 값 검출 | `synthetic_mismatch` |
| 10 | 기존 백엔드 가정 미확인 | `main` 소스 대조: WireView 직접 USB·FLUX 송신 "보유"는 **사실 아님**, CPU 온도 미수집, 핫스팟 미수집 등 | `docs/BACKEND_FACT_CHECK.md` |

### 추가·변경
- `poc/Collect-HWiNfoRest.ps1` 새로 작성(Inventory·Baseline·Collect·Analyze, PS 5.1/7, UTF-8 BOM). RC2 `poc/Test-HWiNfoRestApi.ps1`은 대체되어 제외.
- `.NET` PoC를 `selftest`·`live`·`analyze`로 재구성(경고 0). `analyze`가 `gate_report.md/json`, `sensor_inventory.csv` 생성.
- 릴레이: LAN(`--allow-lan`) 비활성, 상류 주소 루프백만, `/api/ping` 캐시 안 함.
- RC2 `assets/iron-charts.js`(쓰이지 않던 중복 파서) 제외.
- HWiNFO 버전·12시간 제한·자동 시작은 공식 사이트 접속 불가로 2차 출처만 확인(`docs/HWINFO_SPEC_CHECK.md`).

## [RC2] - 2026-11-25

독립 검수에서 지적된 10개 필수 수정 사항을 전면 반영한 Release Candidate 2입니다. 기존 Stage 1 구현, `main` 브랜치, Basalt(`127.0.0.1:8080`) 운영 설정, 하드웨어 보호 설정 및 프로젝트 정본은 변경하지 않았습니다.

### Fixed & Changed (10개 검수 항목 대응)

1. **포트 통일 및 충돌 보호 (`8764` / `8765`):**
   - Basalt가 점유 중인 `127.0.0.1:8080`과의 충돌을 원천 회피하기 위해 전 문서·스크립트·PoC에서 HWiNFO 후보 포트를 **`8764`**, IRON Remote 중계 포트를 **`8765`**로 통일.
   - PowerShell, .NET 10 PoC, Python 중계 서버에 실행 전 포트 점유 및 PID 확인 로직 추가. 충돌 시 기존 프로세스를 종료·변경하지 않고 즉시 안전 중단(Exit Code `1`).
2. **IRON Remote 상태 머신 엄격 분리 (`SIMULATION` / `LIVE` / `OFFLINE` / `STALE`):**
   - `LIVE` 모드 진입 시 시뮬레이션 생성기를 완전 비활성화하고 `simHistory`와 `liveHistory`를 분리하여 그래프·카드 잔존 시뮬레이션 데이터를 제거.
   - 실제 HWiNFO `/api/sensors/read` 응답을 메인 카드·듀얼 GPU·WireView 6핀·NVMe·실시간 그래프에 직접 바인딩하며, 매핑되지 않은 센서(프로세스별 VRAM, Antec HID 등)는 명시적으로 `N/A` 표기.
3. **WireView Pro II 장치 소유권 분리 및 자동 USB 폴백 금지:**
   - WireView REST 연결 실패를 USB 점유 해제로 간주하지 않도록 명시하고 자동 USB 폴백을 전면 금지.
   - 데이터 공급자와 별도의 장치 소유권 상태 머신(`NATIVE_USB_OWNED` / `HWINFO_OWNED` / `RELEASED_FOR_VENDOR_SW` / `UNOWNED`) 정의.
   - X870E USB 단절 이슈를 고려해 **Phase A(공급자별 단독 접근)**부터 검증하고 **Phase B(동시 접근)**는 별도 승인 대상으로 분리.
4. **PowerShell 진단 스크립트(`Test-HWiNfoRestApi.ps1`) 결함 수정 및 고도화:**
   - `-like "t*"` 와일드카드 제외 버그(`Temperature`, `Total Power`, `Tctl/Tdie` 등 누락 유발)를 정확한 3개 메타데이터 키(`tSensorLocation`, `tReadingLocation`, `tReadingType`) 제외 방식으로 수정.
   - Read↔SDB 교차 매핑 검증, 전체 연속 샘플 저장(`samples_all.jsonl`), 타임스탬프 단조성 검증, Min/Avg/P95/Max 통계, 값 고정(`STUCK`) 감지, `HWiNFO64` CPU/RAM 오버헤드 계측, 명시적 종료 코드(`0`~`4`), 고유 실행별 결과 폴더(`run_yyyyMMdd_HHmmss_<id>`) 추가.
5. **Remote 비동기 폴링 안정성 및 XSS 방지:**
   - Single-flight 제어, `AbortController` 기반 타임아웃·취소, 단조 증가 시퀀스 번호 기반 지연 응답 폐기 적용.
   - API 응답 및 업로드된 JSON 데이터의 `innerHTML` 직접 삽입을 전면 제거하고 `createElement` + `textContent` DOM API로 전환.
6. **외부 CDN 제거, 로컬 정적 리소스 포함, 중계 서버 보안 강화 및 부정 테스트:**
   - HTML에서 Tailwind/Chart.js/Lucide 외부 CDN 의존성 전면 제거(순수 CSS + 자체 Canvas 차트 엔진 + 인라인 SVG + `assets/` 로컬 리소스 포함).
   - `hwinfo_relay_poc.py` 기본 바인딩을 `127.0.0.1` 루프백으로 제한하고 LAN 노출은 `--allow-lan` 명시 시에만 허용.
   - 고정 테스트 토큰 제거 → `secrets.token_urlsafe(24)` 실행별 무작위 토큰 자동 생성.
   - 인증 실패(`401`), 쓰기 차단(`403`), 미존재 경로(`404`), 잘못된 `Version` 거부, 파서/값 고정 감지를 검증하는 자동화 테스트(`tests/test_relay_and_parser.py`) 추가.
7. **대상 PC 모의 하드웨어 프로필 반영 및 실기 동적 장치 감지:**
   - `SIMULATION` 모드를 **Ryzen 7 9800X3D**, **RTX 5080 16GB + RTX 5060 Ti 16GB**, **RAM 128GB**, **다수 NVMe SSD(4개)** 환경에 맞춤.
   - `LIVE` 모드에서는 하드코딩 없이 SDB/Read 응답에서 GPU·NVMe·DIMM·WireView 센서를 동적으로 감지·렌더링.
8. **WireView UI 주의 기준 통일 및 합성 샘플 표기 명확화:**
   - WireView UI 주의 기준은 Stage 1 확정 설정과 통일(동기화 대상 명시, 하드웨어 보호 설정 무변경).
   - 모든 합성 JSON 픽스처(`tests/fixtures/*.json`) 및 HTML 내장 샘플을 `SYNTHETIC TEST FIXTURE (실측 아님)`로 명시.
9. **.NET 10 타깃 전환, 가정/사실 분리, 공식 저장소 커밋 SHA 직접 인용:**
   - `HWiNfoRestPoC.csproj`를 `<TargetFramework>net10.0</TargetFramework>`으로 변경.
   - 기존 PC Control Center 백엔드에 대한 확인 사실과 미확인 가정을 명시 구분.
   - 공식 HWiNFO 저장소 인용을 커밋 `97f5d5dc26076c07cca411b3b8e8af19ed0c77b0` 직접 링크로 통일.
10. **G1~G5 검증 기준·명령 통일 및 단기/장기 검증 분리:**
    - 전 문서와 스크립트의 G1~G5 샘플 횟수(60회/600회), 정량 기준(P95 < 250ms 등), 포트(`8764`/`8765`), CLI 명령어 완전 통일.
    - 단기 실기 검증 통과(Short-term PoC Pass)와 장기 안정성 검증 완료(12h/24h+ Soak, 절전/재부팅 복구)를 별도 항목으로 분리.

---

## [RC1] - 2026-11-25

- HWiNFO 8.61 Beta Build 6125 REST API 초기 조사 문서 3종, HTML 프로토타입, PowerShell/.NET 8/Python PoC 및 지시서 초판 작성.
