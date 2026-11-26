# Claude Code 실기 검증 지시서 (`CLAUDE_CODE_VERIFICATION_GUIDE.md`) — RC2

> **RC3 정정 (2026-11-24):** 이 안내의 `Test-HWiNfoRestApi.ps1` 명령과 STUCK 기준은 RC3에서 대체되었습니다. 실기 절차는 `docs/REAL_HARDWARE_VERIFICATION_RUNBOOK.md`, 판정 기준은 `config/gates.json`을 따르세요(`poc/Collect-HWiNfoRest.ps1` + `poc` PoC `analyze`). 합성·오프라인 데이터로는 PASS가 나오지 않습니다.

> **실행 주체:** 실제 IRON 개발 PC의 Claude Code  
> **포트 통일 기준:** Basalt `127.0.0.1:8080` (절대 미변경) · HWiNFO 후보 `127.0.0.1:8764` · IRON Remote Relay `127.0.0.1:8765`  
> **전제:** 본 지시서의 모든 검증은 **단기 실기 검증(Short-term PoC)**이며, 24시간 연속·절전 복귀 등 **장기 안정성 검증(Long-term Soak)**과 명확히 구분해 보고한다.

---

## 1. 절대 준수 가드레일

1. **기존 정본 무변경:** IRON Stage 1 WPF 구현, `main` 브랜치, Basalt(`127.0.0.1:8080`) 운영 설정, 하드웨어 보호 설정(WireView 컷오프/알람, GPU 전력 제한 등)을 **절대 수정·삭제·재시작하지 않는다.**
2. **포트 충돌 시 기존 프로세스 종료 금지:** `8764` 또는 `8765` 포트가 이미 점유된 경우 점유 PID·프로세스명만 보고하고 즉시 중단한다.
3. **읽기 전용 원칙:** HWiNFO REST API에 대해 `POST /api/sensors/filter/*`, `POST /api/sensors/logging/*`를 호출하지 않는다(부정 테스트는 IRON 중계 서버 `8765`를 대상으로만 실행).
4. **WireView 동시 접근 금지:** Phase A(공급자별 단독 접근)만 기본 수행하며, Phase B(동시 접근)는 사용자의 명시적 별도 승인 전까지 실행하지 않는다.
5. **격리 폴더 실행:** RC2 ZIP을 기존 저장소 외부(예: `C:\IRON_Stage2_RC2\`)에 압축 해제해 실행한다.

---

## 2. 사전 준비 (실행 전 포트 점유 및 PID 확인)

```powershell
# Basalt(8080), HWiNFO 후보(8764), IRON Remote(8765) 포트 점유 상태 확인 (읽기 전용)
foreach ($p in @(8080, 8764, 8765)) {
    $conns = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if ($conns) {
        foreach ($c in $conns) {
            $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
            Write-Host "PORT $p IN USE -> PID=$($c.OwningProcess) Name=$($proc.ProcessName) Addr=$($c.LocalAddress)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "PORT $p FREE" -ForegroundColor Green
    }
}
```

- `8080`은 Basalt 점유가 정상이며 건드리지 않는다.
- `8764`가 비어 있음을 확인한 뒤 사용자가 수동으로 HWiNFO 8.61 Beta+ Sensors 창 → **Remote Center**에서 `IP Address: 127.0.0.1`, `Port: 8764`, `HTTPS: 해제(1차 검증)`으로 `Start`한다.

---

## 3. 단계별 검증 절차 (G1 ~ G5 통일 명령)

### Step 1 — Gate G1 & G2: API 연결·지연·SDB↔Read 매핑·오버헤드 (60회 / 1초)

```powershell
cd C:\IRON_Stage2_RC2\poc
pwsh -ExecutionPolicy Bypass -File .\Test-HWiNfoRestApi.ps1 `
  -BaseUrl "http://127.0.0.1:8764" `
  -Samples 60 `
  -IntervalMs 1000 `
  -OutRoot "C:\IRON_Stage2_RC2\results"
Write-Host "PowerShell ExitCode = $LASTEXITCODE"
```

- **PASS 기준:** `ExitCode == 0`, 성공률 `60/60 (100%)`, `Version == 1`, Readout P95 `< 250 ms`, RTT P95 `< 300 ms`, 타임스탬프 단조성 위반 `0건`, Read→SDB 매핑률 `≥ 99%`, HWiNFO64 추가 CPU 점유율 `< 3%`.
- 고유 실행 폴더(`results\run_yyyyMMdd_HHmmss_<id>\`) 내 `summary.json`, `sdb_read_mapping_report.json`, `keyword_matches.json`, `samples_all.jsonl`, `latency_report.csv`를 보존한다.

### Step 2 — .NET 10 PoC 실행 검증 (10회 / 1초)

```powershell
cd C:\IRON_Stage2_RC2\poc
dotnet run --project .\HWiNfoRestPoC.csproj -- "http://127.0.0.1:8764" 10
Write-Host ".NET 10 PoC ExitCode = $LASTEXITCODE"
```

- **PASS 기준:** `.NET 10` 빌드·실행 성공(`ExitCode == 0`), `Version == 1` 검증 통과, 정확한 메타데이터 키(`tSensorLocation`, `tReadingLocation`, `tReadingType`) 제외 및 동적 장치(CPU/Dual GPU/RAM/NVMe/WireView) 파싱 오류 `0건`.

### Step 3 — Gate G3-A: WireView Pro II 단독 접근 검증 (10분 / 600회)

> **선행 조건:** 기존 IRON 직접 USB 점유 및 공식 `WireView2-SW`가 **완전히 종료**되었는지 확인 후 HWiNFO 단독으로만 수행한다. (Phase B 동시 접근은 사용자 별도 승인 없이는 수행 금지.)

```powershell
cd C:\IRON_Stage2_RC2\poc
pwsh -ExecutionPolicy Bypass -File .\Test-HWiNfoRestApi.ps1 `
  -BaseUrl "http://127.0.0.1:8764" `
  -Samples 600 `
  -IntervalMs 1000 `
  -OutRoot "C:\IRON_Stage2_RC2\results_wireview_solo"
```

- **PASS 기준:** `keyword_matches.json`에 WireView Pro II 6핀 개별 전류·전압·전력·커넥터 온도 전 항목 노출, 600회 부하 중 값 고정(`STUCK`) `0건`, USB 단절 `0건`.

### Step 4 — Gate G4-S: 운용성 단기 확인

1. HWiNFO 종료 후 재실행하여 현재 설치 빌드에서 `127.0.0.1:8764` REST 서버가 자동 시작되는지 여부를 기록한다(Build 6125 기준 미지원).
2. `Test-HWiNfoRestApi.ps1` 및 `hwinfo_relay_poc.py`가 포트 충돌 시 기존 프로세스를 종료하지 않고 `ExitCode == 1`로 안전 중단하는지 확인한다.

### Step 5 — Gate G5: 자동 부정 테스트 및 IRON Remote 실기 검증

```powershell
cd C:\IRON_Stage2_RC2
# 1) 자동 단위/부정 테스트 실행 (오프라인 Mock 서버 기반 — 포트 8764/8765 무간섭)
python -m unittest discover -s tests -p "test_*.py" -v

# 2) 로컬 루프백 전용 중계 서버 실행 (기본 127.0.0.1:8765, 실행별 무작위 토큰 자동 생성)
python .\poc\hwinfo_relay_poc.py --hwinfo-url "http://127.0.0.1:8764" --port 8765
```

- **iPhone Safari LAN 테스트가 필요할 때만** 명시적으로 `--allow-lan`을 추가한다:
  ```powershell
  python .\poc\hwinfo_relay_poc.py --hwinfo-url "http://127.0.0.1:8764" --port 8765 --allow-lan
  ```
- **PASS 기준:**
  - 자동 부정 테스트 100% PASS (무토큰 `401`, 오토큰 `401`, 모든 POST `403`, 미존재 경로 `404`, 잘못된 `Version` 거부).
  - 브라우저 개발자 도구(Network 탭)에서 외부 CDN 요청 `0건`.
  - `LIVE` 모드 전환 시 시뮬레이션 데이터 잔존 `0건`, 동적 장치 감지 정상, 미매핑 센서(예: 프로세스 VRAM) `N/A` 표시.

---

## 4. 결과 보고 템플릿 (단기 vs 장기 분리)

| 구분 | 게이트 | 실측 수치 / 로그 경로 | 판정 (`PASS`/`FAIL`/`UNVERIFIED`) |
|---|---|---|---|
| 단기 PoC | **G1** (60회, P95, 오버헤드) | | |
| 단기 PoC | **G2** (SDB↔Read 매핑률, 동적 장치) | | |
| 단기 PoC | **G3-A** (WireView 단독 600회, STUCK/단절) | | |
| 단기 PoC | **G4-S** (재시작 자동구동, 포트 충돌 안전중단) | | |
| 단기 PoC | **G5** (부정 테스트, 무CDN, LIVE/N/A 분리) | | |
| **별도 승인** | **G3-B** (WireView 동시 접근) | *(별도 승인 전 미수행)* | `UNVERIFIED` |
| **장기 검증** | **G4-L1** (12h Free 만료 / 24h+ Pro 연속) | *(단기 PoC 범위 외)* | `UNVERIFIED` |
| **장기 검증** | **G4-L2** (절전/재부팅 복구 및 장시간 부하) | *(단기 PoC 범위 외)* | `UNVERIFIED` |
