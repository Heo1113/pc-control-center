# 검증 결과

이 문서는 요구된 15개 검증 항목을 무엇으로, 어디에서 확인했는지와 **확인하지 못한 것**을 구분해 기록합니다.
가상 데이터나 시뮬레이션으로 통과한 기능을 실제 하드웨어 검증으로 보지 않습니다.

## 검증 환경

| 환경 | 내용 | 한계 |
|---|---|---|
| 개발 컨테이너 (Linux x64) | .NET SDK 10.0.1xx로 전체 빌드(WPF 포함)와 테스트. Windows 전용 테스트 9개는 건너뜀 | Windows API 없음 |
| GitHub Actions `windows-latest` | Windows Server 2025 (이미지 `windows-2025-vs2026`), AMD EPYC 7763, NVIDIA GPU 없음. 전체 테스트, 실제 이벤트 로그·WMI를 쓰는 CLI 스모크, 포터블 패키지 생성, 게시된 EXE의 GUI 스모크 | **Windows 11 클라이언트가 아님.** 러너 계정이 **관리자 권한**이라 일반 사용자 권한 동작은 여기서 확인되지 않음. 러너의 로그는 사용자 PC와 다름 |
| 사용자 PC (RTX 5080, Ryzen 7 9800X3D, DDR5 64GB, X870E) | 2026-11-05 사용자가 직접 실행: 대시보드 스크린샷과 이벤트 내보내기(JSON, 30일 381건)를 받아 확인 | 사용자가 보낸 화면·파일로만 확인. 아래 항목별 판정은 대부분 CI 근거이며, 이 PC에서 자동 테스트를 돌리지는 않음 |

