# 기존 백엔드 사실 확인 — RC2(Gemini) 가정과 실제 소스 대조

- 대상 소스: `Heo1113/pc-control-center` `main` @ `a080a2f` (읽기만 함, 변경 없음)
- Stage 1 비교 대상: `feat/iron-ui-stage1` @ `187d533`
- 방법: `git grep` / `git show` 로 해당 줄을 직접 확인. 아래 경로:줄 번호는 위 커밋 기준이다.

## 1. 결론 요약

| RC2 문서의 주장 | 판정 | 근거 |
|---|---|---|
| "기존 백엔드는 WireView를 **직접 USB 점유** + `RELEASE USB` 핸드오버 구조 보유 (확인 사실)" (`SENSOR_PROVIDER_MATRIX.md` 29행) | **사실 아님** | `main`의 `.cs`·`.csproj` 전체에서 `wireview`, `SerialPort`, `0x0483` 검색 결과 0건. `RELEASE USB`는 Stage 1의 **모의 동작**뿐이다(`feat/iron-ui-stage1:src/Iron.App/Presentation/DeviceViewModels.cs:207-210`, 문구 끝에 "(모의)"). |
| "기존 백엔드에서 **Antec FLUX PRO 표시 송신** 기능 보유 (확인 사실)" (같은 문서 30행) | **사실 아님** | `main`에서 `flux pro`, `HidD_`, `0x2022` 검색 결과 0건. FLUX 송신도 Stage 1 모의(`DeviceViewModels.cs:316` "송신 중 · 1Hz (모의)")이며, 실제 구현은 A-Slim 계획(Stage 2)에만 있다. |
| "기존 백엔드가 CPU 온도·클럭·전력을 로컬 저지연 조회 가능 (가정: LHM/PawnIO)" (24행) | **사실 아님** | CPU 온도·패키지 전력·실제 클럭은 **수집하지 않음**: `modules/local-ai-performance-center/src/LocalAiPerf.Telemetry/HardwareMonitor.cs:17-25` (`NotCollected`, "커널 드라이버(예: PawnIO)와 관리자 권한이 필요"). LibreHardwareMonitor는 **검토 후 미포함**: `modules/local-ai-performance-center/THIRD-PARTY-NOTICES.md:42`. |
| "듀얼 GPU: NVML/DXGI 기반 고빈도 조회 (가정)" (27행) | **대체로 맞음, 범위 정정 필요** | NVML을 쓴다: `.../Nvml/NvmlApi.cs:76-92`(진입점), 온도는 `NVML_TEMPERATURE_GPU`(=0, `NvmlApi.cs:60`)만 읽는다(`NvmlGpuProvider.cs:93`). **핫스팟·메모리 정션 온도는 수집하지 않는다**(검색 0건). 공통 리더 주기는 최소 500 ms(`src/PcControlCenter.Core/ShellSettings.cs:76`), 최대 간격 기본값 1000 ms(`ShellSettings.cs:83`). "100~250 ms"는 근거 없음. |
| "프로세스별 VRAM: 기존 백엔드 유지 (가정: PDH/DXGI/NVML)" (28행) | **맞음 (방식 확정)** | `src/PcControlCenter.GpuMemory/GpuMemoryMonitor.cs:79`, PDH `GPU Process Memory`·`GPU Adapter Memory` + DXGI: `docs/ARCHITECTURE.md:31`, `docs/GPU_MEMORY.md:13-15`. NVML 프로세스 API는 쓰지 않는다. |
| RAM | **확정** | `GlobalMemoryStatusEx` (+ CPU 사용률은 `GetSystemTimes`): `.../Platform/WindowsSystemProvider.cs:10`, `THIRD-PARTY-NOTICES.md:80`. |
| NVMe 온도·S.M.A.R.T. | **기존 백엔드에 없음** | 온도/SMART 읽기 코드 0건. WinDiag는 `Win32_DiskDrive` 목록(`modules/windows-diagnostics-lab/src/WinDiag.Collection/Snapshots/SnapshotProviders.cs:134`)과 `stornvme` 이벤트(`.../EventCatalog.cs:204-205`)뿐. |
| DIMM 온도 | **기존 백엔드에 없음** | 검색 0건. |
| 센서 읽기 구조 | (RC2에 언급 없음) | `SharedHardwareHub`가 **유일한 하드웨어 리더**: `src/PcControlCenter.Hardware/SharedHardwareHub.cs:39-48` ("No module opens NVML itself"). |
| Basalt 포트 | **확정** | `src/PcControlCenter.Core/ShellSettings.cs:240` `DefaultPort = 8080` → HWiNFO 8764·릴레이 8765 배치는 타당. |

## 2. 정정이 판단에 주는 영향

1. **WireView·FLUX는 "기존 것을 유지할지"의 문제가 아니라 "새로 만들지"의 문제다.** RC2의 방안 A("기존 자체 백엔드 유지")는 WireView·FLUX에 대해서는 존재하지 않는 구현을 전제로 비교했다. 실제 선택지는 (a) HWiNFO REST로 WireView를 읽기 / (b) A-Slim 계획대로 읽기 전용 USB(0x01·0x04) 구현이다.
2. **CPU 온도·전력, DIMM·NVMe 온도는 기존 백엔드가 비어 있는 영역**이라 HWiNFO REST가 주 공급자 후보가 된다(관리자 권한·커널 드라이버 없이 얻을 수 있다면).
3. **GPU 핫스팟·메모리 정션은 기존 NVML 경로에 없다.** HWiNFO가 RTX 50 계열에서 이 값을 내는지는 실기 SDB(G2)로 확인해야 한다. 이번 RC3는 이 값을 표시만 하고 경고에는 쓰지 않는다(Stage 1 경고는 코어 온도 기준).
4. **GPU 코어 온도·전력·부하·VRAM 총량은 NVML이 이미 1개 리더로 읽고 있다.** HWiNFO로 옮길 이유는 G1 지연·안정성 결과가 NVML보다 나을 때뿐이다.

## 3. 확인하지 못한 것

- WireView Pro II·FLUX PRO의 실제 USB 동작(이 저장소에 코드가 없으므로 소스로는 확인할 수 없음).
- 사용자 PC에 설치된 별도 앱(iUnity, WireView 제조사 앱)의 프로세스 이름·동작 — Inventory 모드로 수집해 확인해야 한다.
