# IRON 프로세스 식별 모듈 (`Iron.ProcessIdentity`)

기존 Stage 1(`187d533`)의 화면·레이아웃·VRAM 계측·정렬·필터·상세 그래프는 그대로 두고, **기존 프로세스 이름 셀**과 그 툴팁만 바꿉니다.
- **이름 셀:** 아이콘(아이콘을 모르면 실행 파일 첫 글자 표시), 식별된 프로그램 이름(굵게), 그 뒤에 실행 파일 이름(흐리게). 공유 호스트는 "공유 호스트 · svchost.exe"처럼 셀에서 바로 구분됩니다.
- **툴팁:** 한글 역할, 실제 실행 주체, 공유 호스트 보조 정보, 식별 근거, 확인/추정 표시.

브랜치: `feat/iron-process-identity` (187d533 기반, 별도 모듈).

## 1. Stage 1과 Stage 2의 경계

| 구분 | 이 브랜치 |
|---|---|
| Stage 1 행(모의 데이터) | `MockProcessIdentityProvider` → `IdentityComposer.ComposeMock`. 모의 이름·모의 명령줄만 사용하며 **OS 호출은 전혀 없습니다**(모의 PID를 실제 프로세스 표에서 찾지 않음). 모든 값은 "추정"으로 표시되고, 툴팁 끝에 "Stage 1 모의 데이터"라고 적힙니다. |
| 실제 프로세스 해석 | `ProcessIdentityResolver` + `WindowsProcessQuery` (모듈 안에만 있음). **Iron.App에는 연결하지 않았습니다**(Stage 2 승인 범위). 이 사실을 테스트 `Stage1CodeNeverReachesTheRealProcessResolver`가 확인합니다. |
| 실측 | 모듈 단독 측정 도구 `tools/Iron.ProcessIdentity.Bench`를 Windows 러너에서 일반 사용자 계정으로 실행합니다. IRON 프로그램 측정이 아닙니다. |

## 2. 바뀐 Stage 1 파일 (최소)

| 파일 | 변경 |
|---|---|
| `Presentation/ProcessListViewModel.cs` | 행 모델에 `Identity` 속성 1개 추가, `IdentityProvider`(기본: 모의) 추가, 행 생성·갱신 때 식별값 설정. 이름·검색·정렬은 기존 `ProcessReading.Name` 그대로 |
| `Presentation/ProcessIdentityPresentation.cs` (새 파일) | `ProcessIdentityView`, `IProcessIdentityProvider`, `MockProcessIdentityProvider`(최대 256개 캐시) |
| `Theme/IronTemplates.xaml` | 두 행 템플릿(개요·프로세스)의 이름 칸 `TextBlock` → 이름 셀 템플릿 `Iron.ProcessNameCell`(16 DIP 아이콘 또는 글자 표시 + 식별 이름 + 흐린 실행 파일 이름, 툴팁 `{Binding Identity}`). 같은 열(`*`)·같은 셀 스타일·같은 행 높이를 씁니다. 툴팁 내용 템플릿(아이콘 + 글자) 추가 |
| `Views/ProcessesView.xaml` | 검사 패널 제목의 툴팁 1곳 → `Identity` |
| `Hosting/Converters.cs`, `Theme/IronControls.xaml` | 아이콘 픽셀 → 고정된(Freeze) `BitmapSource` 변환기 등록 |
| `Iron.App.csproj`, `Iron.App.Tests.csproj`, `Iron.slnx` | 새 모듈 참조·프로젝트 추가 |
| `Hosting/ScreenRenderer.cs` (CI 렌더링 모드만) | `layout-report.json`의 각 화면에 이름 셀의 픽셀 사각형(`nameCells`)을 추가. 창 동작에는 영향이 없습니다 |

다른 열·열 너비·행 높이·창 배치·계측 방식은 그대로입니다. 행 데이터의 `Name`(실행 파일 이름)도 그대로이며, 검색·정렬은 여전히 이 값을 씁니다. 그래서 셀에 실행 파일 이름을 흐리게 함께 남겼습니다. 회귀 확인 방법은 7장에 적었습니다.

## 3. 표시 우선순위와 확인/추정 구분

1. **공용 실행기 파싱 결과** (svchost 서비스, dllhost COM, WebView2 호스트 앱 등)
2. **Windows MUI 한글 FileDescription** 또는 **서비스 표시 이름**
3. **한글 역할 사전** (`Resources/ProcessRoleDictionary.ko.json`, 내장 리소스)
4. **ProductName / 실행 파일 이름**

