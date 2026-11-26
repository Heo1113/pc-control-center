# HWiNFO REST API — 공식 명세와 RC2 주장 대조

- 1차 출처: `malikm/HWiNFO` 저장소의 `HWiNFO_REST_API_SKILL.md` @ `97f5d5dc26076c07cca411b3b8e8af19ed0c77b0` (이 세션에서 원문 확인)
- hwinfo.com(공식 포럼·버전 기록)은 이 실행 환경에서 접속이 차단되어(DNS) 원문을 읽지 못했다. 그래서 버전·라이선스 항목은 **2차 출처(기술 매체)로만** 확인된 상태로 표시한다.

## 1. 명세로 확인된 것 (RC3 구현에 반영)

| 항목 | 명세 내용 | RC3 반영 |
|---|---|---|
| 읽기 엔드포인트 | `GET /api/ping` → `{"pong":true,"t":<서버 UTC epoch ms>}`, `GET /api/sensors/read`, `GET /api/sensors/sdb` | 수집기·PoC·HTML·릴레이 모두 이 3개 GET만 허용 목록으로 강제 |
| 상태를 바꾸는 엔드포인트 | `POST` filter/apply·filter/reset, logging/setup·start·stop, update_display | 어디에서도 보내지 않음. 릴레이는 POST/PUT/DELETE/PATCH 403 |
| read 최상위 | `Version: 1`, `Timestamp RS/Timestamp/Timestamp RE`(현지 시각 문자열), `Timestamp RS ms / Timestamp ms / Timestamp RE ms`(UTC epoch ms) | `Version`≠1 거부. 신선도는 `Timestamp ms` 기준, `RS ≤ TS ≤ RE` 위반·역행은 INVALID |
| 중첩 구조 | 센서 → 리딩 → `"<typeKey>"` → `{"Value": "문자열"}`, 그룹 수준 값 가능 | `parseRead` (JS·C# 동일) |
| type 키 | 0 NONE, 1 TEMP, 2 VOLT, 3 FAN, 4 CURRENT, 5 POWER, 6 CLOCK, 7 USAGE, 8 OTHER | 참고만. 채택은 SDB `Unit`으로 판정 |
| Value 문자열 | 천 단위 구분 쉼표 제거("strip commas") | 그대로 따르되, **소수점 쉼표 의심 값**(쉼표 뒤 1~2자리)을 G2 미달로 처리 — 명세가 소수점 로캘을 규정하지 않기 때문 |
| SDB 리딩 필드 | `hide`, `monitoring`, `logging`(0/1), `Unit`, `Tooltip`, 메타 객체 `tSensorLocation`·`tReadingLocation`·`tReadingType{eType,eTypeUnion}` | 메타 키는 이 3개만 정확히 제외 |
| hide/monitoring | `hide=1` 또는 `monitoring=0`이면 read에서 빠짐 | 인벤토리 CSV에 `hide`·`monitoring`·`inRead` 열로 기록 |
| 갱신 주기 | 리딩은 센서 원천 주기(약 2초)로 갱신 | 1초 폴링 시 같은 측정이 두 번 오므로 래치·정지 판정은 **서로 다른 타임스탬프**만 셈 |
| 시계 보정 | RTT가 가장 짧은 ping으로 `offset = t − (t0+t3)/2` | 수집기·PoC·HTML 동일 |
| 오래된 값(stale) 규칙 | 명세에 없음 | RC3가 정함: `config/gates.json` `freshness` (나이 > 6000 ms 또는 타임스탬프 정지 > 6000 ms → STALE) |
| 인증·전송 | 인증 없음, CORS `*`(사전 요청 미지원), 자체 서명 HTTPS 또는 HTTP, 404/503 오류는 일반 텍스트 | HTML은 토큰이 없으면 Authorization 헤더를 보내지 않음(사전 요청 회피). 토큰이 필요한 경우는 루프백 릴레이뿐 |

## 2. RC2 주장 중 명세에 없는 것

| RC2 주장 | 현재 상태 | 출처 |
|---|---|---|
| 최초 도입 버전 "HWiNFO 8.61 Beta Build 6125, 2026-11-18" | **2차 출처로 확인**, 1차 미열람 | igor'sLAB, TechPowerUp, VideoCardz, techwatch.nz 기사. 공식 포럼 글 제목 "HWiNFO v8.61-6125 Beta released"는 검색 결과로 확인했으나 본문은 접속 차단으로 읽지 못함 |
| 무료판 REST 서버 12시간 제한 | **2차 출처 여러 곳 일치**, 1차 미열람 | VideoCardz("free version limited to 12 hours"), igor'sLAB, TechPowerUp |
| Pro 가격 $29/$52/$199 | **확인 부족** | 한 매체만 금액 언급. 라이선스 판단에 쓰기 전 공식 확인 필요 |
| Build 6125는 재시작 시 REST 서버 자동 시작 미지원, 후속 베타에서 명령줄 스위치 예정 | **2차 출처 일치**, 1차 미열람 | igor'sLAB, techwatch.nz (포럼 직원 발언 인용) |
| HWiNFO 8.41+에서 WireView Pro II 지원 | **미확인** | RC2가 Overclock.net을 인용. RC3에서는 확인하지 못함 → 실기 SDB로 판정(G2·G3A) |
| 기본 포트 | 명세에 고정값 없음 | RC3는 Basalt 8080을 피해 8764를 사용 |

## 3. 실기에서만 확인되는 것

- 설치된 HWiNFO 버전과 REST 지원 여부: `Collect-HWiNfoRest.ps1 -Mode Inventory` (실행 파일 버전 + `/api/ping` 응답).
- 소수점 표기(로캘): `read_once.json`에서 값이 `64.8`처럼 점으로 오는지. 쉼표면 G2가 `decimalCommaSuspects`로 미달 처리한다.
- 자동 시작·12시간 경계: G4S(수동 기록), 장기 항목.

## 4. 참고 링크

- igor'sLAB: https://www.igorslab.de/en/hwinfo-8-61-rest-api-web-server-cpu-gpu-sensor-data-smartphones-tablets-secondary-pcs/
- VideoCardz: https://videocardz.com/newz/hwinfo-adds-rest-api-for-custom-sensor-dashboards-free-version-limited-to-12-hours
- TechPowerUp: https://www.techpowerup.com/355134/hwinfo-can-now-serve-sensor-readings-to-phones-and-tablets-over-your-network
- techwatch.nz: https://techwatch.nz/articles/hwinfo-8-61-beta-browser-sensor-dashboards-startup-plans-2026
- (접속 불가) HWiNFO 포럼: https://www.hwinfo.com/forum/threads/hwinfo-v8-61-6125-beta-released.11401/
