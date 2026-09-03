## 1. Summary

M9.14.1의 HUD 톱니바퀴 입력 경로와 진단 ZIP 생성 오류를 수정했다. 사용자는 게임 내 설정창 열기와 진단 ZIP 생성 성공을 확인했다. 최종 빌드는 별도 폴더에서 단일 EXE만 실행하는 오프라인 검증을 통과했다.

M9.14 Web OAuth의 개발자·일반 사용자 LIVE PASS는 사용자 보고로 기록한다. 인증 구조와 서버 운영 설정은 변경하지 않았다. 사용자가 추가 승인한 Backend REST 사용자 언급 이름 누락만 별도 수정했다. 이 추가 수정은 로컬 검증 완료이며, 실제 서비스 반영에는 Backend 재배포가 필요하다. 재배포·커밋·푸시는 하지 않았다.

## 2. HUD Gear Root Cause

기존 이벤트 연결과 SettingsWindowService는 존재했다. 문제 지점은 비활성 HUD의 버튼이 기본 Click-on-release 경로에만 의존한 입력 처리였다. WPF ButtonBase는 MouseDown에서 포커스와 마우스 캡처를 처리하고 MouseUp까지 IsPressed가 유지되어야 Click을 발생시킨다. HUD는 게임 포커스를 유지하는 비활성 창 정책을 사용하므로 이 경로에서 클릭이 누락될 수 있었다. 트레이는 버튼 입력을 거치지 않고 공유 Settings 서비스로 직접 들어가므로 정상 동작했다.

첨부 화면에서 톱니바퀴 툴팁은 보였으므로 hover/hit 경로 전체가 끊긴 상황은 아니었다. 기존 OS 입력 캡처 손실 순간을 네이티브 추적으로 직접 관측한 것은 아니다. 코드 경로, 기본 버튼 동작, 수정 후 사용자 PASS를 근거로 입력 경로의 결함을 확인했다. [WPF ButtonBase 원본](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ButtonBase.cs)

## 3. HUD Settings Fix

일반 헤더와 미니멀 HUD 톱니바퀴 모두 PreviewMouseLeftButtonDown에서 설정 열기를 처리한다. 이 이벤트를 처리 완료로 표시해 기본 버튼 캡처와 상위 HUD 드래그가 같은 입력을 다시 처리하지 않게 했다. Focusable=false, IsUnlocked 바인딩, 컨트롤러의 잠금·Dispose 가드를 적용했다. 기존 Click 이벤트는 자동화/키보드 경로용으로 유지했다.

기존 SettingsWindowService를 계속 사용한다. 숨긴 창 재표시, 최소화 복원, 트레이 진입, 단일 인스턴스 재사용을 검증했다. 잠금/해제와 HUD hide/show 이후에도 입력 경로를 검증했으며, 전역 click-through 네이티브 정책은 바꾸지 않았다.

## 4. Diagnostic Root Cause

첫 번째 원인은 BuildSummary 단계의 JsonReaderException이다. 실제 BuildDiagnosticRequest에는 숫자로 직렬화되는 Sales.State와 HasProtectedCredential 같은 bool 필드가 있다. JSON 전체에 로그용 문자열 마스킹을 적용하면서 숫자/불리언 문법이 손상되어 후속 JSON 파싱이 실패했다. 기존 테스트의 단순한 샘플 객체는 이 실제 운영 스냅샷 조합을 재현하지 못했다.

첫 수정본의 실제 사용자 재검증에서는 CollectLogs / InvalidDataException이 추가 확인됐다. 활성 로그에 과거 자격증명 파일명이 기록되어 있었고, 실제 파일이나 비밀값이 아닌 파일명 언급까지 금지 자료로 판정했다. 이 예외가 선택 로그의 생략 경로에 포함되지 않아 전체 ZIP 생성이 실패했다. 파일명 표식 마스킹과 선택 로그의 privacyBoundary 생략 처리를 추가한 후 사용자가 성공을 확인했다.

## 5. Diagnostic Export Fix

저장 대화상자의 기본 위치는 사용자 쓰기 가능 폴더인 %LOCALAPPDATA%\GachaOverlay\Diagnostics이며 사용자가 다른 위치를 선택할 수 있다. 상대 경로 출력은 거절한다. 저장 위치 선택과 스냅샷 생성 오류도 제어된 실패로 반환한다.

최종 파일 옆에 PID/GUID가 포함된 고유 임시 파일을 CreateNew로 생성한다. ZIP 작성과 flush가 끝난 뒤 최종 파일로 이동한다. 실패 시 해당 작업의 임시 파일만 정리하며, 잠긴 기존 최종 ZIP을 손상시키지 않는 테스트를 추가했다.