각 글자에는 근거가 붙습니다(`서비스 관리자`, `버전 정보`, `레지스트리`, `명령줄`, `역할 사전`, `로드 모듈`).
- 시스템에서 직접 읽은 값은 **확인**으로, 표시가 붙지 않습니다.
- 명령줄 해석·폴더 이름·사전에서 나온 값은 **추정**으로, "(추정)"을 붙입니다.
- 회사 이름이 "Microsoft"인 것은 서명 확인이 아니므로 추정에만 씁니다.
- 이름 셀에는 "(추정)"을 붙이지 않고 이름만 보입니다. 확인/추정 표시와 근거("근거: 역할 사전(모의) · 명령줄")는 툴팁에 있습니다.
- 이름 셀 글자(`CellName`): 공용 실행기는 실행 대상의 첫 부분을 덧붙입니다("Python · train.py"). 공유 호스트는 자기 이름만 보이고, 호스팅 항목은 툴팁에만 둡니다(행의 CPU·메모리·VRAM이 한 항목의 사용량으로 읽히지 않게).

## 4. 공용 실행기 규칙

| 대상 | 규칙 |
|---|---|
| svchost.exe | 서비스 관리자에서 PID별 서비스 목록을 가져옵니다(프로세스를 열지 않으므로 일반 사용자도 가능). 대표 서비스는 `-s` 지정 서비스, 없으면 표시 이름 순 첫 번째이고, 여럿이면 "외 N개"를 붙입니다. `-k 그룹`은 보조 정보입니다. |
| dllhost.exe | `/Processid:{GUID}`는 **AppID**이므로 `HKCR\AppID`를 먼저 봅니다. 같은 GUID의 `HKCR\CLSID`는 별도 등록이라, AppID 값이 같을 때만 "확인", 아니면 "추정"입니다. 썸네일 캐시 서러게이트 등 셸 확장 서러게이트일 때만 로드 모듈을 인스턴스당 최대 2회(즉시 1회, 3초 뒤 1회) 읽고, Windows 폴더 밖 DLL(Icaros, PowerToys 등)을 "로드된 확장"으로 병기합니다. |
| cmd / powershell / pwsh | `/c`, `/k`, `-File`, `-Command`의 대상을 찾고 부모 프로세스를 보조로 붙입니다. `-EncodedCommand`·`-Command`의 내용은 표시하지 않고 cmdlet 이름만 보여 줍니다. |
| python / pythonw / node | `-m 모듈`, 스크립트 이름, 프로젝트 폴더를 찾습니다. 가상환경은 `pyvenv.cfg`로 판단하고 conda 환경도 구분합니다. node는 `package.json`의 name, `node_modules` 패키지를 봅니다. `-c`·`-e`의 내용은 숨깁니다. |
| msedgewebview2.exe | `--webview-exe-name`(확인), `--user-data-dir` 앱 폴더(추정), `--type` 구성 요소를 씁니다. |
| rundll32.exe | `<DLL>,<진입점>`을 분리하고 대상 DLL의 버전 정보를 읽습니다. |
| taskhostw / WmiPrvSE | 공유 호스트로 표시하고, 작업 이름은 추정으로 표시합니다. |

**공유 호스트의 CPU·메모리·VRAM은 특정 서비스·DLL·앱에 나누거나 귀속하지 않습니다.** 툴팁에는 항상 다음 문구가 붙습니다: "공유 호스트: CPU·메모리·VRAM은 이 프로세스 전체 값이며, 아래 서비스·확장 각각의 사용량이 아닙니다."

## 5. 권한·민감정보

- **권한:** UAC도 커널 드라이버도 쓰지 않습니다. 프로세스는 `PROCESS_QUERY_LIMITED_INFORMATION`으로만 열고, 모듈 목록은 dllhost에서만 읽습니다. 실패하면 예외 대신 메모를 남깁니다("명령줄: 권한 부족", "서비스 목록을 확인하지 못함"). PPL·SYSTEM 프로세스는 이름·서비스 등 읽을 수 있는 것만 씁니다.
- **명령줄:** 원본은 해석하는 동안만 메모리에 있고, 캐시·로그·툴팁·리포트에는 남기지 않습니다. 보관·표시는 `Redactor`를 거친 형태뿐입니다. `Redactor`가 지우는 것은 다음과 같습니다.
  - password·token·key·secret 계열 옵션 값
  - URL의 사용자:비밀번호
  - 쿼리의 token=
  - 긴 불투명 문자열(JWT·hex·base64)
  - 사용자 프로필 폴더 이름(→ `%USERPROFILE%`)
- **Remote 노출 없음:** 이 모듈은 네트워크를 쓰지 않고, HWiNFO RC3 포트(8764·8765)·Basalt(8080)와도 관계가 없습니다.
- **기존 화면에 남은 항목:** Stage 1 검사 패널은 원래부터 모의 명령줄을 그대로 보여 줍니다(기존 동작, 변경하지 않음). Stage 2에서 실제 데이터를 연결할 때는 이 자리에 `RedactedCommandLine`을 써야 합니다.

## 6. 캐시

| 캐시 | 키 | 한도·무효화 |
|---|---|---|
| 파일(버전 정보·아이콘) | 경로(대소문자 무시) | 512개 LRU입니다. 60초마다 최대 1회 파일 수정 시각·크기를 확인하고, 바뀌었으면 다시 읽습니다. |
| 프로세스 | (PID, 생성 시각 ticks) | 4096개입니다. 10초마다 종료된 인스턴스를 제거합니다. PID가 재사용되면 생성 시각이 다르므로 다른 키가 됩니다. |
| 서비스 스냅샷 | — | 15초 TTL이고, 처음 보는 svchost PID가 나오면 그 PID당 1회 갱신합니다. |
| COM AppID·CLSID | GUID | 각 256개입니다. |
| Stage 1 모의 | (PID, 이름, 명령줄) | 256개입니다. |

