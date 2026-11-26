# IRON Stage 2 — 실제 데이터 연결 (통합 브랜치)

- 브랜치: `feat/iron-stage2-integration`
  - 기준: 프로세스 식별 승인본 `a9d69f4`
  - 함께 들어온 것: `rc3/windows-ci-check` @ ed8883d의 `stage2-rc3/`(파일 복사, 병합 아님)
- 보존: `main`, `feat/iron-ui-stage1`(187d533), `feat/iron-process-identity`(a9d69f4), `rc3/windows-ci-check`는 바꾸지 않습니다. 병합과 프로젝트 정본 수정은 별도 승인 전까지 하지 않습니다.
- 승인 범위: 실제 데이터 연결 코드와 격리된 CI 검증. 실제 하드웨어 검증은 사용자 PC에서 따로 합니다(`stage2-verify/README_실기검증.md`).

## 1. 켜는 법 — 화면 디자인은 그대로

| 실행 | 동작 |
|---|---|
| `Iron.App.exe` | Stage 1과 같음(모의 데이터, 기본값) |
| `Iron.App.exe --source real` | 실제 데이터. 제목 표시줄 단추가 "실측 데이터"(비활성)로 바뀌고, 툴팁에 소스별 상태가 나옴 |
| `--hwinfo-url URL\|off` | HWiNFO REST 주소(루프백만, 8080 금지). 기본 `http://127.0.0.1:8764/` |
| `--probe-real DIR [--probe-seconds N]` | 창 없이 실제 읽기 결과를 `DIR/probe.json`에 기록(명령줄은 가린 형태만) |
| `--soak-log FILE` | 1분마다 IRON 자신의 메모리·핸들·GDI·USER·소스 상태를 CSV로 기록(12시간 실행용) |

## 2. 값의 출처

| 화면 값 | 출처 | 비고 |
|---|---|---|
| GPU 온도·전력·부하·VRAM·클럭 | NVML (`LocalAiPerf.Telemetry.HardwareMonitor`, 기존 코드) | NVML이 없으면 `NoDevice`(회색 "미연결"), CPU·RAM은 계속 표시 |
| CPU 부하, RAM | Windows (`GetSystemTimes`, `GlobalMemoryStatusEx`, 같은 리더) | |
| CPU Tctl | HWiNFO REST (RC3 매핑 규칙, SDB 단위 확인) | HWiNFO가 없거나 끊기면 N/A |
| CPU 패키지·코어 최고 온도 | (없음) | RC3 매핑 규칙에 없음 → N/A. 사용자 PC 인벤토리(G2)로 이름을 확정한 뒤 추가 |
| NVMe 온도 | HWiNFO REST (드라이브가 여럿이면 최고값) | |
| NVMe 읽기 MB/s | Windows PDH `\PhysicalDisk(_Total)\Disk Read Bytes/sec` | 전체 디스크 합 |
| WireView 6핀·전력·전압·커넥터 온도 | HWiNFO REST (읽기 전용) | **IRON은 WireView USB를 열지 않음.** 6핀이 모두 있을 때만 핀을 표시. 커넥터 In/Out은 센서 이름에 In/Out이 있을 때만 |
| 프로세스별 GPU 메모리·엔진 | WDDM 성능 카운터 (`PcControlCenter.GpuMemory`, 기존 코드) | 프로세스당 한 줄: 전용 메모리가 가장 큰 어댑터의 값. NVML 값과 합치지 않음 |
| 프로세스 이름 셀·툴팁 | `Iron.ProcessIdentity` 실제 해석기 | PID는 마지막 스냅샷에서 찾음(PID 재사용 방지). 식별 전에는 exe 이름 + "식별 중" |
| FLUX PRO | (연결 안 함) | 표시 송신은 데이터 연결 범위 밖. "미연결"로 표시 |

각 리더는 자기 스레드에서 돕니다. UI 타이머는 최신 값을 모으기만 하므로 센서를 기다리지 않습니다.
- 주기를 놓치면 건너뛰고 셉니다. 밀린 주기를 따라잡지 않습니다.
- 경고 기준은 Stage 1 값을 그대로 씁니다(GPU 72 °C, 핀 7.0 A, 3회 연속).

## 3. 명령줄 민감정보

- 실제 프로세스의 명령줄은 식별 모듈의 `Redactor`를 거친 값만 행 데이터(`ProcessReading.CommandLine`)에 들어갑니다. 원본은 해석 중에만 메모리에 있습니다.
- 식별 전 행의 명령줄은 빈칸입니다. WDDM 리더는 명령줄을 읽지 않습니다.
- 확인 방법:
  - 단위 테스트: `Composer`, `RealTelemetrySource`
  - CI 탐침: 시험 프로세스에 `--api-key=sk-ci-SECRET-…`를 넣고, probe.json 전체에 그 값이 없는지 검사합니다. 일반 사용자 계정에서도 같은 검사를 합니다.
  - 사용자 PC: `Run-IronProbe.ps1`의 "민감정보 자체 점검"

