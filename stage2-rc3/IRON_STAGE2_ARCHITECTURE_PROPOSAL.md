# IRON Stage 2 백엔드 아키텍처 및 IRON Remote 제안서 (`IRON_STAGE2_ARCHITECTURE_PROPOSAL.md`) — RC2

> **RC3 정정 (2026-11-24):** 방안 A의 "WireView 직접 USB 충돌 관리를 `RELEASE USB`로 통제", "Antec HID 송신 보유"는 기존 백엔드에 없는 기능을 전제로 했습니다(`docs/BACKEND_FACT_CHECK.md`). G1~G5 명령·기준은 `config/gates.json`과 `docs/REAL_HARDWARE_VERIFICATION_RUNBOOK.md`가 우선합니다. Build 6125·12시간 제한·자동 시작은 2차 출처로만 확인되었습니다(`docs/HWINFO_SPEC_CHECK.md`).

> **문서 성격:** IRON Stage 2 아키텍처 개선 가능성에 대한 독립 기술 제안서 (RC2 개정)  
> **제한 사항 준수:** Stage 1 WPF 디자인·구현, `main` 브랜치, Basalt(`127.0.0.1:8080`) 운영 설정, 하드웨어 보호 설정 및 프로젝트 정본 미변경. 특정 방안을 사전 정답으로 가정하지 않으며, 단기 실기 검증과 장기 안정성 검증을 명확히 구분한다.

---

## 1. 백엔드 아키텍처 3안 객관적 비교 (A / B / C)

- **방안 A (기존 자체 백엔드 유지):** Stage 1 PC Control Center 백엔드를 유지하고, 원격 모니터링이 필요하면 IRON 자체 백엔드에 로컬 중계 서버(`127.0.0.1:8765`)를 추가.
- **방안 B (HWiNFO REST API 중심):** 하드웨어 텔레메트리를 HWiNFO REST API(`127.0.0.1:8764`)로 일원화하되, 구조적으로 불가한 **프로세스별 VRAM**과 **Antec FLUX PRO USB HID 송신**만 최소 독립 모듈로 유지.
- **방안 C (HWiNFO + 기존 백엔드 Hybrid Provider):** `ISensorProvider` 추상 계층 아래 `HWiNfoRestProvider`와 `NativeBackendProvider`를 공존시키며, 일반 센서(CPU/RAM/NVMe/GPU)는 폴백 허용, **WireView Pro II는 자동 USB 폴백을 금지하고 별도 장치 소유권(Device Lease)으로 분리 관리**.

