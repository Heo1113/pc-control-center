# IRON RC3 게이트 보고서

- 출처: `SYNTHETIC` — 응답에 SYNTHETIC 표시(_notice)가 있음 — 모의 서버
- 판정 정책: PASS/FAIL은 source=LIVE_CAPTURE일 때만. OFFLINE_JSON·SYNTHETIC·UNVERIFIED_ENDPOINT는 UNVERIFIED.

## G1: UNVERIFIED (기준 충족: false)

| 항목 | 값 | 기준 | 결과 |
|---|---|---|---|
| samples | 8 | >= 60 | 미달 |
| successRatePct | 100 | >= 100 | 충족 |
| versionMismatches | 0 | <= 0 (Version 1) | 충족 |
| rttP95Ms | 13.1 | <= 300 | 충족 |
| readoutP95Ms | 110 | <= 250 | 충족 |
| timestampViolations | 0 | <= 0 | 충족 |
| staleSamples | 0 | <= 0 | 충족 |
| hwinfoCpuOverheadPct | null | <= 3 | 판정 불가 — REST 비활성 기준선(baseline.json) 없음 |

## G2: UNVERIFIED (기준 충족: true)

| 항목 | 값 | 기준 | 결과 |
|---|---|---|---|
| readToSdbMappingPct | 100 | >= 99 | 충족 |
| blockingUnitMismatches | 0 | <= 0 | 충족 |
| decimalCommaSuspects | 0 | <= 0 | 충족 |
| gpuCount | 2 | == 2 | 충족 |
| cpu.tctl | 64.8 | present | 충족 |
| cpu.usage | 41.2 | present | 충족 |
| gpu[0].coreTemp | 66.5 | present | 충족 |
| gpu[0].power | 332.5 | present | 충족 |
| gpu[0].load | 97 | present | 충족 |
| gpu[1].coreTemp | 44 | present | 충족 |
| gpu[1].power | 61.2 | present | 충족 |
| gpu[1].load | 12 | present | 충족 |
| ram.usedMB | 36864 | present | 충족 |

## G3A: NOT_RUN

이 수집은 G3A 모드(-Gate G3A)가 아님

## G3B: NOT_RUN

WireView 동시 접근 시험은 사용자 별도 승인 전 금지

## G4S: MANUAL
- [ ] HWiNFO 재실행 후 REST 서버 자동 시작 여부 기록
- [ ] 8764/8765 점유 시 기존 프로세스 무변경 중단(Exit 1)

## G5: RUN_SEPARATELY

- 명령: `python -m unittest discover -s tests -p "test_*.py" -v`
- LAN 노출: NOT_RUN (--allow-lan 은 별도 승인 전 금지)

## 장기 미검증 항목
- 12시간 이상 연속 운용(무료판 제한 여부 포함)
- HWiNFO 재부팅·자동 시작 반복
- WireView 동시 접근(G3B)
- Remote LAN 노출
- 장시간 CPU 오버헤드 추세