필수 JSON 6개는 유효성과 허용 목록을 검증한다. 선택 로그/크래시 자료가 없거나 잠겨 있거나 안전하게 내보낼 수 없으면 생략하고 사유를 manifest에 기록한다. 로그는 공유 읽기, 최대 2개·총 2 MiB 제한을 유지한다. 읽기 시작 시 길이를 고정하고 UTF-8/16/32 BOM 및 tail 경계를 처리한다. 잘린 첫 줄은 식별자 없는 비밀값이 나올 수 있어 제외한다. 실패 UI에는 단계/예외 종류만 표시하고 원문 예외 메시지·스택은 내보내지 않는다.

## 6. Diagnostic Privacy

필수 JSON 6종, 선택 crash-summary.json, 정해진 logs/log-N.txt만 허용한다. 자격증명 .dat 파일, 토큰 저장소, 메시지 원문 파일은 수집하지 않는다. JSON을 구조적으로 순회하며 민감 필드를 마스킹하고 운영 상태의 숫자/명시적 bool 플래그는 보존한다.

OAuth code/state/token/Client Secret/claim, Remote credential, 인증 헤더, 채팅·판매 본문 필드, Discord ID를 마스킹한다. 따옴표·공백·인코딩 경계·과대 입력·정규식 시간 제한을 테스트했다. 금지 DPAPI 표식이 남은 선택 자료는 privacyBoundary로 제외한다. 최종 단계에서 허용 목록과 재마스킹 일치 여부를 다시 검사한다.

사용자가 만든 실제 ZIP의 JSON 6개와 로그 2개를 읽기 전용으로 검사했다. 예상 밖 entry 0, 유효 JSON 6, 가려지지 않은 Discord ID 패턴 0, DPAPI 표식 0, 자격증명 파일명 표식 0이었다. 이는 검사한 자료와 규칙에 대한 결과이며 모든 가능한 비밀값 부재를 수학적으로 보장한다는 뜻은 아니다.

## 7. Web OAuth Live Validation

사용자가 보고한 기존 M9.14 LIVE 결과: 개발자 계정 PASS, 일반 비관리자 계정 PASS, slash 명령 미사용, 채널 Application Commands 제한 유지, 재시작 자동 로그인 PASS. 이 수정에서 에이전트가 실제 OAuth를 다시 수행한 것은 아니다. OAuth 흐름·DPAPI 저장·서버 환경변수·Discord 권한 설정은 변경하지 않았다.

## 8. Regression Safety

Chat/Sales/Presence/write-back/OAuth/DPAPI/Settings/tray/lifetime 기존 테스트를 유지했다. 전역 click-through, 캐시된 설정창, M9.12 종료 수명 정책을 유지했다. 오프라인 검증 모드는 명시적 인자에서만 실행되며 실제 사용자 프로필이나 네트워크를 시작하지 않는다.

추가 승인된 언급 수정: 실시간 SocketMessage와 달리 최근 메시지 REST 경로는 RestMessage.MentionedUsers의 이름을 읽지 않아 HUD가 @ID 대체 표시를 했다. 해당 데이터에서 GlobalName, 없으면 Username을 사용하도록 했다. 기존 ResolvedData의 구성원 표시 이름 우선순위는 유지한다. 실제 SDK 모델을 이용한 3개 회귀 테스트에서 이름과 추가 언급별 REST 요청 0을 확인했다. REST 사용자 정보만으로 서버별 닉네임을 항상 보장하는 것은 아니다. [Discord.Net 3.20.1 RestMessage](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.Rest/Entities/Messages/RestMessage.cs)

Backend는 재배포하지 않았으므로 현재 운영 서버의 @ID 표시는 아직 이 수정의 LIVE PASS가 아니다. 재배포 후 최근 메시지를 다시 불러와 확인해야 한다.

## 9. Tests

| 검증 | 결과 |
| --- | --- |
| 기존 기준 | 1,366개 |
| 신규 | 35개, 총 1,401개 |
| Debug / Release | 각각 1,401 PASS |
| 실패 / 건너뜀 | 각각 0 / 0 |
| Debug / Release 빌드 경고·오류 | 각각 0 / 0 |
| restore / format --verify-no-changes / diff --check | PASS |
| Backend linux-x64 Release publish | PASS, Linux 실행 NOT RUN |
| WPF win-x64 self-contained single-file publish | PASS |
| 최종 EXE 단독 복사 후 오프라인 실행 | PASS, ZIP 2회, 필수 JSON 6개, 잔여 tmp 0 |
| 격리 Docker context 소스 구성/restore/publish | PASS, 135개 입력·3개 프로젝트 |
| 실제 Docker 이미지 빌드 | NOT RUN |
| 장시간 soak / 다른 사용자의 PC | NOT RUN |

최종 WPF: artifacts/m9141-final/wpf-win-x64/GachaOverlay.App.exe (179,130,955 bytes).
SHA256: 32658EEFCFCE5EBA6F0C1C7F39DAC30B9CDB5A056594BD36D0667B5EE7287341.
Backend: artifacts/m9141-final/backend-linux-x64 (11 files, 5,587,532 bytes).
TRX: artifacts/m9141-validation/m9141-complete-debug.trx 및 m9141-complete-release.trx.
상세 수치는 docs/deployment/m9141-validation.json에 기록했다.

