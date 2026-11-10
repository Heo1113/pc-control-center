# 기존 프로그램 데이터 가져오기

PC Control Center는 처음에 빈 데이터로 시작합니다. 기존 프로그램(A·B·C·E)에서 쌓은 기록을 쓰려면 **설정 › 데이터·이전**에서 모듈별로 가져옵니다.
가져오기 전까지 기존 프로그램의 폴더는 읽기만 하며, 가져온 뒤에도 기존 프로그램은 자기 데이터로 계속 쓸 수 있습니다(두 데이터는 그 뒤로 따로 쌓입니다).

| 기존 프로그램 | 기본 폴더 | 파일 | 가져오는 곳 |
|---|---|---|---|
| A Local AI Performance Center | `%LOCALAPPDATA%\LocalAiPerformanceCenter` | `lapc.db` | `modules\local-ai` |
| B GPU Profit & Compute Center | `%LOCALAPPDATA%\GpuProfitCenter` | `gpc.db` | `modules\gpu-economics` |
| C Windows Diagnostics Lab | `%LOCALAPPDATA%\WindowsDiagnosticsLab` | `wdl.db` | `modules\diagnostics` |
| E Windows AI Operator | `%LOCALAPPDATA%\WindowsAiOperator` | `wao.db` | `modules\operator` |

포터블로 쓰던 경우 [다른 폴더 선택]에서 실행 파일 옆 `data` 폴더를 고릅니다.

## 순서

1. **기존 프로그램을 종료**합니다(명령줄 도구 `lapc`·`gpc`·`wdl`·`wao` 포함). 실행 중이면 진행하지 않습니다.
2. **[검사·리허설]** — 아무것도 바꾸지 않습니다.
   - 원본 데이터베이스(와 WAL)를 바이트 그대로 `migration\<모듈>\<시각>\`에 복사하고, 복사 전후 원본의 SHA-256이 복사본과 같은지 확인합니다.
     원본 파일에는 SQLite 연결을 열지 않습니다(열기만 해도 `-shm` 같은 파일이 생길 수 있어서).
   - 복사본으로만: 무결성 검사, 스키마 버전(통합 앱보다 새 버전이면 중단), 모듈 자체 백업 검증, SQLite 백업 API로 만든 최종 복사본,
     모든 테이블의 행 수와 주요 집계(B 전력 합·수익/비용 금액, 마지막 기록 시각 등) 비교.
   - 결과는 화면의 검사 목록과 `rehearsal-report.json`에 남습니다.
3. **[적용…]** — 확인 창에 원본·대상·보관 위치가 나옵니다. 확인하면:
   - 그 모듈만 잠시 멈춥니다(다른 모듈은 계속 동작).
   - 통합 앱의 그 모듈 데이터베이스는 같은 폴더에 `*.before-restore-*.db` / `*.before-import-*.db`로 **보관**하고(삭제하지 않음), 검증된 복사본을 놓습니다.
     A·B·C는 각 프로그램의 복원 기능(`RestoreFrom`)을 그대로 씁니다.
   - [설정 파일도 가져오기]를 켜 두면 `settings.json`도 같은 방식으로 바꿉니다. API 키(`secrets.dat`)는 옮기지 않습니다 — Operator의 모델 화면에서 다시 입력하세요.
   - 행 수를 다시 확인하고 모듈을 다시 시작합니다. 중간에 실패하면 보관한 이전 파일(데이터베이스·WAL·SHM·설정)을 되돌려 놓고,
     모두 원래대로인지 확인한 뒤 다시 시작합니다. 가져오다 생긴 설정 파일은 지웁니다.
   - 자동으로 되돌리지 못하면 그렇다고 표시하고(“자동 되돌리기 실패”), 그 모듈은 **멈춘 채로** 둡니다. 화면에 보관 파일 위치가 나오며
     아무 파일도 지우지 않았으니, 아래 방법으로 직접 복구한 뒤 모듈을 다시 시작하세요.

## 되돌리기

보관된 이전 파일은 그 모듈 폴더에 남아 있습니다. 되돌리려면 PC Control Center를 종료하고 현재 파일을 옮긴 뒤 보관 파일의 이름을 원래 이름(`gpc.db` 등)으로 바꿉니다.
원본 프로그램의 폴더는 처음부터 바뀌지 않았으므로 그대로 둡니다.
