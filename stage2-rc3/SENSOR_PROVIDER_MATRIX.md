# IRON 센서별 공급자 비교 매트릭스 (`SENSOR_PROVIDER_MATRIX.md`) — RC2

> **RC3 정정 (2026-11-24):** §2 표 WireView·FLUX 행의 "확인 사실: 기존 백엔드가 WireView 직접 USB 점유 + `RELEASE USB` 보유", "FLUX PRO 표시 송신 보유"는 **사실이 아닙니다**. `main@a080a2f`에는 WireView·FLUX·SerialPort·HID 코드가 없고, 두 기능은 Stage 1의 모의 동작입니다. CPU 행(로컬 저지연 조회)도 실제로는 수집하지 않습니다. 근거: `docs/BACKEND_FACT_CHECK.md`. 소유권 상태 `HWINFO_OWNED`는 REST 감지만으로 정하지 않으며 RC3는 `UNCONFIRMED`/`OPERATOR_ATTESTED`만 씁니다. 권고는 `docs/SENSOR_PROVIDER_RECOMMENDATION.md`.

> **문서 성격:** IRON Stage 2 센서 공급자 비교 및 장치 접근 권한 분리 분석서 (기존 Stage 1 백엔드 삭제·교체 확정 금지, 하드웨어 보호 설정 변경 금지)  
> **조사 기준일:** 2026-11-25 (RC2 개정)

---

## 1. 사실 확인 범위: 확인된 사실 vs 미확인 가정

본 문서는 기존 IRON 프로젝트 소스코드를 직접 열람하지 않은 독립 조사 문서이므로, 사용자 요청서에서 확인된 사실과 일반 Windows 모니터링 아키텍처 기반 가정을 엄격히 구분한다.

| 구분 | 항목 |
|---|---|
| **확인된 사실 (사용자 제공)** | ① IRON Stage 1 WPF 프로토타입(.NET 10) 구현 중 ② 대상 PC 하드웨어 구성: **AMD Ryzen 7 9800X3D**, **RTX 5080 16GB + RTX 5060 Ti 16GB**, **RAM 128GB**, **다수 NVMe SSD**, **WireView Pro II**, **Antec FLUX PRO** ③ Basalt가 `127.0.0.1:8080` 사용 중 ④ 기존 백엔드는 WireView Pro II 직접 USB 점유 및 `RELEASE USB` 핸드오버 구조와 Antec FLUX PRO 표시 송신 기능을 보유 ⑤ WireView UI 주의 기준은 Stage 1 확정 설정이 별도 존재 |
| **미확인 가정 (소스 미열람)** | 기존 PC Control Center 백엔드가 CPU/RAM/NVMe/GPU/프로세스 VRAM 수집에 내부적으로 어떤 라이브러리 조합(예: LibreHardwareMonitor/PawnIO, NVML, DXGI/PDH, WMI, 자체 USB HID/Serial 드라이버)을 사용하는지는 실제 코드 확인 전까지 **일반적 가정**으로 표기하며, 실기 검증 시 Claude Code가 기존 백엔드와 직접 대조해야 함 |
| **실기 미검증 (`[실기 검증 필요]`)** | 대상 PC의 실제 HWiNFO SDB 트리, 듀얼 GPU(RTX 5080 + RTX 5060 Ti) 센서 노출 및 인덱스 안정성, 128GB DDR5 DIMM 온도 노출 여부, 다수 NVMe S.M.A.R.T. 노출, WireView Pro II 6핀 전 항목 노출 및 USB 단절 여부 |

---

## 2. 센서별 최적 공급자 비교 매트릭스

