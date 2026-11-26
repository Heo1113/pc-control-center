# results — 실측 결과 없음

**이 폴더에는 사용자 PC에서 측정한 결과가 없습니다.** RC3 작업 환경(클라우드 컨테이너)에 사용자 PC가 연결되지 않아 HWiNFO를 실행·측정할 수 없었습니다. 측정값을 만들어 넣지 않았습니다.

| 폴더 | 내용 | 출처 표시 |
|---|---|---|
| `selftest_dotnet_SYNTHETIC/` | `.NET PoC selftest` 출력. `tests/fixtures/expected`(JS 모델 결과)와 값이 같음 | SYNTHETIC |
| `example_mock_run_SYNTHETIC/` | `Collect-HWiNfoRest.ps1 -Mode Collect`를 **모의 HWiNFO 서버**(`tests/mock_hwinfo_server.py`)에 8샘플 실행한 수집 폴더 + `analyze` 결과. 파일 형식 예시용 | SYNTHETIC (게이트 전부 UNVERIFIED) |

실기 결과는 `docs/REAL_HARDWARE_VERIFICATION_RUNBOOK.md` 절차를 실행하면 이 폴더에 `inventory_*`, `baseline_*`, `run_G1_*`, `run_G3A_*`로 생성됩니다.

## 단기 게이트 현황 (실측 전)

| 게이트 | 상태 | 비고 |
|---|---|---|
| G1 연결·지연·신선도·오버헤드 | 미검증 | 도구·기준 준비 완료, 실측 필요 |
| G2 SDB↔Read·단위 매핑 | 미검증 | 실기 SDB 이름 확인 후 `config/mapping_rules.json` 보정 필요할 수 있음 |
| G3A WireView 단독 | 미검증 | 운영자 확인 필요 |
| G3B WireView 동시 접근 | 실행 안 함 | 별도 승인 전 금지 |
| G4S 운용성(자동 시작·포트 점유) | 미검증 | 수동 기록 |
| G5 자동 테스트 | 합성 데이터로 통과 | 아래 참고, LAN 노출 시험은 실행 안 함 |

## 장기 미검증

12시간 이상 연속 운용(무료판 제한 포함), 재부팅·자동 시작 반복, WireView 장시간 안정성, 장시간 CPU 오버헤드 추세, Remote LAN 노출.
