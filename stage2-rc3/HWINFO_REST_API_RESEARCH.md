# HWiNFO REST API 심층 기술 조사서 (`HWINFO_REST_API_RESEARCH.md`) — RC2

> **RC3 정정 (2026-11-24):** 버전(8.61 Build 6125)·무료 12시간 제한·자동 시작 미지원은 공식 명세(SKILL.md)에 없고 기술 매체(2차 출처)로만 확인됩니다. 공식 포럼은 RC3 작업 환경에서 접속이 차단되어 원문을 읽지 못했습니다. 명세 대조는 `docs/HWINFO_SPEC_CHECK.md`.

> **문서 성격:** IRON Stage 2 아키텍처 검토를 위한 독립 조사 문서 (Stage 1 디자인·구현, `main` 브랜치, Basalt 운영 설정 및 하드웨어 보호 설정 미변경)  
> **조사 기준일:** 2026-11-25 (RC2 개정)  
> **공식 저장소 확인 커밋:** [`malikm/HWiNFO` `97f5d5dc26076c07cca411b3b8e8af19ed0c77b0`](https://github.com/malikm/HWiNFO/tree/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api) (2026-11-20T10:15:03Z)  
> **주요 1차 출처:** [HWiNFO REST API Guide (PDF)](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf), [HWiNFO_REST_API_SKILL.md](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/AI/HWiNFO_REST_API_SKILL.md), [공식 대시보드 디렉터리](https://github.com/malikm/HWiNFO/tree/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards)

---

## 1. 사실 확인 범위 및 미검증 항목 구분

| 구분 | 검증 상태 | 대상 항목 |
|---|---|---|
| 공식 API 명세·엔드포인트·JSON 스키마 | **공식 저장소 커밋 `97f5d5d` 직접 확인** | 9개 엔드포인트, `Version` 계약, Type-level 트리, SDB 메타데이터 키(`tSensorLocation`, `tReadingLocation`, `tReadingType`), 에러 코드, CORS/TLS 규칙 |
| 버전·라이선스·자동 시작 현황 | **외부 기술 보도 교차 확인** | 8.61 Beta Build 6125(2026-11-18), 무료 12시간 제한, Build 6125 재시작 시 REST 서버 자동 시작 미지원 |
| IRON 대상 PC 실기 동작 | **미검증 (`[실기 검증 필요]`)** | 실제 PC(Ryzen 9800X3D, RTX 5080 + RTX 5060 Ti, 128GB RAM, 다수 NVMe, WireView Pro II)의 SDB 구조, 지연(P95), 값 고정 여부, CPU/RAM 오버헤드, 12시간 만료 시 정확한 HTTP 응답, 후속 베타 자동 시작 여부 |

---

## 2. 최신 베타 버전 및 포트 배치 주의사항

- **최초 도입 버전:** **HWiNFO 8.61 Beta (Build 6125)**, 2026년 11월 18일 공개 ([igor'sLAB](https://www.igorslab.de/en/hwinfo-8-61-rest-api-web-server-cpu-gpu-sensor-data-smartphones-tablets-secondary-pcs/), [TechPowerUp](https://www.techpowerup.com/355134/hwinfo-can-now-serve-sensor-readings-to-phones-and-tablets-over-your-network), [VideoCardz](https://videocardz.com/newz/hwinfo-adds-rest-api-for-custom-sensor-dashboards-free-version-limited-to-12-hours)).
- **정식 버전 구분:** 2026년 11월 10일 공개된 안정판 HWiNFO 8.60에는 REST API 서버가 포함되지 않음 ([igor'sLAB](https://www.igorslab.de/en/hwinfo-8-61-rest-api-web-server-cpu-gpu-sensor-data-smartphones-tablets-secondary-pcs/)).
- **IRON 환경 포트 정책 (Basalt 충돌 회피):**
  - 공식 가이드 예제는 예시 포트로 `8080`을 사용하지만([HWiNFO REST API Guide PDF, p.3](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf)), **IRON 대상 PC에서는 Basalt가 `127.0.0.1:8080`을 운영 중**이므로 `8080`을 절대 사용하지 않는다.
  - 본 RC2 패키지는 **HWiNFO REST API 후보 포트를 `127.0.0.1:8764`**, **IRON Remote 중계 서버 포트를 `127.0.0.1:8765`**로 통일한다.
  - 실행 전 반드시 포트 점유 및 PID를 조회하며, 충돌 시 기존 프로세스를 종료하거나 설정을 변경하지 않고 즉시 중단한다.

---

## 3. 공식 API 명세 및 엔드포인트

공식 문서([HWiNFO REST API Guide PDF](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf), [HWiNFO_REST_API_SKILL.md](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/AI/HWiNFO_REST_API_SKILL.md)) 기준 전체 엔드포인트는 9개이며 모두 절대 경로(`/api/...`)를 사용한다.

| Method | Path | 기능 | 요청 본문 | 주요 응답 코드 | IRON 검증 허용 여부 |
|---|---|---|---|---|---|
| `GET` | `/api/ping` | 연결 확인, RTT·시계 오프셋 측정 | 없음 | `200 {"pong":true,"t":<epoch_ms>}` | **허용 (읽기 전용)** |
| `GET` | `/api/sensors/read` | 표시·모니터링 중인 실시간 센서 값 조회 | 없음 | `200 <JSON>`, `503 {"error":...}` | **허용 (읽기 전용)** |
| `GET` | `/api/sensors/sdb` | Sensor Database(정적 메타·단위·툴팁·플래그) 조회 | 없음 | `200 <JSON>`, `404`, `500` | **허용 (읽기 전용)** |
| `POST` | `/api/sensors/filter/apply` | 센서 필터 전역 적용 (최대 1 MB, 깊이 16) | 플래그 전용 JSON (`Version: 1`) | `200 {"ok":true,"sensors":N}`, `400`, `413` | **금지 (전역 상태 변경)** |
| `POST` | `/api/sensors/filter/reset` | 저장된 필터 삭제 및 기본 트리 복원 | 무시됨 | `200 {"ok":true}` | **금지 (전역 상태 변경)** |
| `POST` | `/api/sensors/logging/setup` | 내장 로깅 옵션 설정 | 설정 JSON (`Version: 1`) | `200 {"ok":true}`, `400` | **금지 (파일 쓰기)** |
| `POST` | `/api/sensors/logging/start` | 내장 로깅 시작 (`logging=1` 항목 기록) | 무시됨 | `200 {"ok":true}`, `409` | **금지 (파일 쓰기)** |
| `POST` | `/api/sensors/logging/stop` | 내장 로깅 중지 | 무시됨 | `200 {"ok":true}`, `409` | **금지 (상태 변경)** |
| `POST` | `/api/sensors/update_display` | HWiNFO Sensors 창 UI 재그리기 | 무시됨 | `200 {"ok":true}` | **검증 불필요** |

### 3.1 `Version` 계약 규칙

- 4개 JSON 계약(`read`, `sdb`, `filter/apply`, `logging/setup`)은 각자 독립된 정수형 루트 `"Version"`을 가진다(현재 모두 `1`) ([HWiNFO_REST_API_SKILL.md](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/AI/HWiNFO_REST_API_SKILL.md)).
- 클라이언트는 `read`와 `sdb` 수신 시 `"Version"` 누락 또는 미지원 범위(`min..current` 외)를 즉시 오류로 처리하고 **데이터를 폐기**해야 한다.

---

## 4. JSON 스키마 및 파싱 핵심 규칙

### 4.1 `GET /api/sensors/read`

공식 문서([HWiNFO_REST_API_SKILL.md](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/AI/HWiNFO_REST_API_SKILL.md))에 제시된 실제 예시 구조:

```json
{
  "Version": 1,
  "Timestamp RS": "11.11.2026 14:43:09.431",
  "Timestamp": "11.11.2026 14:43:09.559",
  "Timestamp RE": "11.11.2026 14:43:09.590",
  "Timestamp RS ms": 1794404589431.396,
  "Timestamp ms": 1794404589559.047,
  "Timestamp RE ms": 1794404589590.982,
  "System: ASUS ": {
    "Virtual Memory Committed": { "8": { "Value": "19,324" } },
    "Core VIDs": {
      "2": { "Value": "1.172" },
      "P-core 0 VID": { "2": { "Value": "1.170" } }
    }
  }
}
```

1. **루트 구분:** 문자열/숫자(`Version`, `Timestamp *`)는 메타필드, 최상위 객체만 센서다.
2. **Type-Level 계층:** `"<reading>": { "<type>": { "Value": "..." } }` 구조이며 `<type>`은 문자열 숫자 키(`"0"` NONE, `"1"` TEMP, `"2"` VOLT, `"3"` FAN, `"4"` CURRENT, `"5"` POWER, `"6"` CLOCK, `"7"` USAGE, `"8"` OTHER)다.
3. **그룹 자체 값:** 그룹 노드도 하위 리딩과 나란히 숫자 타입 키를 가질 수 있다.
4. **문자열 `Value`:** 천 단위 쉼표(`"19,324"`)나 텍스트(`"Yes"`/`"No"`)를 포함하므로 쉼표 제거 후 파싱해야 한다.
5. **단위·툴팁 분리:** `read`에는 `Unit`/`Tooltip`이 없으므로 `/api/sensors/sdb`를 1회 조회해 경로+타입 키로 조인한다.
6. **후행 공백 주의:** `"System: ASUS "`처럼 센서명 끝에 공백이 포함될 수 있으므로 SDB–Read 매칭 시 원본 키 기준 일치와 정규화 키를 구분해 처리한다.

### 4.2 `GET /api/sensors/sdb` 메타데이터 키 주의사항

공식 SDB 노드에는 `hide`, `monitoring`, `logging`, `Unit`, `Tooltip`과 함께 3개의 메타데이터 객체 **`tSensorLocation`, `tReadingLocation`, `tReadingType`**이 포함된다([HWiNFO REST API Guide PDF, p.6](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf)).
- **파서 구현 주의:** `t*` 접두사 와일드카드로 제외하면 **`Total Power`, `Temperature`, `Tctl/Tdie`, `Thermal Grizzly ...`** 같은 실제 센서/리딩 노드가 통째로 누락되는 치명적 결함이 발생한다. 반드시 정확한 3개 키 집합(`tSensorLocation`, `tReadingLocation`, `tReadingType`)만 제외해야 한다.

---

## 5. 갱신 주기, 인증, TLS/CORS, 라이선스 및 자동 시작

- **갱신 주기:** HWiNFO 내부 폴링(기본 약 2,000 ms)에 종속되며, Remote Center의 `Pause Monitoring` 체크 시 `GET /api/sensors/read` 호출 시에만 갱신된다([Guide PDF, p.3, p.5](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf)). 공식 가이드 권고 폴링 간격은 **500 ms 이상**이며 지연 응답은 폐기해야 한다([Guide PDF, p.9](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/docs/HWiNFO_REST_API.pdf)).
- **인증 및 CORS/TLS:**
  - **인증 없음("No authentication yet")** — 신뢰된 네트워크 전용 권고([SKILL.md](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/AI/HWiNFO_REST_API_SKILL.md)).
  - `Access-Control-Allow-Origin: *` 반환, **CORS Preflight(`OPTIONS`) 미지원**(브라우저 Cross-Origin POST는 `Content-Type: text/plain` 필수).
  - HTTPS 기본 자체 서명 인증서 사용. `404` 및 종료 중 `503 server is stopping`은 JSON이 아닌 **plain text** 응답.
- **무료·Pro 라이선스 제한:**
  - 무료 버전(개인·비상업): REST API 서버 **12시간 연속 구동 제한** (Shared Memory·원격 모니터링과 동일) ([TechPowerUp](https://www.techpowerup.com/355134/hwinfo-can-now-serve-sensor-readings-to-phones-and-tablets-over-your-network), [VideoCardz](https://videocardz.com/newz/hwinfo-adds-rest-api-for-custom-sensor-dashboards-free-version-limited-to-12-hours)).
  - 유료 HWiNFO Pro: 12시간 제한 해제. 외부 보도 기준 Personal $29(1년) / $52(3년) / $199(평생), 상업용은 별도 Pro Engineer/Corporate 라이선스 필요 ([VideoCardz](https://videocardz.com/newz/hwinfo-adds-rest-api-for-custom-sensor-dashboards-free-version-limited-to-12-hours), [Time Doctor](https://www.timedoctor.com/blog/pc-monitoring-software/)).
- **자동 시작 현황:**
  - **Build 6125 기준 재시작 시 REST 서버 자동 시작 미지원** — 매 재실행마다 Remote Center에서 수동 `Start` 필요 ([igor'sLAB](https://www.igorslab.de/en/hwinfo-8-61-rest-api-web-server-cpu-gpu-sensor-data-smartphones-tablets-secondary-pcs/)).
  - 개발팀은 향후 베타에서 CLI 자동 시작 파라미터와 종료 시 서버 상태 저장/복원을 추가할 예정이라고 밝혔으나, 실제 지원 여부는 `[실기/후속 빌드 검증 필요]`.

---

## 6. 공식 대시보드 예제 5종 요약

공식 저장소 커밋 `97f5d5d` 기준([`dashboards/`](https://github.com/malikm/HWiNFO/tree/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards)) 모두 외부 라이브러리 없는 단일 HTML 파일로 제공된다.

| 대시보드 | 직접 링크 (커밋 `97f5d5d`) | 핵심 특징 및 IRON 참고 포인트 |
|---|---|---|
| Simple Example | [`simple_dashboard.html`](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/simple_example/simple_dashboard.html) | 약 100줄 최소 참조 구현, `sdb`→`read` 단위 매핑, Typed/Flat 양립 파서 |
| Built-in Dashboard | [`HWiNFO_builtin_dashboard.html`](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/hwinfo_builtin/HWiNFO_builtin_dashboard.html) | HWiNFO 내장 기본 UI, Min/Max/Avg, NTP-style Timing P95(최근 200회), 필터 편집기, 모바일 카드 뷰 |
| Telemetry Console | [`telemetry_console.html`](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/telemetry_console/telemetry_console.html) | 다크 콘솔, Health Score(0–100), CPU 코어맵, 지연 응답 폐기 패턴 |
| PC3D | [`hwinfo_pc3d.html`](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/pc3d/hwinfo_pc3d.html) | CSS 3D 뷰, SDB `tSensorLocation`/`tReadingLocation` 기반 컴포넌트 자동 매핑(기본 HTTP 전용 주의) |
| Bubbles | [`bubbles.html`](https://github.com/malikm/HWiNFO/blob/97f5d5dc26076c07cca411b3b8e8af19ed0c77b0/sensors/rest_api/dashboards/bubbles/bubbles.html) | 수치 비례 버블, Intel/AMD/NVIDIA 공통 핵심 센서 매칭 휴리스틱 |