| 센서 영역 (대상 하드웨어 기준) | 기존 PC Control Center 백엔드 *(구현 세부사항은 가정 포함)* | HWiNFO REST API (8.61 Beta+, 포트 `8764`) | 1차 후보 주 공급자 | 폴백 및 실기 검증 포인트 |
|---|---|---|---|---|
| **CPU 온도·클럭·전력** *(Ryzen 7 9800X3D)* | 기존 백엔드 유지 시 로컬 저지연 조회 가능 *(가정: LHM/PawnIO 기반일 경우 X3D 신규 센서 대응 지연 가능성)* | AMD Ryzen 9000X3D 코어별 유효 클럭, Tctl/Tdie, 패키지 전력, 스로틀링 플래그 상세 지원 ([HWiNFO 공식](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/telemetry_console/Readme.md)) | **HWiNFO REST API** (조건부) | HWiNFO 장애/미구동 시 기존 백엔드 폴백 `[9800X3D Tctl/Tdie 노출 실기 검증 필요]` |
| **RAM 사용량 및 DIMM 온도** *(128GB DDR5)* | OS API 기반 RAM 사용량은 초저지연. DDR5 DIMM 개별 온도(SPD5118/SMBus)는 보드별 호환성 편차 큼 *(가정)* | 물리/가상 메모리 + DDR5 DIMM 개별 온도·전력 지원 ([Telemetry Console](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/telemetry_console/Readme.md)) | **하이브리드** (사용량: 기존/HWiNFO, **DIMM 온도: HWiNFO**) | `[대상 메인보드 128GB 풀뱅크 DIMM 온도 노출 여부 실기 검증 필요]` |
| **NVMe SSD 온도·상태** *(다수 NVMe)* | 기존 스토리지/SMART 수집 모듈 *(가정)* | 드라이브별 복합/컨트롤러 온도, 잔여 수명(%), 누적 쓰기량, I/O 속도, 경고 플래그 ([PC3D](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/pc3d/Readme.md)) | **HWiNFO REST API** | S.M.A.R.T.는 1~2초 주기로 충분. `[전체 NVMe 드라이브 동적 인식 실기 검증 필요]` |
| **듀얼 GPU 사용률·VRAM·전력·온도** *(RTX 5080 16GB + RTX 5060 Ti 16GB)* | *(가정: NVML/DXGI 기반 시)* 100~250 ms 고빈도 조회 및 외부 앱 무의존성 강점 | 멀티 GPU(`GPU [#0]`, `GPU [#1]`)의 코어/핫스팟/메모리 정션 온도, 전력, 클럭, 전체 VRAM, Limiters 제공 | **하이브리드** (상세 열·리미터: HWiNFO / 고빈도·폴백: 기존 백엔드) | `[RTX 5080 / RTX 5060 Ti 동적 식별 및 재부팅 후 순서 유지 실기 검증 필요]` |
| **프로세스별 VRAM 점유량** | 기존 백엔드 유지 *(가정: PDH/DXGI/NVML 프로세스 API)* | **미지원.** HWiNFO REST API는 디바이스 단위 하드웨어 센서만 제공 ([Guide PDF](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf)) | **기존 백엔드 필수 유지 (대체 불가)** | HWiNFO 단독 구조에서도 별도 유지 필수 |
| **WireView Pro II** *(12V-2x6 6핀 전류·전압·전력·온도)* | **확인 사실:** 직접 USB 점유 + `RELEASE USB` 핸드오버 구조 보유 | HWiNFO 8.41+에서 WireView Pro II 지원 확인 ([Overclock.net](https://www.overclock.net/threads/hwinfo-wireview-pro.1820325/)). 단 **읽기 전용**이며 X870E 등 일부 USB 포트에서 HWiNFO 접근 중 단절 이슈 보고 ([Reddit](https://www.reddit.com/r/ThermalGrizzly/comments/1r1suu4/wireview_pro_ii_software_105_available/)) | **단독 실기 검증 후 결정 (자동 USB 폴백 금지)** | 아래 섹션 3의 장치 소유권 분리 설계 및 2단계 검증 절차 필수 준수 |
| **Antec FLUX PRO 표시 송신** | **확인 사실:** 기존 백엔드에서 표시 송신 기능 보유 *(참고: 공개 프로토콜은 USB HID `VID 0x2022 / PID 0x0522` 12바이트 패킷, [antec-flux-pro-display-windows](https://github.com/motiop/antec-flux-pro-display-windows))* | **송신 불가.** HWiNFO REST API는 외부 USB HID 출력(Write)을 지원하지 않음 | **송신 드라이버: 기존 백엔드 필수 유지** | 입력 온도 소스만 HWiNFO 선택 가능(단, HWiNFO 장애 시 기존 백엔드 온도 폴백 필수) |

---

## 3. WireView Pro II 심층 분석: 장치 소유권 분리 및 `RELEASE USB` 핸드오버 검토

### 3.1 핵심 안전 원칙 (RC2 필수 반영)

1. **REST 연결 실패 ≠ USB 장치 점유 해제:**
   - HWiNFO REST API가 `503`, `Timeout`, `Connection Refused`를 반환하거나 무료 12시간 제한으로 웹 서버가 중단되더라도, **HWiNFO 본체 프로세스가 여전히 WireView Pro II USB 핸들을 점유하고 있을 수 있다.**
2. **WireView 자동 USB 폴백 전면 금지:**
   - 따라서 HWiNFO REST API 장애 감지 시 IRON이 **자동으로 직접 USB 연결로 폴백하는 동작은 금지**한다. 두 프로세스가 동시에 WireView USB에 접근하면 X870E 등 칩셋에서 USB 단절·센서 값 고정(Freeze) 위험이 급증한다 ([Reddit WireView2-SW 1.0.5](https://www.reddit.com/r/ThermalGrizzly/comments/1r1suu4/wireview_pro_ii_software_105_available/), [Reddit USB 단절 사례](https://www.reddit.com/r/ThermalGrizzly/comments/1rgyvlf/are_the_usb_disconnections_still_an_issue_on/)).
3. **장치 접근 권한(Device Ownership)과 데이터 공급자 분리 관리:**
   - 일반 센서(CPU/GPU/NVMe)는 자동 폴백이 가능하지만, WireView Pro II는 별도의 **Device Lease / Ownership 상태 머신**으로 관리한다:
     - `NATIVE_USB_OWNED` (기존 IRON 직접 USB 점유 — HWiNFO WireView 센서 비활성화 전제)
     - `HWINFO_OWNED` (HWiNFO 단독 점유 — IRON은 REST API 읽기 전용, 직접 USB 미개방)
     - `RELEASED_FOR_VENDOR_SW` (기존 `RELEASE USB` 핸드오버 상태 — 공식 `WireView2-SW` 사용 시)
     - `UNOWNED / DEGRADED` (소유권 미확정 또는 통신 장애 — UI에 `OFFLINE`/`STALE` 표시, 자동 USB 점유 시도 금지)
4. **UI 주의 기준 통일 및 보호 설정 무변경:**
   - WireView UI 주의/경고 임계치는 **기존 IRON Stage 1에 확정된 기준을 그대로 계승**하며, Stage 2 제안에서 임의로 변경하지 않는다. 하드웨어 자체의 컷오프/보호 설정 역시 일절 변경하지 않는다.

### 3.2 X870E USB 단절 위험을 고려한 2단계 검증 분리

| 검증 단계 | 대상 및 조건 | 목적 | 승인 요건 |
|---|---|---|---|
| **Phase A: 공급자별 단독 접근 검증 (필수 선행)** | ① 기존 IRON 단독 접근 기준선 측정 (HWiNFO 종료) → ② HWiNFO 단독 접근 측정 (기존 IRON 직접 USB 및 `WireView2-SW` 완전 종료) | 6핀 전류·전압·전력·온도 전 항목 SDB 노출 여부, 값 고정/USB 단절 0건 여부, 피크 편차 확인 | RC2 검증 지시서로 즉시 수행 가능 |
| **Phase B: 동시 접근 검증 (별도 승인 분리)** | HWiNFO 접근 상태에서 `WireView2-SW` 또는 기존 IRON USB 동시 접근 여부 테스트 | 동시 접근 시 USB 단절 재현 여부 확인 | **사용자 명시적 별도 승인 없이는 수행 금지** |

### 3.3 `RELEASE USB` 핸드오버 제거 가능성 판정 조건

- **제거 가능(조건부):** IRON이 WireView를 **100% 읽기 전용**으로만 사용하고, Phase A HWiNFO 단독 장시간 부하 검증에서 6핀 전 항목 노출 + USB 단절 0건 + 피크 편차 허용 범위가 확인되며, HWiNFO Pro 상시 구동이 전제될 때. (단, 공식 `WireView2-SW` 실행 시 HWiNFO와의 충돌 회피 절차는 여전히 필요함.)
- **제거 불가(유지):** IRON 내 장치 제어(화면 전환/폴트 클리어 등)가 필요하거나, HWiNFO 단독 검증에서 USB 단절·항목 누락·피크 누락이 발생하거나, HWiNFO Free 12시간 제한·자동 시작 부재를 수용할 수 없는 경우.
