# 센서 공급자 권고안 (RC3)

> **상태: 잠정.** 사용자 PC에서 실측을 하지 못했다(이 세션에 사용자 PC가 연결되지 않음). 아래 권고는 기존 백엔드 소스 확인(`docs/BACKEND_FACT_CHECK.md`)과 공식 명세(`docs/HWINFO_SPEC_CHECK.md`)에만 근거하며, 각 항목은 표시된 게이트가 **LIVE_CAPTURE로 PASS**해야 확정된다. Stage 2 구현은 별도 승인 전 착수하지 않는다.

## 1. 권고: 항목별 공급자 분리(Hybrid), 자동 전환 없음

| 영역 | 권고 공급자 | 이유 | 확정 조건 |
|---|---|---|---|
| GPU 코어 온도·전력·부하·VRAM 총량·클럭 | **기존 NVML (`SharedHardwareHub`) 유지** | 이미 단일 리더로 동작, 외부 앱 의존 없음 | 변경 없음. HWiNFO 값은 비교용으로만 |
| GPU 핫스팟·메모리 정션 온도 | **HWiNFO REST (표시 전용)** | 기존 NVML 경로가 읽지 않음 | G2에서 RTX 5080·5060 Ti 두 GPU 모두 °C 단위로 노출 |
| CPU Tctl/Tdie·패키지 전력 | **HWiNFO REST** | 기존 백엔드는 수집하지 않음(PawnIO·관리자 권한 필요) | G1 PASS + G2(`cpu.tctl` 채택) |
| RAM 사용량 | **기존 `GlobalMemoryStatusEx` 유지** | 충분, 의존성 없음 | — |
| DIMM·NVMe 온도, NVMe 수명 | **HWiNFO REST** | 기존 백엔드에 없음 | G2 informational 항목이 실제로 채워짐 |
| 프로세스별 VRAM | **기존 PDH(`PcControlCenter.GpuMemory`)** | HWiNFO REST 미제공 | — |
| Antec FLUX PRO 표시 송신 | **새로 구현(Stage 2, HID 0x2022/0x0522)** | 기존 백엔드에도 없음(RC2의 "보유" 주장은 사실 아님). HWiNFO와 무관 | Stage 2 승인 후 |
| WireView Pro II | **HWiNFO REST 읽기 우선 검토, 확정 보류** | 직접 USB 코드가 아직 없으므로, HWiNFO가 6핀을 안정적으로 내면 USB 코드·`RELEASE USB` 핸드오버를 만들 필요가 없어진다 | G3A PASS(600샘플, 6핀 누락 0, 정지 ≤ 10, 운영자 확인) + 장기 항목. 실패하면 A-Slim의 읽기 전용 USB(0x01·0x04)로 |

## 2. 지킬 규칙

1. **공급자 자동 전환 금지.** 특히 WireView는 HWiNFO REST가 끊겨도 IRON이 USB를 열지 않는다(REST 실패 ≠ USB 해제). 공급자는 사용자가 설정으로 하나만 고른다.
2. **소유권은 REST 센서 노출로 판단하지 않는다.** UI·보고서의 소유권 상태는 `UNCONFIRMED`가 기본이고, `OPERATOR_ATTESTED`는 운영자 확인 + 다른 WireView 클라이언트 프로세스 0개 + PnP 기록이 모두 있을 때만.
3. **경고 기준은 Stage 1 값 그대로**(GPU 코어 72 °C, 핀 7.0 A, 0.72, 3회 래치). 핫스팟·커넥터 온도·불균형 %에는 Stage 1 기준이 없으므로 경고하지 않는다.
4. **부분 데이터는 합치지 않는다.** GPU 전력 합계는 모든 GPU가 전력을 낼 때만, WireView 편차는 6핀이 모두 있을 때만.
5. Basalt 8080은 쓰지 않으며, 원격(LAN) 노출은 별도 승인 전 금지.

## 3. HWiNFO 의존의 운영 위험 (장기 미검증)

- 무료판 12시간 제한(2차 출처) → 상시 운용하려면 HWiNFO Pro가 필요할 가능성.
- Build 6125는 재시작 시 REST 서버가 자동으로 켜지지 않는다는 보도(2차 출처) → 부팅 후 수동 시작 필요. 후속 빌드의 명령줄 스위치 여부 확인 필요.
- HWiNFO가 꺼지면 CPU 온도·DIMM·NVMe 온도가 N/A가 된다(기존 백엔드로 대체할 수 없음). GPU 코어 온도 경고는 NVML이므로 영향 없음.

## 4. 판정 순서

1. G1(연결·지연·신선도·CPU 오버헤드) → 2. G2(인벤토리·단위 매핑) → 3. G3A(WireView 단독) → 4. G4S(수동) → 5. 장기 항목.
G1 또는 G2가 FAIL이면 HWiNFO는 "보조 표시"로만 남기고 CPU 온도는 N/A를 유지하는 것이 안전하다.