CI 근거: 브랜치 `claude/ecstatic-newton-cfosml`의 [Actions 실행 목록](https://github.com/Heo1113/windows-diagnostics-lab/actions?query=branch%3Aclaude%2Fecstatic-newton-cfosml).
아래 수치는 실행 #5(커밋 `7970c43`)의 로그에서 옮겼습니다. 스크린샷·JSON 보고서는 `smoke-test-evidence` 아티팩트에 있습니다.

> 이 작업 환경에서는 아티팩트 저장소에 접속할 수 없어 **GUI 스크린샷을 직접 열어 보지 못했습니다.** GUI 점검은 스모크 테스트의 자동 판정
> (화면별 데이터 존재, 예외 0건, WPF 바인딩 경로 오류 0건)에 근거합니다. 화면 배치·가독성은 사람이 스크린샷이나 실제 실행으로 확인해야 합니다.

## 항목별 결과

판정: **통과** = 실제 Windows 또는 해당 조건에서 확인 · **부분** = 일부만 실제로 확인, 나머지는 시뮬레이션 · **미검증** = 확인하지 못함

| # | 항목 | 판정 | 근거 | 확인하지 못한 것 |
|---|---|---|---|---|
| 1 | 정상 이벤트 수집 | 부분 | Windows 러너의 실제 System·Application·Setup·Kernel-PnP 로그를 구조화 쿼리로 읽음(`Every_default_channel_query_is_accepted_by_the_event_log_service`, `Collector_reads_real_logs_into_the_store_and_resumes_without_duplicates`). CLI 스모크 7일 28건, 강제 종료 검증 30일 145건 저장. 러너의 실제 로그 지움 이벤트(Eventlog 104)가 `log_cleared` 누락으로 기록됨. 파싱·분류 규칙은 이벤트 XML 고정 자료로 테스트(`EventXmlParserTests`, `EventClassifierTests`) | WHEA, BugCheck, Kernel-Power 41, GPU TDR, 디스크 오류의 **실제 이벤트 샘플로는 확인하지 못함**. 테스트용 XML은 공개 이벤트 구조를 바탕으로 직접 작성했고 실제 PC에서 캡처한 것이 아님(`tests/WinDiag.Tests/Fixtures/events/README.md`) |
| 2 | 중복 이벤트 방지 | 통과 | 채널·레코드 번호·공급자·ID·시각의 UNIQUE 키(`Same_event_is_stored_once_even_when_read_again`), 재시작 후 이어 읽기(`Restart_continues_from_the_saved_position_without_duplicates`), 과거 가져오기와 실시간 수집의 겹침(`History_import_extends_the_range_without_moving_the_live_position`), 실제 Windows 로그 재수집(`Collector_reads_real_logs_..._without_duplicates`). 강제 종료 후 다시 실행해도 이벤트 수 145건 그대로 | — |
| 3 | 수집 중 프로그램 재시작 | 통과 | 채널 위치와 이벤트가 한 트랜잭션으로 저장(`Channel_progress_and_events_commit_together`), 멈추지 않은 엔진의 다음 시작 복구(`Engine_that_was_not_stopped_is_recovered_on_the_next_start`), **Windows에서 게시된 GUI를 40초 수집 중 강제 종료 후 재시작**(CI `after-kill`) | — |
| 4 | 권한 없는 로그 접근 | 부분 | 접근 거부 채널은 한 번만 '권한 부족'으로 기록하고 다른 로그는 계속 수집(`Access_denied_channel_is_reported_once_and_others_continue`, 가상 소스), 없는 로그·꺼진 로그 구분(`Missing_and_disabled_channels_are_marked_not_failed`, 실제 Windows `Missing_channel_is_reported_not_thrown`), 보안 로그 점검이 권한과 관계없이 상태를 돌려줌(`Security_log_probe_returns_a_status_whatever_the_rights`) | CI 러너가 관리자라 **실제 Windows에서 접근 거부가 일어나는 상황은 관찰하지 못함**. 일반 사용자 계정에서 Minidump 폴더가 '권한 부족'으로 표시되는지도 미확인 |
| 5 | 센서 누락 | 통과 | NVIDIA 없는 실제 Windows에서 NVML 미검출 사유를 값 대신 표시(`Nvml_without_an_nvidia_driver_reports_a_reason_instead_of_values`, GUI 스모크 `gpuUnavailableReason: "nvml.dll을 찾을 수 없습니다 …"`), 없는 값은 NULL/N/A로 저장·표시(`Sensor_samples_keep_missing_values_as_null_and_downsample_on_request`, `Missing_gpu_is_recorded_as_unavailable_not_estimated`), 그 시각에 기록이 없으면 그렇다고 표시(`Shows_values_around_an_event_or_says_there_are_none`) | — |
| 6 | GPU 드라이버 또는 센서 접근 오류 | 부분 | 실행 중 GPU 공급자가 예외를 내도 CPU·RAM 기록은 계속됨(`A_failing_gpu_provider_never_stops_system_sampling`, A의 가상 공급자), NVML 없음(항목 5) | **실제 NVIDIA 드라이버가 실행 중 재설정(TDR)될 때 NVML 동작은 미검증.** 사용자 PC 화면(2026-11-05)에 RTX 5080의 사용률·온도·전력·VRAM이 표시되어 NVML 읽기 자체는 동작함을 확인했으나, 값이 맞는지 `nvidia-smi`와 비교하지는 않음 |
| 7 | 비정상 종료 후 기록 유지 | 통과 (프로세스 강제 종료) | **Windows CI: 게시된 GUI를 실제 로그로 40초 수집 후 강제 종료 → `wdl status --json`: 무결성 `ok`, 이벤트 145건·센서 8건 보존, 비정상 종료 1건(`app_unclean_end`) 기록.** 강제 종료 직후 DB 본 파일은 4KB였고 기록은 아직 WAL 파일에만 있었으나 모두 읽힘. Linux에서 `wdl demo`를 쓰기 도중 `kill -9`: 무결성 `ok`, 커밋된 이벤트 보존, 커밋 전 센서 일괄 기록은 전부 없음(부분 기록 없음). 테스트: `Unfinished_runs_are_recovered_with_their_last_sample_and_a_gap`, `Corrupt_file_is_moved_aside_never_deleted` | **OS 충돌(BSOD)·전원 차단 중 기록은 미검증**(요구사항에 따라 유발하지 않음). SQLite WAL + `synchronous=FULL` 설정은 커밋된 트랜잭션의 내구성을 위한 것이지만 실제 전원 차단으로 시험하지 않음 |
| 8 | 과거 기록 검색 | 통과 | 기간·중요도·로그·공급자·분류·검색어·ID 필터와 묶음(`Query_filters_search_and_groups`, `Event_explorer_searches_groups_details_notes_and_exports`), 과거 가져오기(`History_import_extends_the_range_without_moving_the_live_position`), 실제 Windows 로그의 과거 7일·30일 읽기(CI), GUI 스모크 `event-search-and-detail` | 1년 이상 쌓인 큰 DB에서의 검색 속도는 미측정 |
| 9 | 기준선 비교 | 부분 | 빈도·정확 검정·새로 나타난/사라진 문제·센서·구성 차이·같은 기간 다른 변경(`Compares_rates_groups_sensors_and_configuration_and_lists_other_changes`), 이전 기록이 없을 때 지어내지 않음(`A_baseline_with_no_data_before_says_so_instead_of_inventing_a_rate`), 정확 검정 값(`Exact_tests_match_known_values`), 화면·내보내기(`Compare_page_compares_baselines_and_snapshots`, GUI 스모크 `compare`) | **가상 시나리오 데이터로만 확인.** 실제 BIOS 업데이트·EXPO·전압·드라이버 변경 전후 데이터로는 미검증 |
| 10 | 잘못된 시간 순서·시스템 시간 변경 | 부분 | 실행 중 벽시계 점프 감지(`Wall_clock_jumps_are_recorded`, 가상 시계), 타임라인의 시간 역행 표시(`Repeats_close_together_are_collapsed_and_clock_reversals_are_flagged`), Kernel-General 1 시각 변경 해석(`Large_clock_change_is_a_warning_with_delta_from_nine_digit_times`), 9자리 소수 시각 파싱, 6008 현지 시각의 타당성 검사(`Event_6008_localized_time_is_parsed_only_when_plausible`), 덤프 헤더의 비현실적 값 제거(`Implausible_header_values_are_dropped_not_shown`) | **실제 PC의 시스템 시각을 바꿔 보는 시험은 하지 않음**(관리자 권한 필요, 시스템 설정 변경이라 범위 밖) |
| 11 | SQLite 백업·복원 | 부분 | 실행 중 백업·검증·복원(현재 DB는 옆에 보관)(`Backup_validates_and_restores_without_deleting_the_previous_database`), 잘못된 백업은 아무것도 바꾸기 전에 거부(`Invalid_backup_is_refused_before_anything_changes`), 다른 앱·새 버전 DB 거부, 복원 명령의 검증·확인 절차(`Restore_checks_the_backup_and_asks_before_handing_it_to_the_app`), Windows CLI 스모크 `backup`·`verify-backup`, GUI 스모크 `settings-save-and-backup` | **GUI에서 복원 후 프로그램이 다시 시작되는 흐름은 자동 테스트가 없음** |
| 12 | 로그 내 개인정보 보호 | 통과 (정해 둔 범위) | 사용자·컴퓨터 이름, 사용자 폴더 경로, SID, 이메일, 비밀값 가림(`PrivacyMaskerTests` 5개), 최소·상세 내보내기(`Minimal_export_…`, `Detailed_export_…`), CSV 수식 주입 방지, 앱 로그의 비밀값 제거(A의 `SecretRedactor`), 보안 로그·DNS-Client 미수집 | 마스킹은 정해 둔 형식만 가립니다. 메시지에 다른 형태의 개인 정보(예: 파일 이름 속 실명)가 있으면 남을 수 있음. 장치 인스턴스 ID(USB 일련번호가 들어갈 수 있음)는 DB에 그대로 저장되며 최소 내보내기에서만 줄임 |
| 13 | 앱 시작·종료 | 통과 | 게시된 GUI EXE가 실제 로그로 시작해 7개 화면을 모두 거쳐 종료 코드 0으로 끝남(CI 3회: 실제+가상 데이터, MOCK 센서, 강제 종료 후 재시작), 엔진 시작·정지(`Engine_starts_on_windows_with_real_sources_and_stops_cleanly`), 데이터 폴더당 한 프로세스(`One_holder_per_data_folder_until_it_is_released`) | 며칠 이상 연속 실행 안정성은 미측정 |
| 14 | Windows x64 GUI 빌드 | 통과 | Windows CI 빌드(경고를 오류로 처리, 경고 0), Linux에서도 WPF 컴파일 확인 | — |
| 15 | 포터블 EXE 또는 설치 파일 | 부분 | 자체 포함 단일 파일 `WindowsDiagnosticsLab.exe`(약 85.6MB)·`wdl.exe`(약 39.4MB)와 각 SHA-256, ZIP(약 108.5MB)을 Actions 아티팩트 `WindowsDiagnosticsLab-win-x64`로 생성. GUI 스모크는 이 게시된 EXE로 실행 | **설치 프로그램(MSI) 없음, 코드 서명 없음**(SmartScreen 경고 가능) |

## 자동 테스트 수

| 위치 | 결과 |
|---|---|
| Linux (개발 컨테이너, 이 문서 작성 시점) | 125개 중 116개 통과, 9개 건너뜀(Windows 전용) |
| Windows CI 실행 #5 | 124개 모두 통과(건너뜀 0). 이후 추가된 복원 명령 테스트 1개는 다음 실행에 포함 |

테스트 분류: 이벤트 XML 파싱·분류, 개인정보·설정, 저장소, 수집기, 센서·덤프·알림·엔진, 분석(충돌 사건·센서 전후·타임라인·반복·기준선·보고서),
내보내기·문서 동기화, 7개 화면 뷰모델, Windows 실제 API(9개), A 코드 해시 일치.

## 이 작업에서 실제로 확인하지 못한 것 (요약)

1. 사용자 PC(RTX 5080, Ryzen 7 9800X3D, DDR5 64GB, ASRock X870E Nova)에서의 실행.
2. RTX 5080에서 NVML 값의 정확도(화면 표시는 확인, 다른 도구와 비교는 안 함), 드라이버 재설정 중의 NVML 동작.
3. 실제 BSOD·WHEA·GPU TDR·디스크 오류 이벤트(유발하지 않았고, 러너에도 없음). 이 이벤트들의 분류는 직접 작성한 XML 자료로만 시험했습니다.
4. Windows 11 클라이언트(CI는 Windows Server 2025)와 일반 사용자 권한 계정에서의 동작.
5. 실제 BIOS·EXPO·메모리 전압·GPU 드라이버 변경 전후 비교.
6. 시스템 시각을 실제로 바꾼 경우, OS 충돌·전원 차단 중 기록 보존.
7. GUI의 시각적 품질(스크린샷을 이 환경에서 열어 보지 못함), 고해상도·다중 모니터, 알림 영역 풍선의 실제 표시.
8. 며칠 이상 연속 실행, 수개월 데이터에서의 DB 크기와 속도.
9. Strata·프로젝트 A와 같은 PC에서 함께 실행. (C는 둘의 파일·DB를 읽거나 쓰지 않도록 만들었고 코드에서 그 경로를 참조하지 않지만, 같은 PC에서 함께 실행해 보지는 않았습니다.)

## 사용자 PC 첫 실행에서 드러난 점 (2026-11-05)

- 30일 동안 시스템 충돌, WHEA, GPU 드라이버 오류, 디스크 오류 기록은 0건이었습니다. 따라서 이 항목들의 분류는 여전히 실제 이벤트로 확인되지 않았습니다.
- 수집된 오류의 70%(381건 중 267건)가 '기타 오류·경고'였습니다. 원인은 Schannel, DCOM 10010·10005, VSS, Windows Search, Kernel-PnP 225 등에 규칙이 없었기 때문입니다.
  이 이벤트들의 실제 필드 구조로 규칙을 추가했고(카탈로그 버전 2), 기존에 저장된 이벤트는 새 버전을 처음 실행할 때 다시 분류됩니다.

## 마우스 휠 스크롤 (2026-11-05 수정)

- 표·목록·차트 위에서 휠이 페이지를 스크롤하지 못하던 문제(프로젝트 B 작업 중 코드 분석으로 보고됨)를 고쳤습니다.
- GUI 스모크 테스트가 하드웨어 화면의 차트 위와 대시보드 표(끝까지 내린 상태) 위에 Windows와 같은 순서로 휠 이벤트를 보내고 페이지가 내려가는지 확인합니다. 결과는 `smoke-report.json`의 `wheel`에 남습니다.
- 실제 마우스로 사용자 PC에서 확인한 것은 아닙니다. 차트의 Ctrl+휠 확대·축소도 실제 조작으로 확인하지 않았습니다.

## 사용자 PC에서 안전하게 확인해 볼 수 있는 것

시스템을 바꾸거나 오류를 일부러 일으키지 않는 확인입니다.

1. 일반 사용자 권한으로 실행 → 설정 화면의 로그별 상태, 하드웨어 화면의 센서 지원 현황 확인.
2. 하드웨어 화면의 GPU 온도·전력·클럭을 `nvidia-smi` 또는 NVIDIA 앱의 값과 비교.
3. 이벤트 뷰어(eventvwr)에서 최근 오류 몇 건을 골라 이벤트 탐색의 같은 시각·ID·공급자와 비교.
4. 과거에 블루스크린이나 갑작스러운 재부팅이 있었다면, 진단 화면의 시스템 충돌 목록에 같은 날짜로 나타나는지 확인.
5. 다음에 BIOS·드라이버를 바꿀 계획이 있으면 바꾸기 전부터 실행해 두고, 변경 기록과 기준선을 만든 뒤 며칠 후 비교.
6. 진단 자료 묶음을 만들어 `events.csv`·`report.html`에 사용자 이름·컴퓨터 이름이 남지 않는지 확인.
