# GPU 메모리 분석 (하드웨어 › GPU 메모리)

Windows에서 GPU 메모리가 **어느 어댑터의 어느 프로세스에** 쓰이는지를 Windows가 보고하는 범위 안에서 보여 주는 화면과, 그 근거입니다.
참고한 오픈소스 [gputaskman](https://github.com/shanselman/gputaskman)(Scott Hanselman, MIT, 커밋 `15ec959`, 2026-11-13)의 실제 구현을 읽고, 기존 PC Control Center의 .NET 구조에 맞게 새로 작성했습니다.
gputaskman의 코드는 복사하지 않았습니다(Python → C#로 따로 작성). Python 프로세스도 실행하지 않습니다.

## 1. gputaskman에서 확인한 구현 원리

### 1.1 프로세스별 GPU 메모리 (Windows, `pdh.py`·`dxgi.py`·`collector.py`)

| 항목 | gputaskman 구현 (소스) | 값의 성격 |
|---|---|---|
| 프로세스 전용(VRAM) | PDH `\GPU Process Memory(*)\Local Usage`, 추가가 안 되면 `Dedicated Usage` (`pdh.py` `_COUNTERS`) | 측정 |
| 프로세스 공유(시스템 RAM) | `\GPU Process Memory(*)\Non Local Usage`, 대체 `Shared Usage` | 측정 |
| 프로세스 커밋 | `\GPU Process Memory(*)\Total Committed` | 측정 |
| "Paged" | `max(Total Committed − Local − Non Local, 0)` (`ProcGpu.paged_out`) | **추정** (README: "eviction trace가 아님") |
| 어댑터 전용·공유 | `\GPU Adapter Memory(*)\Dedicated Usage` / `Shared Usage` | 측정 |
| 프로세스 합계 | 측정된 전용 값만 더함, 하나라도 N/A면 합계 N/A (`Snapshot.attributed`) | 계산 |
| 엔진 사용률 | `\GPU Engine(*)\Utilization Percentage`, 가장 바쁜 엔진 | 측정(비율) |

- **카운터 선택 이유** (`pdh.py` 주석, README "DWM, Spill, and Paged"): 프로세스별 `Dedicated Usage`는 다른 프로세스와 공유한 리소스까지 세어, 같은 표면이 앱·`csrss.exe`·`dwm.exe`에 중복 집계됩니다. gputaskman은 16 GB 카드에서 dwm.exe가 `Dedicated Usage` 약 15.3 GiB, `Local Usage` 약 1.4 GiB로 읽힌 사례를 기록했습니다. 다만 "모든 드라이버에서 합이 정확히 맞는다는 보장은 아니다"라고 밝힙니다.
- **카운터 이름**: 영어 이름으로 추가(`PdhAddEnglishCounterW`)해 한국어 Windows에서도 동작합니다.
- **PID 연결**: 인스턴스 이름 `pid_<PID>_luid_0x<상위>_0x<하위>_phys_<n>`에서 PID와 어댑터 LUID를 읽고, psutil로 이름·경로·명령줄·생성 시각을 얻습니다. 생성 시각이 달라지면 PID 재사용으로 보고 캐시를 버립니다(`ProcInspector.get`).
- **어댑터 연결**: DXGI `EnumAdapters1`/`GetDesc1`의 LUID로 카운터와 짝짓습니다. 이름은 PnP(`DisplayConfigGetDeviceInfo` + SetupAPI)로 확인하고, PCI 위치는 `D3DKMTQueryAdapterInfo`로 얻습니다. NVML 장치는 **PCI 버스 ID로만** 짝짓고 이름·순서로는 짝짓지 않습니다(`select_nv_device`).
- **다중 GPU**: 선택한 어댑터 하나씩 수집기를 둡니다(`--gpu N`, NVIDIA 먼저, TCC GPU는 맨 뒤). README에는 **Windows 다중 GPU는 하드웨어로 검증하지 않았다**고 적혀 있습니다.
- **프로세스 시작·종료**: 와일드카드 카운터라 수집할 때마다 인스턴스가 새로 펼쳐집니다. 전용 1 MiB·공유 16 MiB·비상주 16 MiB 미만 행은 숨깁니다(`_is_live`).
- **권한 제한**: 보호된 프로세스는 이름만 보이거나 접근이 거부됩니다. **WDDM에서 NVML은 프로세스별 메모리를 N/A로 보고합니다.** NVIDIA 문서에 따르면 `nvmlProcessInfo_t.usedGpuMemory`는 Windows 커널이 메모리를 관리하는 WDDM에서 항상 `NVML_VALUE_NOT_AVAILABLE`입니다([NVML API](https://docs.nvidia.com/deploy/nvml-api/api/structnvmlProcessInfo__t.html)). 그래서 프로세스별 값은 PDH가 유일한 출처이고, TCC 모드 GPU에서만 NVML을 씁니다(`tcc.py`).

### 1.2 로컬 LLM 메모리 (`llm.py`·`gguf.py`·`procinfo.py`)

모델 증거는 `api` > `mapped`(메모리 매핑된 파일) > `cmdline` > `path-arg` > `installed` 순서입니다. 세그먼트 크기의 출처는 다음 세 가지입니다.
1. llama.cpp/Ollama **로드 로그**의 버퍼 크기(`model/KV/compute buffer size`): 로드 시점에 런타임이 보고한 할당. 현재 상주량의 증거는 아님.
2. 로컬 서버 API(llama-server `/props`·`/slots`·`/metrics`, Ollama `/api/ps`의 `size`·`size_vram`, LM Studio): 런타임 보고.
3. GGUF 헤더 + 실행 인자(`-ngl`, `-c`, `-ctk/-ctv`, `-np`): **추정**. 텐서 오프셋으로 층별 가중치 바이트를 구하고, 토큰당 KV를 `Σ층 n_kv_heads × (k_len + v_len) × 원소 바이트`로 잡아 `× n_ctx`. 이 KV 세그먼트에는 "est."가 붙습니다.

| 항목 | gputaskman이 식별하는 방식 | 분류 |
|---|---|---|
| 모델 가중치 | 로그의 model buffer / GGUF 텐서 크기 × llama.cpp 배치 가정(입력 임베딩은 CPU) | 런타임 보고 / 추정 |
| KV 캐시 | 로그의 KV buffer / GGUF 구조 × 컨텍스트 × 타입 (슬라이딩 윈도면 상한) | 런타임 보고 / 추정 |
| 추론 작업 버퍼 | 로그의 compute buffer / 프로세스 총량에서 뺀 나머지 | 런타임 보고 / 계산(나머지) |
| CUDA·드라이버 오버헤드 | 직접 측정 없음. "runtime and other overhead" 나머지에 섞임 | 알 수 없음 |
| 시스템 RAM 오프로딩 | 로그의 CPU 쪽 가중치(부분 오프로드 메모), `Non Local Usage`는 "spill"이라 표시하되 "고정 버퍼 포함 가능, 오프로딩 증거 아님" | 런타임 보고 / 측정(용도 불명) |
| VRAM → RAM 이동 | 직접 관측 없음. "Paged"는 커밋 차이 추정 | 추정 |
| 모델별 메모리 | 프로세스 단위. 한 프로세스 안 여러 모델의 KV 분리는 미지원(README) | 프로세스 측정 + 모델 추정 |
| 복수 모델 동시 실행 | 프로세스마다 행. Ollama 러너가 여럿이면 로그를 쓰지 않음 | 프로세스 측정 |

gputaskman의 llama.cpp 대조(RTX 4080 SUPER, llama.cpp b12061): Qwen3-0.6B Q8_0 가중치 추정 604.15 MiB = 로그 604.15 MiB, KV(8,192, f16) 896 MiB = 896 MiB, Gemma-3-270M KV 추정 72 MiB는 슬라이딩 윈도 때문에 실제 49.5 MiB(상한).
이 대조는 **llama.cpp 배치 규칙**에 대한 것입니다. Strata는 자체 엔진(전문가 캐시·VRAM 탄력 조정 등)이라 이 식이 그대로 맞는다는 근거가 없습니다(아래 3.2).

### 1.3 재사용성

| 항목 | 판단 |
|---|---|
| Windows PDH (`pdh.dll`) | .NET에서 `LibraryImport`로 직접 사용 (채택) |
| DXGI 어댑터 목록 | COM vtable을 함수 포인터로 호출 (채택, 장치는 만들지 않음) |
| 프로세스 이름·부모·시작 시각 | Toolhelp32 + `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `GetProcessTimes` (채택) |
| 포트 소유 프로세스 | `GetExtendedTcpTable` (채택, 지정한 포트만) |
| 기존 NVML 래퍼(A) | 확장하지 않음. WDDM에서 프로세스별 메모리가 없고, PCI 버스 ID 짝짓기는 모듈 A 수정이 필요 |
| psutil·pynvml·Textual·Python | 쓰지 않음. 상시 Python 프로세스 없음 |
| 메모리 매핑 파일·명령줄로 모델 식별 | 쓰지 않음. 다른 프로세스의 메모리 맵·명령줄을 읽어야 하고(경로·API 키 노출 위험), Strata 엔진이 GGUF를 매핑한다는 근거도 없음 |
| 권한 | 모두 일반 사용자 권한. 관리자 권한·커널 드라이버 불필요 |
| 라이선스 | MIT. 코드를 복사하지 않아 고지 의무는 없으나, 설계 참고로 `THIRD-PARTY-NOTICES.md`에 적음 |

## 2. 기존 PC Control Center에 이미 있던 것

- **장치 단위 VRAM**: A의 NVML 센서(`NvmlGpuProvider`)가 `SharedHardwareHub`를 거쳐 사용량/전체를 읽고, A·B·C가 각자 DB에 기록합니다. 대시보드·하드웨어 화면에도 표시됩니다.
- **Strata 텔레메트리**: A가 모델 ID·로드 여부·컨텍스트·슬롯별 보유 토큰·요청 통계를 읽습니다(`/v1/telemetry`, 텔레메트리 키).
- **없던 것**: 프로세스별 GPU 메모리, 전용/공유 구분, Windows GPU 카운터, 어댑터 LUID, 프로세스 트리로 Strata 엔진을 찾는 기능.

## 3. 새로 구현한 것

```
PcControlCenter.exe
├─ SharedHardwareHub (그대로) ─ NVML·CPU·RAM, 전용 스레드 1개
└─ GpuMemoryMonitor (새로, src/PcControlCenter.GpuMemory) ─ 전용 스레드 1개, NVML을 열지 않음
     ├─ WindowsGpuMemoryProvider: PDH 카운터 5개 · DXGI · Toolhelp/GetProcessTimes · GetExtendedTcpTable(지정 포트만)
     ├─ MockGpuMemoryProvider (--mock-sensors, 테스트, 스모크)
     └─ 메모리 내 기록 30분 (DB 저장 없음)
          ▼ (불변 스냅샷)
     GpuMemoryViewModel (하드웨어 › GPU 메모리, 1초 UI 타이머) + ServerProcessLinker (Strata 포트 → 프로세스)
```

- **P1 프로세스별 VRAM**: PID·이름·어댑터·전용·공유·커밋·시작 시각·식별 상태. 전용 값 큰 순. 전용 1 MiB, 공유·커밋 16 MiB 미만은 숨기고 개수를 표시합니다.
- **P2 전용/공유**: 어댑터마다 전용·공유 사용량(측정)과 DXGI 용량, 프로세스 전용 합계(계산), 어댑터 − 합계(계산; 음수면 중복 집계 경고).
  NVIDIA 어댑터가 하나이고 NVML 장치도 하나일 때만 공통 센서의 NVML 사용량을 같은 카드에 비교로 보여 줍니다(PCI 위치 없이 여러 장을 짝짓지 않음).
  공유 메모리는 "GPU가 쓰는 시스템 RAM"으로만 설명하고, LLM 오프로딩과 같다고 표시하지 않습니다.
- **P3 Strata**: 로컬 AI 모듈이 연결된 Strata 주소의 포트 → 그 포트를 수신 대기하는 프로세스 하나 → 그 하위 프로세스(최대 3단계)를 찾습니다.
  하위 프로세스는 **부모·자식 시작 시각을 모두 읽을 수 있고 자식이 부모보다 먼저 시작하지 않았을 때만** 연결합니다(재사용된 부모 PID가 오래된 프로세스를 입양하지 않게).
  Strata의 HTTP 서버(Python)는 엔진을 자식 프로세스(`strata --serve`)로 띄우고, 모델은 그 자식이 GPU에 올리므로 자식까지 따라가야 합니다.
  화면에는 연결 근거, 모델 ID·컨텍스트·슬롯 보유 토큰(런타임 보고), 연결된 프로세스의 측정 메모리를 함께 보여 줍니다.
  "가중치·KV 캐시·작업 버퍼"는 **알 수 없음**으로 표시합니다.
- **신선도**: 각 스냅샷에 실제 수집 시각과 그때의 간격을 담습니다. `간격 × 3 + 2초`가 지나거나 리더가 멈추면 값을 숨기고 "새 측정값이 없습니다 — 마지막 측정 …"을 표시합니다.
- **CPU 부담**: 화면을 보는 동안 2초, 그 밖에는 10초 간격입니다. 수집 비용은 화면 상태 줄과 스모크 보고서에 표시합니다. 공통 설정에서 끌 수 있습니다(`ProcessMemoryEnabled`).
- **격리**: 공급자 생성·읽기 예외는 리더 안에서만 처리합니다. 연속 3회 실패하면 30초 쉬었다가 다시 만들고, 카운터가 없으면 60초마다 다시 시도합니다. 공통 센서·모듈·대시보드는 이 리더에 의존하지 않습니다.
- **개인정보**: 실행 파일 이름만 표시합니다. 경로·명령줄은 읽지 않습니다. 스모크 보고서에는 프로세스 이름을 넣지 않습니다.

## 4. 측정 정확도와 알려진 한계

| 값 | 정확도·한계 |
|---|---|
| 프로세스 전용(Local Usage) | Windows 커널의 프로세스별 집계. 리소스 소유 그래프가 아니며, 드라이버에 따라 합이 어댑터 값과 다를 수 있음 |
| 대체 카운터(Dedicated Usage) | 공유 리소스 중복 집계. 쓰일 때 경고 |
| 공유(Non Local Usage) | GPU가 쓰는 시스템 RAM. 고정 버퍼 등 의도된 할당 포함 → 오프로딩 증거 아님 |
| 커밋 | 할당 총량(상주 여부 무관). "VRAM→RAM 이동량"으로 바꾸지 않음 |
| 어댑터 − 프로세스 합계 | 카운터 수집 시점 차이·커널 할당·읽지 못한 프로세스가 섞임. 원인은 구분하지 않음 |
| NVML 비교 | 장치 전체 기준. Windows 카운터와 집계 방식이 달라 값이 다를 수 있음. 수집 스레드·시각도 다름 |
| 프로세스 식별 | 카운터 수집 직후 프로세스 목록을 읽음. 보호된 프로세스·다른 사용자 프로세스는 시작 시각을 읽지 못해 "확인 불가"로 표시 |
| Strata 연결 | 로컬 AI 모듈의 최근 15초 안 조회가 연결 상태일 때만. 원격 주소·포트 소유자 여럿·커널 소유 포트는 연결하지 않음 |
| 다중 GPU | 어댑터마다 카드와 행을 따로 표시. NVIDIA 여러 장이면 NVML 비교를 하지 않음. **복수 GPU 실기 검증 없음** |
| Windows 버전·드라이버 | GPU 카운터는 WDDM 2.0 이상 드라이버에서 제공. 없으면 이유와 함께 N/A. 인스턴스 이름 형식이 다르면 제외 개수를 표시 |

## 5. P3·P4·P5 판단

### 5.1 P3 — Strata가 실제로 주는 정보 (Strata 원본 `13eedf5`, `serve/server.py`·`strata_cli/client.py`)

| 출처 | 메모리 관련 내용 | 이 앱의 사용 |
|---|---|---|
| `GET /v1/telemetry` (텔레메트리 키) | 모델 ID·로드 여부·컨텍스트, 슬롯별 `held_tokens`, `devices[]`(NVML 장치 `mem_used`/`mem_total`), `ram`(호스트 RAM) | 모델·컨텍스트·보유 토큰 표시(A가 이미 읽음). `devices[]`는 공통 센서 NVML과 같은 종류라 쓰지 않음 |
| `GET /v1/status` (API 키) | `vram`(엔진 수치, `POST /v1/vram` 후에만 채워짐), `machine.gpu`(장치 사용량) | 텔레메트리 키로는 401 → 쓰지 않음 |
| `GET /metrics` (API 키) | `engine` 정보(`vram_free_mib`, 전문가 캐시 MiB 등) | API 키 경로라 쓰지 않음(A의 정책 그대로) |
| `POST /v1/vram` | VRAM 예약량을 **바꾸는** 명령 | 절대 호출하지 않음 |

결론: Strata는 프로세스별 VRAM, 가중치/KV 바이트, 양자화를 텔레메트리 키 범위에서 보고하지 않습니다. 그래서 **프로세스 트리 연결 + Windows 측정값**만 구현했습니다.
양자화는 모델 파일 이름에 표기가 있어도 해석하지 않습니다(이름은 사람이 붙인 것이라 근거가 아님).

### 5.2 P4 — 모델 메모리 추정: 이번에 구현하지 않음

- 필요한 입력(층 수·KV 헤드 수·헤드 차원·KV 타입·슬롯 수·배치 규칙)을 Strata가 텔레메트리 키 범위에서 주지 않습니다. GGUF 헤더를 읽으려면 모델 파일 경로가 필요한데, 그 경로는 다른 프로세스의 메모리 맵·명령줄이나 Strata 내부 파일에서만 얻을 수 있습니다(A는 내부 파일을 읽지 않는 정책).
- Strata 엔진은 llama.cpp 배치 규칙과 다른 자체 메모리 정책(전문가 캐시, VRAM 탄력 조정 등)을 써서 gputaskman의 식이 맞는다는 근거가 없습니다.
- 대안(다음 단계 후보): 사용자가 값을 직접 넣는 "계산기"를 **추정** 표시와 함께 별도 화면에 두고, Strata 로그나 llama.cpp 로그와 대조해 검증하는 방법. 식: KV 바이트 ≈ 토큰 수 × Σ층 n_kv_heads × (k_len + v_len) × 원소 바이트.

### 5.3 P5 — 통합 기록: 데이터베이스에 저장하지 않음

- 장치 전체 VRAM은 이미 A·B·C의 DB에 기록되고 통합 기록에 나옵니다.
- 프로세스별 행을 저장하면 저장량이 크게 늘고(예: 30개 프로세스 × 2초 간격 ≈ 하루 130만 행), 프로세스 이름(개인정보)이 남으며, 기존 스키마를 바꾸거나 새 DB를 만들어야 합니다.
- 그래서 이번에는 **메모리 내 30분 기록**(그래프용)만 두고 기존 스키마는 바꾸지 않았습니다.
- 대안: 필요하면 별도의 선택형 `gpu-memory.db`에 상위 N개 프로세스와 어댑터 합계를 10~30초 간격으로 저장하고, 통합 기록에 출처 하나로 추가하는 방식(기존 DB는 그대로).

## 6. 직접 확인하는 방법

1. **작업 관리자** › 세부 정보 › 열 선택 › "전용 GPU 메모리"·"공유 GPU 메모리"로 같은 프로세스를 비교합니다. 작업 관리자 열이 어떤 카운터와 같은지는 확인하지 못했으므로, dwm.exe처럼 공유 리소스가 많은 프로세스는 다를 수 있습니다.
2. **성능 모니터(perfmon)**에서 "GPU Process Memory"(한국어 표시 이름)의 Local Usage·Non Local Usage를 추가하면, 이 화면이 읽는 카운터 자체를 볼 수 있습니다. typeperf는 Windows 표시 언어의 카운터 이름을 씁니다.
3. **nvidia-smi** `--query-gpu=memory.used,memory.total --format=csv`: 장치 전체 VRAM을 "NVML 장치 VRAM" 행과 비교합니다. WDDM에서는 nvidia-smi도 프로세스별 메모리를 N/A로 보여 줍니다.
4. **알려진 부하**: 모델 로드 전·후 strata.exe 행의 "전용" 변화와 nvidia-smi 변화를 비교합니다. 컨텍스트를 바꿔 다시 로드하면 KV 캐시 크기 변화가 프로세스 총량에 나타나는지 볼 수 있습니다(나누어 표시하지는 않음).

## 6.1 CI에서 확인한 것 (GPU 없는 Windows 러너)

- Windows 전용 테스트: PDH 판독 또는 이유, DXGI 어댑터·LUID, 자기 프로세스 시작 시각이 .NET 값과 1 ms 안에서 같음, IPv4/IPv6 루프백 수신 포트·소유자(바이트 순서), 실제 자식 프로세스(ping.exe) 연결 — 모두 통과.
- GUI 스모크 real: 카운터가 Local/Non Local로 추가됨, 카운터 인스턴스의 LUID가 DXGI 어댑터 LUID와 짝지어짐(Basic Render Driver에 프로세스 12개), 측정된 0은 0으로, 카운터가 없는 어댑터는 N/A로 표시, 한 번 읽는 비용 4.7 ms.
- 러너에는 GPU가 없어 VRAM 값의 크기·정확도는 확인하지 못했습니다(아래 7장).

## 7. RTX 5080·Strata 실기에서만 확인할 항목

- RTX 5080(WDDM) 드라이버에서 `Local Usage`/`Non Local Usage` 카운터가 추가되는지, 아니면 대체 카운터로 떨어지는지(화면 상태 줄의 "프로세스 값" 문구).
- Strata 모델 로드 전·후 strata.exe 행의 전용 메모리 변화가 nvidia-smi 장치 사용량 변화와 같은 규모인지.
- 실제 Strata 데스크톱 앱에서 포트(18500~18520) 소유자가 Python 서버이고, 엔진이 그 자식으로 연결되는지. 데스크톱 앱 구성이 바뀌어 손자 이하 단계이거나 시작 시각을 읽을 수 없으면 "연결 안 함"으로 표시됩니다.
- 어댑터 − 프로세스 합계 차이가 작업 관리자의 GPU 메모리 그래프와 비교해 납득할 만한지, dwm.exe 값이 과대하지 않은지.
- 통합 GPU를 켠 상태에서 어댑터 카드가 둘로 나오고 프로세스가 올바른 어댑터에 붙는지(LUID 기준).
- 2초 간격 수집 비용(상태 줄의 "수집 N ms")과 앱 전체 CPU 사용률.
- 4K·175% 배율에서 표·그래프 가독성.