## 4. HWiNFO 연결이 끊긴 동안 요청이 쌓이던 문제 (RC3 → RC3.2)

**원인(Windows CI 실측, 수정 전 RC3 코드)**
1. **Windows의 거부 연결:** 서버가 꺼진 루프백 포트에 연결하면 RST를 받고도 다시 시도해 실패까지 약 2초가 걸립니다.

   | 클라이언트 | 실패까지 걸린 시간 |
   |---|---|
   | .NET HttpClientHandler | 2.0초 |
   | PS 5.1 Invoke-WebRequest | 2.1~2.9초 |
   | PS 7 Invoke-WebRequest | 2.3초 |
   | Python socket | 2.0초 |
   | RC3.2 연결 시간 제한(0.5초) | 0.5초 |

   RC3은 요청 시간 제한 5초에 연결 시간 제한이 없어, 끊긴 동안 요청 하나가 여러 주기를 붙잡았습니다. PS 5.1에서는 한 요청이 4.0초 걸렸습니다.
2. **절대 일정 + 따라잡기:** 샘플 i를 `(i−1)×간격`에 맞추고, 이미 늦었으면 기다리지 않고 바로 보냈습니다. 재연결 직후 밀린 샘플이 연달아 나갔습니다.
   - PoC: 14 ms 안에 7개
   - PS 5.1: 220 ms 안에 8개
   - PS 7: 51 ms 안에 7개

   같은 순간의 값이 여러 샘플로 기록되고, HWiNFO에 순간 부하가 몰렸습니다.

**수정(RC3.2)**
- 수집기·PoC·IRON 클라이언트 모두 주기 전체를 놓친 샘플은 요청하지 않고 `SKIPPED_LATE`로 기록합니다(IRON은 개수만 셈).
- 요청 시간 제한은 간격의 2배(1~5초)로 줄였습니다. 연결 시간 제한은 간격 길이(0.25~1초)로 따로 두었습니다.
- IRON 클라이언트는 실패하면 1·2·4·최대 5초 간격으로 다시 시도합니다. 요청은 한 번에 하나뿐이고, 다시 연결되면 센서 목록(SDB)을 새로 읽습니다.
- 검증: `stage2-rc3/tests/disconnect_timeline.py`(같은 끊김 시나리오를 수정 전/후에 적용, 샘플별 시간표), `Iron.Stage2.Tests`의 `OutageDoesNotPileUpRequests`.

## 5. 핸들 증가 조사

- 관찰: 식별 모듈 단독 5분 측정에서 핸들이 +17~+18 늘었습니다(기준선 +2). 실행 3~10회 모두 같았습니다.
- 도구:
  - `measure`에 자기 프로세스 핸들의 **종류별 집계**를 넣었습니다(`ProcessHandleInformation` + `NtQueryObject`). 30초 시점과 끝을 비교합니다.
  - `soak` 모드: 5초마다 짧은 프로세스를 띄워 식별·캐시 정리를 반복합니다. 1분마다 종류별 핸들·스레드·GDI·메모리를 기록하고, 2분 이후 시간당 기울기를 냅니다.
- CI: 일반 사용자 계정으로 20분 soak을 돌립니다. 사용자 PC: bench 12시간 soak과 IRON 창 12시간 `--soak-log`.

## 6. 검증 — 격리 CI (`.github/workflows/iron-stage2-integration.yml`)

| 작업 | 내용 |
|---|---|
| Linux | 빌드, Stage 1 + 식별 + Stage 2 단위 테스트, RC3.2 Python 테스트 |
| Windows 회귀 | 식별 Windows 실기 테스트 포함 단위 테스트 |
| | 화면: 187d533 대비(판정 — 이름 셀 밖은 픽셀 동일), a9d69f4 대비(보고 — ~ 표시와 공유 호스트 배지만 셀 안) |
| | 실제 창 스모크 |
| | Stage 2 탐침(모의 HWiNFO + 비밀값 시험 프로세스) |
| | 실제 데이터 창 스모크 |
| | 실기 검증 키트 아티팩트 |
| Windows HWiNFO | RC3 Windows 묶음을 RC3.2에 실행, 끊김 시간표 수정 전/후, 거부 연결 시간 측정 |
| Windows 측정 | 일반 사용자 5분 기준선/식별, 식별 전/후 사례, 일반 사용자 Stage 2 탐침, 20분 soak |

러너에는 NVIDIA GPU·HWiNFO·WireView가 없습니다. 그래서 NVML은 `Unavailable`, HWiNFO는 모의 서버(SYNTHETIC 고정 값)입니다. 실제 값의 정확성은 사용자 PC 검증으로 확인합니다.

## 7. 사용자 PC에서 확인할 것

`stage2-verify/README_실기검증.md` 참고:
- HWiNFO G1~G5, WireView 독점(G3A)
- 한글 MUI, WebView2, 실제 아이콘
- 12시간 연속 실행(IRON 창 `--soak-log`, 선택: bench soak)
