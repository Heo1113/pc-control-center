# 실기 검증 절차 (IRON PC, 읽기 전용)

이 절차는 사용자 PC에서 직접 실행합니다. 모든 단계는 **읽기 전용**이며, 어떤 프로세스도 종료하지 않고 HWiNFO·WireView·BIOS의 보호 설정을 바꾸지 않습니다. WireView 동시 접근 시험(G3B)과 LAN 노출은 포함하지 않습니다.

## 0. 준비

1. 이 패키지를 **격리 폴더**에 풉니다. 예: `C:\IRON\RC3` (기존 `main` 체크아웃·운영 배포 폴더와 분리).
2. PowerShell: Windows PowerShell 5.1 또는 PowerShell 7. 스크립트 실행이 막혀 있으면 이 창에서만 허용합니다.

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

3. G2·G3A 판정에는 .NET 10 SDK가 필요합니다(`dotnet --version`). 없으면 수집만 하고 결과 폴더를 전달하면 됩니다.
4. HWiNFO CPU 사용률을 읽으려면 **관리자 PowerShell**이 필요할 수 있습니다(HWiNFO가 관리자 권한으로 실행되기 때문). 읽지 못하면 오버헤드 항목이 UNVERIFIED로 남습니다.

## 1. WireView 동시 접근 피하기 (가장 먼저)

1. WireView 제조사 앱(WireView2-SW 등)과 iUnity를 **사용자가 직접** 종료합니다. 스크립트는 종료하지 않습니다.
2. 그 다음 HWiNFO를 실행합니다(WireView 지원이 켜져 있으면 이제 HWiNFO만 WireView에 접근).
3. 수집기는 SDB에 WireView 센서가 있는데 `config/gates.json`의 `evidence.wireViewClientProcessNames`에 있는 프로세스가 실행 중이면 **수집하지 않고 종료 코드 5**로 멈춥니다. 이 이름 목록은 추정값이므로 1단계 Inventory 결과의 `processes.scan`을 보고 실제 이름으로 고쳐 주세요.

## 2. 인벤토리 (버전·포트·REST·센서 전체)

```powershell
cd C:\IRON\RC3\poc
.\Collect-HWiNfoRest.ps1 -Mode Inventory
```

- 확인 항목: HWiNFO 실행 파일 버전, 8080(Basalt)·8764·8765 점유 프로세스, `/api/ping` 응답, WireView·IRON 관련 프로세스, PnP `VID_0483&PID_5740`(WireView)·`VID_2022&PID_0522`(FLUX).
- REST 서버가 켜져 있으면 `sdb.json`·`read_once.json`(전체 센서 인벤토리)이 `results\inventory_...`에 저장됩니다.
- **8080은 Basalt 그대로 둡니다.** 8764를 다른 프로세스가 쓰고 있으면 HWiNFO 포트를 바꾸지 말고 결과만 기록하세요.

## 3. REST 비활성 기준선 (CPU 오버헤드 비교용)

HWiNFO는 평소 설정(센서 창, 폴링 주기 동일)으로 켜 두고 **REST 서버만 끈 상태**에서:

```powershell
.\Collect-HWiNfoRest.ps1 -Mode Baseline -BaselineSeconds 60
```

8764가 열려 있으면 종료 코드 1로 멈춥니다. 결과: `results\baseline_...\baseline.json`.

## 4. REST 서버 켜기

HWiNFO → Sensors → Remote Center(API) → 주소 `127.0.0.1`, 포트 `8764`, HTTPS 끔(1차) → Start.
(HTTPS를 켜려면 PowerShell 7에서 `-SkipCertificateCheck`를 붙입니다. 루프백 전용.)

## 5. G1 — 연결·지연·신선도·오버헤드 (60샘플, 약 1분)

```powershell
.\Collect-HWiNfoRest.ps1 -Mode Collect -BaselineFile ..\results\baseline_<시각>\baseline.json
```