해석은 크기가 정해진 `Channel`(256)과 BelowNormal 전용 스레드 1~2개가 맡습니다. 폴링 루프는 `Observe`(쓰기 시도만 함, 기다리지 않음)와 `TryGet`만 부릅니다. 큐가 차면 그 항목은 다음 폴링 때 다시 넣습니다.

## 7. 검증

- **단위 테스트:**
  - 명령줄 분해(CommandLineToArgvW 규칙)
  - 민감정보 제거
  - 파서 7종(cmd, python, node, webview2, svchost, dllhost, rundll32)과 taskhostw·WmiPrvSE
  - 캐시 키·LRU·무효화·정리
  - 서비스 TTL
  - 해석기: 막지 않음·큐 보류·PID 재사용·모듈 확인 2회 제한·권한 부족 메모
- **Windows 실기 테스트(CI):**
  - 스냅샷 생성 시각
  - 자기 프로세스 명령줄
  - cmd.exe 버전 정보·아이콘
  - 서비스 PID 매핑과 간접 문자열 확장
  - 아이콘 200회 추출 후 GDI 핸들 누수 없음
  - 실제 cmd 자식 프로세스 식별
- **Stage 1 회귀(CI, `tools/ci/Compare-StageScreens.ps1`):**
  - 기존 48개 테스트를 그대로 돌립니다.
  - 같은 모의 데이터·같은 시계로 두 빌드가 모든 화면을 4번씩 그립니다.
  - 각 화면을 이 브랜치 렌더러가 알려 준 **이름 셀 사각형 안과 밖으로 나눠** 비교합니다. 밖은 187d533과 픽셀까지 같아야 통과하고, 안은 바뀐 픽셀 수만 보고합니다.
  - 4번 렌더링한 결과끼리도 비교해, 어느 빌드가 실행마다 달라지는지 따로 표시합니다.
  - 레이아웃 점검(글자 잘림·겹침·창 밖) 수와 바인딩 오류 수가 187d533과 같아야 합니다.
  - 이름 열 일부를 잘라 로그에 남깁니다(이 브랜치·187d533).
  - 실제 창 스모크 테스트를 돌립니다.
- **실측(CI):**
  - 일반 사용자 계정 `ironbench`로 측정 도구를 실행합니다. 리포트에 elevated 여부를 함께 기록합니다.
  - 1초 프로세스 목록 루프를 5분 기준선과 5분 식별 포함으로 비교합니다(CPU·Private·Working Set·GDI·핸들).
  - 도구가 직접 띄운 cmd·powershell·python(venv)·node·WebView2·dllhost와 기존 svchost 등의 식별 전/후를 기록합니다.

## 8. 출처와 라이선스

- **재사용 코드: 없습니다.** System Informer, Process Explorer, Autoruns, Icaros의 바이너리·소스를 포함하거나 열람·역공학하지 않았습니다. 모든 호출은 Microsoft가 문서화한 Win32/NTAPI입니다.
  - OpenProcess, QueryFullProcessImageNameW
  - NtQueryInformationProcess(ProcessCommandLineInformation=60), NtQuerySystemInformation(SystemProcessInformation)
  - GetFileVersionInfoSizeExW/GetFileVersionInfoExW(FILE_VER_GET_LOCALISED|FILE_VER_GET_NEUTRAL)/VerQueryValueW
  - GetPackageFullName, GetApplicationUserModelId
  - OpenSCManagerW, EnumServicesStatusExW(SC_ENUM_PROCESS_INFO)
  - SHLoadIndirectString
  - ExtractIconExW, SHGetFileInfoW, GetIconInfo, GetDIBits, DestroyIcon
  - CreateToolhelp32Snapshot
  - 레지스트리 `HKCR\AppID`·`HKCR\CLSID`
- **개념 수준 참고만 했습니다.** 사용자께서 첨부하신다고 한 System Informer 기반 조사서는 이 세션에 전달되지 않아 보지 못했습니다. 이 모듈은 요청서에 적힌 규칙(서비스 PID 매핑, dllhost AppID/CLSID, 셸 확장 표시)만 따랐습니다.
- **외부 패키지:** 새 NuGet 패키지는 없습니다. 테스트는 저장소에 이미 고정된 `xunit.v3` 4.0.3을 씁니다.
- **역할 사전:** 이 저장소에서 새로 작성한 한글 설명입니다. 썸네일 캐시 GUID `{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}`는 요청서에 적힌 값을 썼습니다.
- `THIRD-PARTY-NOTICES.md`는 정본이라 직접 고치지 않았습니다. 추가할 행은 수정안으로만 제안합니다(보고서 참고).
