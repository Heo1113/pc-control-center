# IRON Stage 2 — HWiNFO REST API 검증 패키지 RC3

RC2의 잔여 결함을 고친 검증 도구 묶음입니다. **실측 결과는 포함되어 있지 않습니다**(작업 환경에 사용자 PC가 연결되지 않았음). 실기 절차는 `docs/REAL_HARDWARE_VERIFICATION_RUNBOOK.md`.

## 구성

| 경로 | 내용 |
|---|---|
| `config/iron_stage1_thresholds.json` | Stage 1 소스(`feat/iron-ui-stage1@187d533`)에서 추출한 UI 주의 기준. `tools/extract_stage1_thresholds.py`로 재추출·비교 |
| `config/gates.json` | G1~G5 기준의 단일 출처(수집기·PoC·HTML 공통) |
| `config/mapping_rules.json` | 이름은 후보만, 채택은 SDB 단위로 하는 매핑 규칙 |
| `assets/iron-model.js` | 공유 모델(브라우저·Node). 해석·매핑·PARTIAL·INCOMPLETE·STALE·래치·G2 |
| `assets/iron-config.js`, `assets/iron-synthetic.js` | config·합성 픽스처에서 생성(직접 수정 금지) |
| `poc/` (`HWiNfoRestPoC.csproj`, `src/*.cs`) | .NET 10 PoC: `selftest` · `live` · `analyze`. JS 모델과 같은 결과(테스트로 비교) |
| `poc/Collect-HWiNfoRest.ps1` | 실기 수집기: Inventory · Baseline · Collect · Analyze (RC2 `Test-HWiNfoRestApi.ps1` 대체) |
| `poc/hwinfo_relay_poc.py` | 루프백 전용 읽기 릴레이(LAN 옵션 비활성) |
| `IRON_REMOTE_PROTOTYPE.html` | 원격 화면. SIMULATION · LIVE · STALE · OFFLINE · SNAPSHOT 분리 |
| `tests/` | Python(릴레이·일관성·교차 구현·수집기 정책) + Node 모델 테스트 + 픽스처 |
| `docs/` | 백엔드 사실 확인, 명세 대조, 공급자 권고, 실기 절차 |
| `results/` | 합성 자체 시험 출력만(실측 없음) |

## 빠른 확인

```bash
python -I -m unittest discover -s tests -p "test_*.py" -v
```

```bash
node --test tests/js/model.test.js
```

```bash
dotnet run --project poc -c Release -- selftest
```

## 데이터 출처 표시

`LIVE`(브라우저 폴링) · `LIVE_CAPTURE`(수집기가 HWiNFO64 소유 포트에서 기록) · `UNVERIFIED_ENDPOINT` · `OFFLINE_JSON` · `SYNTHETIC`. 게이트 PASS/FAIL은 `LIVE_CAPTURE`에서만 나오며, 나머지는 기준을 넘어도 `UNVERIFIED`입니다.