- 종료 코드: 0 PASS · 3 FAIL · 4 UNVERIFIED(기준선 없음, 출처 미확인 등) · 1 정책/포트 · 2 연결 · 5 WireView 동시 접근 위험.
- 출처는 포트 8764를 연 프로세스가 `HWiNFO64`로 확인될 때만 `LIVE_CAPTURE`입니다. 그 밖에는 PASS가 나오지 않습니다.

## 6. G2 — 인벤토리·단위 매핑 (위 수집 폴더로)

```powershell
cd C:\IRON\RC3
dotnet run --project poc -c Release -- analyze results\run_G1_<시각>
```

- 결과: `gate_report.md`, `gate_report.json`, `sensor_inventory.csv`(SDB 전체 + read 값).
- 미달이면 `sensor_inventory.csv`를 보고 `config/mapping_rules.json`의 이름 규칙을 실제 이름에 맞게 고칩니다(채택은 계속 SDB 단위로만). 고친 뒤 `python -I tools/build_iron_config.py`로 HTML 설정을 다시 만듭니다.
- 소수점이 쉼표(`64,8`)로 오면 `decimalCommaSuspects`로 미달 처리됩니다. 이 경우 Windows 지역 설정이나 HWiNFO 숫자 형식을 확인해 결과에 기록해 주세요.

## 7. PoC 재검증 (실측 데이터로 모델 출력 보기)

```powershell
dotnet run --project poc -c Release -- live http://127.0.0.1:8764 30 results\poc_live_<시각>
```

샘플마다 CPU Tctl, GPU 코어/핫스팟, GPU 전력 합계(PARTIAL 여부), WireView 상태(OK/INCOMPLETE/N/A)를 출력합니다. 이 모드는 포트 소유 프로세스를 확인하지 않으므로 출처가 `UNVERIFIED_ENDPOINT`이며 게이트 판정용이 아닙니다.

## 8. G3A — WireView 단독 접근 (선택, 600샘플, 약 10분)

1단계 조건(다른 WireView 클라이언트 종료)을 다시 확인한 뒤:

```powershell
.\Collect-HWiNfoRest.ps1 -Mode Collect -Gate G3A -OperatorAttestsHWiNFOExclusive -BaselineFile ..\results\baseline_<시각>\baseline.json
cd ..
dotnet run --project poc -c Release -- analyze results\run_G3A_<시각>
```

수집 중 10샘플마다 다른 WireView 클라이언트를 다시 확인하고, 나타나면 즉시 멈춥니다(종료 코드 5).

## 9. G4S — 수동 기록

- HWiNFO를 종료 후 다시 실행했을 때 REST 서버가 자동으로 켜지는지 기록.
- 8765를 다른 프로그램이 쓰는 상태에서 릴레이를 실행하면 기존 프로그램을 건드리지 않고 종료 코드 1로 멈추는지 기록.

## 10. G5 — 자동 테스트 (Python 3, 선택: node·dotnet·pwsh)

```powershell
cd C:\IRON\RC3
python -I -m unittest discover -s tests -p "test_*.py" -v
```

## 11. HTML 원격 화면 (루프백만)

- HWiNFO 직접: `IRON_REMOTE_PROTOTYPE.html`을 열고 연결 탭에서 Base URL `http://127.0.0.1:8764`, 토큰 공란.
- 릴레이 경유: `python poc\hwinfo_relay_poc.py` 실행 → 브라우저에서 `http://127.0.0.1:8765` → 콘솔에 출력된 토큰 입력. 토큰은 파일에 저장되지 않으며 결과에 붙여 넣지 마세요.

## 12. 결과 전달

`results\` 아래 `inventory_*`, `baseline_*`, `run_*` 폴더를 압축해 전달하면 됩니다. 이 파일들에는 하드웨어 이름·프로세스 경로·센서 값이 들어 있고, 토큰·자격 증명은 들어 있지 않습니다.
