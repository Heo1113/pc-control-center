# Strata 연동 근거

확인한 원본: https://github.com/Niko1221/Strata (MIT), 커밋 `c62d5e3` (2026-10-31), 문서상 앱 0.2.9 / 엔진 0.1.55.

## 사용하는 공개 엔드포인트 (모두 GET, 읽기 전용)

| 엔드포인트 | 근거 문서 | 쓰는 값 | 인증 |
|---|---|---|---|
| `GET /v1/telemetry` | `docs/TERMINAL_TELEMETRY_FIELDS.md` ("authoritative read-only response"), `docs/TERMINAL.md` | `schema_version`(1만 허용), `sampled_at`, `instance`, `model`, `work`(슬롯·대기·거부·시간 초과), `rates`(answer/prefill tok/s, 출력 카운터), `recent[]`(완료 요청 수치), `totals`, `activity.events[]` | API 키 또는 읽기 전용 텔레메트리 키 |
| `GET /metrics` + `GET /v1/status` | 같은 문서: v1이 **404일 때만** 대체 | 구버전 서버의 상태·속도·최근 요청 | API 키 |
| `GET /v1/status` (단독) | 같은 문서: `/metrics`도 404일 때 "status-only" | 모델·컨텍스트·엔진 버전 (실시간 값 없음 → "제한적 연결") | API 키 |
| `GET /health` | `docs/DETAILS.md` Quick start / Using it | 서비스 이름, 로드 상태, API 키 설정 여부 | 없음 |
| `GET /v1/status` | `serve/server.py` `v1_status()` | 엔진 버전, 가동 시간, 동시 처리 슬롯, 비전 | API 키 |
| `GET /props` | `docs/DETAILS.md` "Model properties" | 기본 생성 설정(temperature, top_p, top_k, min_p, repeat_penalty, presence/frequency_penalty, repeat_last_n, n_predict), 컨텍스트, 슬롯, 모델 파일 **이름만**, 엔진 빌드 | API 키 |
| `GET /v1/models` | `docs/DETAILS.md` | 모델 ID와 별칭 | API 키 |

## 사용하지 않는 것

- `GET /status`: 문서와 코드(`serve/server.py`, issue #212 주석)에 따르면 마지막 답변의 끝부분을 포함할 수 있어 호출하지 않습니다.
- `POST` 전체(추론, `/slots`, `/v1/load`, `/v1/unload`, `/v1/vram` 등), 관리 브리지(`python -m strata_control.management`), Strata 파일·설정·프로세스.
- `/props`의 `chat_template`, 모델 파일 전체 경로(사용자 이름이 들어갈 수 있음).

## 클라이언트 규칙 (Strata 자체 모니터 클라이언트 `strata_cli/client.py`와 동일)

- 서버 루트 URL만 허용, URL에 자격 증명 금지, 원격은 https만.
- 리디렉션을 따르지 않음, 응답 2 MiB 제한, `application/json`만 허용.
- 키는 `Authorization: Bearer`로만 보내고 URL·명령줄에 넣지 않음. `/health`에는 키를 보내지 않음.
- 서버 데이터는 신뢰하지 않음: 타입이 틀리면 null, 숫자는 유한값만, 문자열은 제어 문자 제거·길이 제한, 목록 개수 제한, activity 이벤트는 순서·형식 검증.
- `null`은 0이 아니라 "값 없음"으로 유지합니다. 전체 출력 처리량은 같은 인스턴스의 카운터 두 번 사이에서만 계산합니다(계산값 표시).

## 오류 분류

| 상황 | 분류 | 화면 상태 | 재시도 |
|---|---|---|---|
| 연결 거부, 이름 해석 실패 | 연결 오류 | 연결 안 됨 | 1→2→4…30초 |
| 제한 시간 초과 | 시간 초과 | 응답 시간 초과 | 지수 백오프 |
| 401/403 | 인증 오류 | 인증 필요 | 30초 |
| `schema_version` ≠ 1, JSON 아님, 형식 오류, 알 수 없는 4xx, 알려진 엔드포인트 없음, 다른 서비스 | **API 호환성 오류** | API 호환성 오류 | 30초 |
| 5xx | 서버 오류 | 서버 오류 | 지수 백오프 |
| 3xx, TLS 오류 | 설정 오류 | 설정 오류 | 30초 |

## Strata 데스크톱 앱과의 관계 (실기 확인, 2026-11-01)

사용자 PC(Strata 데스크톱 앱)에서 확인한 내용과 소스 근거입니다.

- `apps/host/server/native-control.mjs`의 `loadEngine()`은 모델을 로드할 때마다 엔진 API 키(`randomBytes(32)`)와
  포트(`freePort()`, 18500부터 최대 20개 위)를 새로 만들고, 엔진을 `127.0.0.1`에만 엽니다. 앱 호스트(47831)는 `/v1/telemetry`를 공개하지 않습니다.
- 앱의 Serving API 키로 엔진 `/v1/telemetry`를 요청하면 401입니다(사용자 PC에서 확인).
- 엔진(`serve/server.py`)은 시작할 때 환경 변수 `STRATA_TELEMETRY_KEY`(또는 설정 `telemetry_read_key`)를 읽기 전용 텔레메트리 키로 받습니다.
  데스크톱 앱은 엔진을 띄울 때 환경 변수를 복사해 넘깁니다(`control-runtime.mjs`의 `sourceServerEnvironment`/`controlEnvironment`,
  `strata_control/runtime.py`의 `os.environ.copy()`; `STRATA_API_KEY`만 제거).
- **사용자 PC에서 확인함:** 사용자 환경 변수 `STRATA_TELEMETRY_KEY`를 설정하고 Strata 앱을 다시 시작한 뒤, 이 프로그램이 같은 키로
  `127.0.0.1:18500`의 `/v1/telemetry`에 연결되었습니다(상태 "연결됨", 모델 ID·컨텍스트 표시). 엔진 버전·`/props`·`/v1/models`는 텔레메트리 키 권한 밖이라 화면에서 숨깁니다(`/v1/status`가 401이면 나머지는 묻지 않음).
- 포트는 로드마다 바뀔 수 있어, 포트 자동 찾기가 18500~18520에서 `/health`(인증 없음, `service: "strata"`)로 엔진을 찾습니다.
  여러 엔진이 있으면 로드된 엔진 중 텔레메트리 키를 받아 주는 엔진을 고릅니다. 탐색 중에는 키를 보내지 않습니다.
- 내부 파일(`native-instances/*.json` 등)은 읽지 않습니다.

> 정정: 이 문서의 이전 판은 "데스크톱 앱 엔진의 텔레메트리는 공개 인터페이스로 읽을 수 없다"고 적었습니다.
> `STRATA_TELEMETRY_KEY` 환경 변수 경로를 빠뜨린 잘못이었고, 위와 같이 고쳤습니다.

## 로컬 공유(LAN 공유)를 쓰지 않는 이유

Strata 앱의 "로컬 공유"(`apps/host/server/local-share.mjs`)는 `0.0.0.0:8792`에서 `/v1/models`, `/v1/chat/completions`, `/v1/messages`만
허용하고 나머지는 404를 돌려줍니다. 텔레메트리는 여기서도 공개되지 않고, 47831에서 이미 쓸 수 있는 API와 같습니다.
켜면 같은 네트워크의 다른 장치에 HTTP로 모델이 공개되므로, 이 프로그램은 로컬 공유를 요구하지 않습니다.

