# 구조와 기술 선택

## 선택한 것과 이유

| 영역 | 선택 | 이유 |
|---|---|---|
| 언어·GUI | C# / .NET 10 / WPF(Fluent 다크) / MVVM(CommunityToolkit.Mvvm) | 요구 스택. 사용자 기존 A·B 프로젝트와 같은 빌드·배포 방식(단일 EXE, CI)을 그대로 쓸 수 있음 |
| UI 자동화 | FlaUI.UIA3 5.0.0 + 직접 작성한 Win32 호출 | .NET에서 가장 검증된 UIA 래퍼(MIT). 창 열거·DPI·포커스·SendInput·캡처는 Win32로 직접 제어해 검증 지점을 명확히 함 |
| 모델 연결 | OpenAI 호환 `POST /v1/chat/completions`를 직접 구현(SDK 없음) | Strata·llama.cpp·LM Studio·Ollama 모두 지원. 리디렉션 금지·응답 크기 제한·오류 분류·structured output 폴백을 직접 통제 |
| 저장 | SQLite(Microsoft.Data.Sqlite, WAL) | 기존 프로젝트와 같은 방식. 단계마다 기록해 비정상 종료 후에도 실행된 변경을 구분 |
| 비밀정보 | 환경 변수 또는 DPAPI(현재 사용자) 암호화 | 키가 설정 파일·DB·로그·프롬프트에 남지 않음 |

## 쓰지 않은 것과 이유

- **UFO² 실행 환경 통째 도입(Python)**: Python 런타임과 pywinauto 등 의존성이 늘고, 실행 엔진과 안전 정책이 두 언어로 나뉨.
  아키텍처 아이디어(라벨 붙인 컨트롤 목록, 민감 단계 확인, 경로 검증)만 C#으로 다시 구현.
- **UI-TARS Desktop 통합(Electron/TypeScript)**: 별도 런타임과 UI-TARS 전용 모델 출력 형식에 묶임. 0~1000 정규화 좌표와
  "실행 시점에 물리 픽셀로 변환" 규칙만 채택.
- **모델이 만든 코드 실행, PowerShell 실행**: 하지 않음. 모델은 정해진 동작(JSON)만 내고, 실행기가 검증·정책·승인 후 수행.
- **Reason Network·P08 연동, 백그라운드·예약 실행**: 범위 밖(요구사항 2·12).

## 실행 흐름

```
자연어 요청 ─▶ ModelPlanner(프롬프트: 허용 앱·창 목록·UIA 스냅샷을 <untrusted_ui_data>로 감쌈)
            ─▶ 모델 응답 ─▶ PlanParser(닫힌 동작 집합·필드·범위 검사, 실패 시 1회 수정 요청 후 거부)
            ─▶ 계획 확인(사용자) ─▶ TaskExecutor
TaskExecutor(단계마다): 창 찾기 → 허용 목록/차단 목록/관리자 권한 검사 → 요소 찾기(ref→지문→live 재탐색)
            → PolicyEngine(허용/승인 필요/거부) → 승인 창(대상·변경·영향·복구, 시간 초과=실행 안 함)
            → 승인 후 재검증(창·프로세스·요소·이름·값·좌표 화면 서명) → UIA 패턴 우선 실행(키보드·마우스는 포커스·적중 확인 후)
            → 결과 검증 → 예상 밖 창 감지 시 일시정지 → TaskJournal(SQLite) 기록
```

## 프로젝트

| 프로젝트 | 대상 | 내용 |
|---|---|---|
| WindowsAiOperator.Core | net10.0 | 동작 스키마, 계획 검증, 정책, 실행기, 계획기, 모의 데스크톱, 워크플로, 모델 비교 |
| WindowsAiOperator.ModelProviders | net10.0 | OpenAI 호환 클라이언트, Strata/llama.cpp 기능 확인, 이미지·응답 형식 시험, MOCK 모델 |
| WindowsAiOperator.Storage | net10.0 | SQLite 기록·복구·백업 |
| WindowsAiOperator.Presentation | net10.0 | ViewModel(WPF 비의존, Linux에서도 테스트) |
| WindowsAiOperator.Automation.Windows | net10.0-windows | FlaUI/Win32 구현 |
| WindowsAiOperator.App | net10.0-windows | WPF 앱(8개 화면, 승인 창, 긴급 중단 단축키, 스모크 테스트) |
| WindowsAiOperator.Cli | net10.0-windows | `wao.exe` 읽기 전용 진단·백업 |
| WindowsAiOperator.TestTarget | net10.0-windows | 실제 UIA 시험용 독립 앱(자기 상태만 바뀜) |
| tests/WindowsAiOperator.Tests | net10.0 | 단위·회귀 테스트(모의 데스크톱) |
| tests/WindowsAiOperator.UiaTests | net10.0-windows | 실제 Windows UIA 테스트 |
