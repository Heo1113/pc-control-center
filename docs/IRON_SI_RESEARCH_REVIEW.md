# System Informer 조사서 대조 결과 (IRON 프로세스 식별)

- 대상: 사용자께서 첨부하신 「IRON System Informer Research」(System Informer·Process Explorer·Autoruns·Icaros 조사, 190줄)
- 비교 기준: `feat/iron-process-identity` @ a9d69f4(승인본)과 이 통합 브랜치
- 방법: 조사서의 개념·문서화된 Windows API만 대조했습니다. System Informer 저장소의 소스는 열람하거나 옮겨 오지 않았습니다. 재사용한 코드는 없습니다.

## 1. 이미 구현되어 있던 것 (변경 없음)

| 조사서 항목 | IRON 구현 |
|---|---|
| 신규 (PID, 생성 시각)만 비동기 조회, 유계 채널, 1~2 작업자, BelowNormal | `ProcessIdentityResolver` (채널 256, 가득 차면 다음 주기에 다시 시도) |
| 경로 캐시·프로세스 캐시, 종료 프로세스 정리 | `FileIdentityCache`(512, 파일 변경 시 다시 읽음), `ProcessIdentityCache`(LRU, `EvictExcept`) |
| `ProcessCommandLineInformation(60)` 저권한 명령줄 | `WindowsProcessQuery.QueryCommandLine` |
| `GetFileVersionInfoExW(LOCALISED\|NEUTRAL)` 한글 MUI | 사용함(0412 → UI 언어 → 0409 순) |
| `EnumServicesStatusExW(SC_ENUM_PROCESS_INFO)` + `SHLoadIndirectString` | 사용함(15초 TTL, 새 svchost가 보이면 갱신) |
| svchost / dllhost(AppID·CLSID 구분) / rundll32 / taskhostw / WmiPrvSE | `SharedHostClassifier` |
| dllhost 셸 확장 모듈 확인(즉시 + 3초 뒤, 최대 2회) | 사용함 |
| cmd·powershell·python·node·msedgewebview2 파싱 | 사용함(조사서도 "System Informer는 하지 않음, IRON 독립 확장"으로 분류) |
| 공유 호스트 자원 귀속 금지 | 보조 정보로만 표기, 툴팁 경고 문구 |

## 2. 반영한 개선 (실질적으로 유용한 것만)

| 개선 | 근거(조사서) | 효과 |
|---|---|---|
| 역할 사전 48 → 164개 | "약 150~300개 항목" | Windows·GPU·개발·AI 도구 프로세스가 사전으로 이름을 얻음 |
| `java`/`javaw`(-jar, -m, 메인 클래스), `dotnet`(앱 DLL·SDK 명령), `wsl`(배포판·명령), `conhost`(콘솔 주인) | 조사서 끝의 "wsl.exe, javaw.exe, dotnet.exe 파싱 규칙" 제안 | 공용 실행기 뒤의 실제 대상 표시 |
| cmd·PowerShell 스크립트, JAR·앱 경로의 **폴더 이름** | 사례 "명령 프롬프트 · ComfyUI (run_nvidia_gpu.bat)" | `배치 파일 run_nvidia_gpu.bat · 폴더 ComfyUI_windows_portable` |
| svchost **서비스 설명**(레지스트리 Description, 간접 문자열 확장, 캐시)을 역할로 | `PhGetServiceDescriptionKey` 개념 | "이 컴퓨터의 오디오를 관리합니다" 같은 한글 설명(확인) |
| dllhost CLSID `InprocServer32` DLL의 **버전 정보**, Windows 밖 COM 서버 이름 표시 | "InprocServer32 DLL 경로·버전정보" | 서드파티 COM 서버가 무엇인지 확인됨. 공유 호스트 표시는 유지 |
| 보호 프로세스(PPL) 이름 표시 | "PPL은 커널 드라이버 없이 우회 금지, 폴백" | "권한 부족" 대신 "보호 프로세스(PPL) — 읽을 수 없음(정상)" |
| WUDFHost, vmmem·vmmemWSL을 공유 호스트로 | `PhGetProcessKnownTypeEx`의 wudfhost 분류 | 드라이버·가상 머신 합계를 한 앱 사용량으로 읽지 않게 함 |
| node: package.json으로 확인된 node_modules 패키지 이름을 반복하지 않음 | 사례 "Node.js · Claude Code" | `@anthropic-ai/claude-code (node_modules)` |

## 3. 반영하지 않은 것과 이유

| 항목 | 이유 |
|---|---|
| 2단계 무거운 조회(서명 확인 `WinVerifyTrust`) | 카탈로그 서명 확인은 파일마다 수십 ms~초가 걸릴 수 있습니다. 셀 표시는 달라지지 않고 "Microsoft (추정)"을 "확인"으로 바꾸는 효과만 있어, 비용 대비 이득이 작습니다. 필요하면 별도 승인 후 캐시와 함께 추가합니다. |
| MSIX 패키지 로고(AppxManifest) 아이콘 | 대부분의 패키지 앱 실행 파일에 아이콘이 들어 있습니다. 사용자 PC 실기(아이콘 항목)에서 빠진 앱이 확인되면 그때 추가합니다. |
| `ConcurrentDictionary` 무제한 캐시 | 현재의 크기 제한 LRU가 장시간 실행에 더 안전합니다. |
| `DropOldest` 채널 | 오래된 요청을 버리면 일찍 시작한 프로세스가 계속 미식별로 남을 수 있습니다. 현재 방식(가득 차면 다음 주기에 다시)을 유지합니다. |
| 스레드 단위 서비스 태그(SubProcessTag) | 공유 호스트의 CPU를 서비스별로 나누게 되므로, 귀속 금지 원칙(금지 4)에 맞지 않습니다. |
| xCyclopedia 데이터 | 2021년 이후 갱신이 없고 한글이 없습니다. 역할 사전은 이 저장소에서 새로 작성했습니다. |

## 4. 라이선스

- System Informer(MIT)를 포함해 어떤 외부 코드도 복사하거나 옮기지 않았습니다. 조사서가 인용한 함수 이름은 비교 설명에만 썼습니다.
- Process Explorer·Autoruns(Sysinternals 약관)와 Icaros(소스 비공개)는 바이너리·소스 모두 포함하지 않았고, 역공학하지 않았습니다.
- `THIRD-PARTY-NOTICES.md`는 프로젝트 정본이라 직접 고치지 않았습니다. 추가가 필요하다면 아래 행을 수정안으로만 제안합니다.
  - `Iron.ProcessIdentity / Iron.Stage2 — 이 저장소 자체 작성. 외부 코드·NuGet 없음. 문서화된 Win32/NTAPI만 사용. System Informer 등은 개념 참고만.`