## 10. Files Changed

저장소 기준 경로와 목적:

| 경로 | 목적 |
| --- | --- |
| src/GachaOverlay.App/Presentation/HudWindow.xaml | 일반/미니멀 톱니바퀴 preview 입력 및 잠금 바인딩 |
| src/GachaOverlay.App/Presentation/HudWindow.xaml.cs | MouseDown에서 설정 요청, 중복/드래그 입력 차단 |
| src/GachaOverlay.App/Services/HudWindowController.cs | disposed/locked guard 및 안전한 입력 수락 로그 |
| src/GachaOverlay.App/Lifecycle/ApplicationHost.cs | 진단 저장 기본 경로, 단계별 실패 처리, 실제 요청 테스트 접근 |
| src/GachaOverlay.App/App.xaml.cs | 명시적 오프라인 게시 EXE 검증 진입점 |
| src/GachaOverlay.App/Lifecycle/ApplicationLifetimeService.cs | 검증 종료 사유, 기존 공통 종료 경로 재사용 |
| src/GachaOverlay.App/Services/ClientExportVerification.cs | 프로필·네트워크 없이 실제 WPF/진단 경로 검증 |
| src/GachaOverlay.Infrastructure/Diagnostics/DiagnosticBundleExporter.cs | 안전한 수집·공유 읽기·단계·선택 생략·최종 ZIP 검증 |
| src/GachaOverlay.Infrastructure/Diagnostics/DiagnosticContentSanitizer.cs | JSON 구조 보존 마스킹과 본문/ID 처리 |
| src/GachaOverlay.Infrastructure/Logging/SensitiveDataRedactor.cs | 따옴표/공백 민감값 및 입력 크기·시간 제한 |
| src/GachaOverlay.Core/Logging/OAuthDataRedactor.cs | OAuth 따옴표 민감값 및 입력 크기·시간 제한 |
| src/LSOverlay.Backend/Chat/DiscordChatMessageNormalizer.cs | REST 사용자 언급 이름 보존 |
| tests/GachaOverlay.Tests/Diagnostics/M9141DiagnosticRegressionTests.cs | 실제 스냅샷·로그·인코딩·출력 실패·개인정보 회귀 |
| tests/GachaOverlay.Tests/Presentation/M9141ClientCorrectiveTests.cs | 버튼 입력·잠금·창 재사용·실제 요청 회귀 |
| tests/GachaOverlay.Tests/Presentation/OptionDisplayTests.cs | 기존 단일 WPF Application 범위에서 Settings 재사용 확인 |
| tests/GachaOverlay.Tests/Backend/M9141RestMentionTests.cs | 실제 SDK REST 이름 보존 3개 테스트 |
| tools/dev/run-ls-m9141-client-check.ps1 | EXE만 임시 폴더에 복사해 오프라인 검증 |
| docs/deployment/m9141-client-corrective-report.md | 이번 완료 보고서 |
| docs/deployment/m9141-validation.json | 검증 수치·증거·미실행 항목 |

## 11. User Actual Validation

M9.14.1 CLIENT CORRECTIVE: RUN

- HUD gear: PASS — 사용자 게임 화면에서 설정 열기 확인.
- Tray Settings: NOT RUN — 이번 사용자 재확인은 미수신. 자동 경로/창 재사용 검증은 PASS.
- Diagnostic Bundle: PASS — m9141-corrected 실행본으로 생성 성공, 실제 ZIP 구조/패턴 검사 완료.

최종 m9141-final 실행본은 이후 로그 경계/마스킹 보강을 포함하며 자동 단독 실행 검증을 통과했다. 사용자가 물리 입력으로 확인한 바이너리와 최종 바이너리는 동일 파일이 아니므로 최종본 실게임 재검증은 별도다. 다른 사용자 PC의 진단 생성은 아직 NOT RUN이다.

최소 후속 확인: 기존 앱을 트레이에서 종료하고 최종 EXE 실행 → 트레이 우클릭으로 설정 열기 → 닫은 뒤 게임에서 미니멀 HUD 잠금 해제 후 톱니바퀴 열기 → 잠금/해제·hide/show 후 반복 → 진단 ZIP 1회 생성. Backend 언급은 별도 승인된 재배포 후 최근 메시지 새로고침으로 확인한다.

## 12. Final Status

M9.14.1 CLIENT REGRESSIONS FIXED

## 13. Deferred Work

시작하지 않음: slash pairing retirement, status.revo32.cloud, LS Overlay 2.0 UI/UX overhaul, final branding integration, RC soak, release engineering. 커밋·푸시·PR·릴리스·Railway 재배포/설정 변경도 하지 않았다.

## 14. Recommended Next Stage

Slash Pairing Retirement / Web Auth Finalization.

자동으로 시작하지 않는다. 승인된 REST 언급 수정의 실제 서버 반영 역시 별도 배포 승인이 필요하다.