| 평가 기준 | 방안 A: 기존 자체 백엔드 유지 | 방안 B: HWiNFO REST API 중심 | 방안 C: Hybrid Provider 구조 |
|---|---|---|---|
| **정확성** | 기존 구현 기준 일관성 확보. 단 최신 플랫폼/DDR5 DIMM 등은 라이브러리 업데이트 주기에 의존 *(가정)* | 최신 CPU(9800X3D)·GPU(RTX 50 시리즈)·DIMM·NVMe 지원 폭 최상 ([igor'sLAB](https://www.igorslab.de/en/hwinfo-8-61-rest-api-web-server-cpu-gpu-sensor-data-smartphones-tablets-secondary-pcs/)). 단 기본 2초 폴링은 짧은 피크 누락 가능 | 영역별 강점 결합 가능. 단 두 공급자 간 동일 지표(예: CPU 온도) 미세 편차 표시 정책 및 WireView 단독 소유권 규칙 필수 |
| **안정성** | 단일 프로세스 완결. WireView 직접 USB 충돌 관리를 `RELEASE USB`로 통제 | HWiNFO 베타(Build 6125) 및 REST 서버 상태에 전면 종속 | 일반 센서는 무중단 폴백 가능. WireView는 자동 USB 폴백 금지로 X870E USB 충돌 위험 차단 |
| **응답 지연** | 로컬 인프로세스 호출(수 ms 수준, 고빈도 갱신 용이) *(가정)* | HTTP+JSON+HWiNFO 내부 폴링(기본 2,000 ms, 권고 ≥500 ms) ([Guide PDF](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf)) | 고빈도 지표(프로세스 VRAM·GPU)와 1~2초 보강 지표(DIMM·NVMe)를 비동기 분리 가능 |
| **유지보수성** | 신규 하드웨어 대응을 IRON 측이 직접 유지 | 센서 파싱 부담 대폭 감소. 단 베타 API 변경·문자열 경로 변동 대응 필요 | **초기 구현 복잡도 최상**(Provider 추상화, 동적 장치 매핑, 상태 머신, WireView 소유권 관리) |
| **라이선스** | 외부 상용 소프트웨어 추가 비용 없음 | 상시 구동 시 **HWiNFO Pro 필수**(무료 12시간 제한; Personal $29/1년·$52/3년·$199 평생, [VideoCardz](https://videocardz.com/newz/hwinfo-adds-rest-api-for-custom-sensor-dashboards-free-version-limited-to-12-hours)) | HWiNFO 미설치/무료 만료 시에도 기본 기능 유지, Pro 보유 시 확장 센서 활성화 가능 |
| **외부 의존성** | **최소** | **매우 높음** (Build 6125 기준 재시작 시 REST 서버 수동 시작 필요, [igor'sLAB](https://www.igorslab.de/en/hwinfo-8-61-rest-api-web-server-cpu-gpu-sensor-data-smartphones-tablets-secondary-pcs/)) | **선택적 의존** (HWiNFO 부재 시에도 IRON 단독 부팅·운용 가능) |
| **장애 복구** | 내부 예외 처리 및 장치 재초기화로 완결 | HWiNFO 종료·12시간 만료·재부팅 시 자동 복구 불가(수동 Start 대기) | 일반 센서는 즉시 Native 폴백 + `/api/ping` 백오프 재결합. WireView는 `OFFLINE`/`STALE` 표기(자동 USB 점유 금지) |

---

## 2. 포트 배치 및 IRON Remote 보안 아키텍처 (RC2 통일 기준)

### 2.1 포트 할당 및 충돌 보호 원칙

| 서비스 | 할당 주소 및 포트 | 비고 |
|---|---|---|
| **Basalt (기존 운영)** | `127.0.0.1:8080` | **절대 변경·종료·간섭 금지** |
| **HWiNFO REST API (후보)** | `127.0.0.1:8764` | 루프백 전용 바인딩 권장 (LAN 직접 노출 금지) |
| **IRON Remote Relay (PoC)** | `127.0.0.1:8765` (기본값) | LAN 접속은 `--allow-lan` 플래그 명시 시에만 활성화 |

모든 검증 스크립트 및 중계 서버는 시작 전 대상 포트(`8764`, `8765`)의 점유 여부와 PID를 확인하며, 충돌 감지 시 **기존 서비스를 종료하거나 변경하지 않고 즉시 중단(Non-zero exit)**한다.

### 2.2 HWiNFO 직접 접근 vs IRON 중계 서버(Relay) 비교

| 비교 항목 | HWiNFO REST API 직접 접근 | IRON 중계 서버 (`hwinfo_relay_poc.py` / 향후 Kestrel) |
|---|---|---|
| **인증·보안** | **인증 없음** — LAN 내 쓰기 엔드포인트(`filter/apply`, `logging/*`) 노출 위험 ([SKILL.md](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/AI/HWiNFO_REST_API_SKILL.md)) | **실행별 무작위 Bearer 토큰**(`secrets.token_urlsafe(24)`) + **Read-Only 강제**(모든 POST `403 Forbidden`) + 기본 루프백 바인딩 |
| **외부 CDN 및 오프라인 동작** | 대시보드 구현에 따라 외부 폰트/CDN 의존 가능 | **외부 CDN 의존성 0** — 로컬 정적 자산(`assets/`) 및 단일 오프라인 HTML을 동일 출처(Same-Origin)로 서빙 |
| **데이터 통합 및 상태 관리** | HWiNFO 센서만 노출 (프로세스 VRAM·Antec HID 상태 부재) | HWiNFO + 기존 백엔드 통합 가능, 다중 클라이언트 접속 시 500 ms 메모리 캐시로 HWiNFO 부하 1x 고정 |
| **iPhone Safari 호환성** | 자체 서명 HTTPS 인증서 수동 신뢰 및 CORS 제약 존재 | 동일 출처 서빙, Safe Area 대응, Single-flight/AbortController/지연 응답 폐기, `SIMULATION/LIVE/OFFLINE/STALE` 엄격 분리 |

---

## 3. 통일된 실기 검증 게이트 (G1~G5) 및 단기 vs 장기 구분

> **중요:** 아래 G1~G5의 샘플 수, 정량 기준, 실행 명령은 `CLAUDE_CODE_VERIFICATION_GUIDE.md` 및 `Test-HWiNfoRestApi.ps1`과 100% 일치한다. **단기 실기 검증 통과(Short-term PoC Pass)**는 Stage 2 설계 착수 근거일 뿐이며, 프로덕션 채택을 위해서는 별도의 **장기 안정성 검증(Long-term Soak Verification)**이 완료되어야 한다.

### 3.1 단기 실기 검증 게이트 (Short-term PoC Pass)

| 게이트 | 실행 명령 / 절차 (요약) | 정량 PASS 기준 |
|---|---|---|
| **G1. API 연결·지연·오버헤드** | `Test-HWiNfoRestApi.ps1 -BaseUrl "http://127.0.0.1:8764" -Samples 60 -IntervalMs 1000` + `.NET 10 PoC` 10회 | Exit Code `0`, `Version == 1`, 성공률 `100%` (60/60), Readout P95 `< 250 ms`, RTT P95 `< 300 ms`, 타임스탬프 단조성 위반 `0건`, HWiNFO64 추가 CPU 점유율 `< 3%` |
| **G2. 센서 커버리지·SDB↔Read 매핑** | G1 실행 산출물(`sdb_dump.json`, `sdb_read_mapping_report.json`, `keyword_matches.json`) 분석 | Read→SDB 매핑률 `≥ 99%`, 핵심 그룹(Ryzen 9800X3D, RTX 5080, RTX 5060 Ti, 128GB RAM/DIMM, 다수 NVMe) 동적 식별 확인 (미지원 항목인 프로세스 VRAM·Antec 송신은 N/A로 분류) |
| **G3-A. WireView 단독 접근 검증** | **기존 IRON USB & `WireView2-SW` 완전 종료** 상태에서 HWiNFO 단독으로 `Test-HWiNfoRestApi.ps1 -BaseUrl "http://127.0.0.1:8764" -Samples 600 -IntervalMs 1000` (10분 부하) | 6핀 개별 전류·전압·전력·커넥터 온도 전 항목 노출, 값 고정(`STUCK`) `0건`, USB 단절 `0건`, 기존 단독 기준선 대비 피크 편차 허용 범위 내 *(Phase B 동시 접근은 별도 승인 후에만 수행)* |
| **G4-S. 운용성 단기 확인** | HWiNFO 재실행 테스트 + 포트 점유 충돌 테스트 | 현재 설치 빌드의 REST 서버 자동 시작 여부 기록(Build 6125 기준 미지원 확인), `8764`/`8765` 포트 충돌 시 기존 프로세스 무변경 중단(Exit `1`) 확인 |
| **G5. IRON Remote 보안·iPhone 검증** | `pytest`/`unittest` 자동 부정 테스트 + `hwinfo_relay_poc.py --hwinfo-url http://127.0.0.1:8764 --port 8765` (`--allow-lan` 명시 시 iPhone Safari 접속) | 자동 부정 테스트(`401`/`403`/`404`/무작위 토큰/파서) 100% PASS, iPhone Safari `LIVE` 모드 시뮬레이션 잔존 0건, 미매핑 센서 `N/A` 표기, 외부 CDN 요청 `0건` |

### 3.2 장기 안정성 검증 (Long-term Soak Verification — 단기 통과와 별도 완료 조건)

단기 게이트(G1~G5)를 모두 통과하더라도 아래 3개 장기 항목이 완료되기 전에는 **장기 안정성 검증 완료**로 선언하지 않는다.
1. **12시간 경계 검증 (G4-L1):** HWiNFO Free 사용 시 12시간 경과 시점의 실제 HTTP 응답·동작 기록, 또는 HWiNFO Pro 라이선스 환경에서 24시간 이상 연속 폴링 무중단 확인.
2. **장시간 고부하 WireView 안정성 (G3-L):** X870E 등 대상 메인보드에서 수 시간 연속 게임/AI 듀얼 GPU 부하 중 WireView USB 단절·센서 고정 0건 확인.
3. **절전(Sleep)/재부팅 복구 검증 (G4-L2):** Windows 절전 복귀 및 재부팅 후 HWiNFO REST 서버·중계 서버·IRON 클라이언트의 상태 복구(`OFFLINE` → `LIVE`) 검증.
