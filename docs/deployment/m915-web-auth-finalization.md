## 1. Summary

M9.15 — Slash Pairing Retirement / Web Auth Finalization

- 기준: M9.14.1, commit `c15f84f`, 자동 테스트 1,401개.
- 신규 인증을 Discord Web OAuth 하나로 정리했습니다. 기존 slash pairing 발급 Remote credential은 그대로 유지됩니다.
- slash handler, 등록/upsert, pair-code API·상태·계약·클라이언트 호출·WPF UI를 제거했습니다.
- 기존 등록 명령을 안전하게 폐기하는 독립 migration을 추가했습니다.
- 로컬 구현·자동 검증·배포용 publish는 완료했습니다. 운영 배포와 실제 명령 소멸 확인은 아직 하지 않았습니다.
- commit, push, PR, tag, Release, Railway/DNS/Portal/서버 권한 변경은 하지 않았습니다.

작성일: 2026-09-03. 저장소: `E:\Codex\Projects\Gacha_Overlay`.

## 2. Removed Slash Pairing Architecture

제거한 운영 경로:

- `DiscordPairingCommand` interaction handler와 `SlashCommandExecuted` 구독.
- Gateway Ready/GuildAvailable 시 legacy 명령 registration/reconciliation.
- `POST /api/v1/pairings`, `GET /api/v1/pairings/{pairingId}`.
- `PairingService`의 일시적 승인·만료·polling 상태와 `PairingHealth`.
- `CreateUserCode`, `NormalizeUserCode`, 구형 claim 인증 parser.
- `PairingState`, `CreatePairingRequest`, `CreatePairingResponse`, `PairingClaimResponse`.
- RemoteClient의 `CreatePairingAsync`, `GetPairingAsync`.
- WPF와 온보딩의 pair code, slash 입력 지시, 구형 polling, loopback fallback.
- 구형 pairing 전용 rate limiter와 metrics.

WebAuth의 독립 ClaimSecret과 암호학적 공용 함수는 유지했습니다. 과거 로그의 비밀정보를 가리는 redactor 및 금지 query 키는 방어 목적으로 남겼으며, 인증 기능을 수행하지 않습니다.

## 3. Registered Command Retirement

정확한 대상은 현재 Bot application의 configured Target Guild에 등록된 chat-input root `lsoverlay`입니다. 그 안에 `pair` subcommand 하나와 필수 string `code` 옵션 하나만 있는 legacy 구조를 확인합니다.

처리 순서:

1. 기존 Bot Token으로 현재 Bot application ID를 조회합니다.
2. 그 application + Target Guild의 명령만 조회합니다.
3. application/guild/name/type/옵션 구조가 일치하는 명령 ID 하나씩만 삭제합니다.
4. 다시 조회해 해당 legacy 명령의 부재를 확인합니다.

다른 application, 다른 guild, 다른 이름, global command에는 삭제 요청을 보내지 않습니다. 구조가 달라진 동일 이름 명령도 임의 삭제하지 않고 보류합니다. bulk overwrite/delete나 재등록은 없습니다.

이 API는 application/guild/command ID를 명시하는 Discord의 개별 guild-command 삭제 경로를 사용합니다. [Discord 공식 명령 API](https://docs.discord.com/developers/interactions/application-commands#delete-guild-application-command)

- 버전: `M9.15/slash-retirement-v1`.
- 존재하지 않으면 성공, 삭제 경쟁의 404도 재조회 후 성공 처리합니다.
- 한 프로세스에서 성공한 뒤 worker는 종료합니다.
- 영속 completion marker는 만들지 않습니다. 다음 시작에서 다시 확인하는 방식으로 멱등성을 유지합니다.
- Gateway Ready 이후 별도 BackgroundService에서 실행합니다. Gateway 이벤트 처리 및 `/healthz` readiness와 분리했습니다.
- HTTP 요청당 10초, 시도당 20초, 시작당 최대 3회, 재시도 사이 30초로 제한합니다.
- 403/429/5xx/timeout 등의 실패는 비밀정보 없는 유형 로그를 남기고 보류합니다. Remote 핵심 동작을 중단하지 않으며 다음 시작에서 다시 시도합니다.
- Discord.Net 3.20.1의 guild-command DeleteAsync가 전달된 RequestOptions를 하위 호출에 넘기지 않아, 취소·시간제한이 적용되는 전용 REST client를 사용했습니다. [Discord.Net 해당 버전 소스](https://raw.githubusercontent.com/discord-net/Discord.Net/3.20.1/src/Discord.Net.Rest/Entities/Interactions/RestGuildCommand.cs)

운영 확인 로그:

`Migration M9.15/slash-retirement-v1: completed; legacy command absent.`

현재 실제 서버에 이 migration을 배포하거나 실행하지 않았습니다. 모든 운영 배포에서 명령 폐기가 확인된 이후 후속 작업에서 migration 코드 자체를 제거할 수 있습니다.

## 4. Final Authentication Architecture

- 최초 설치/유효한 credential 없음: WPF의 “Discord로 로그인” → 공식 브라우저 OAuth → Backend 신원 및 Target Guild 검사 → Remote credential 발급 → WPF DPAPI 보관.
- 기존 credential 있음: 보호된 credential을 읽어 Bootstrap/Stream을 시작합니다. 브라우저를 자동으로 열지 않습니다.
- 명확한 무효/취소: 로그인 재시도 UI를 제공합니다.
- 일시적 통신 장애: credential을 지우지 않고 재연결합니다.
- WebAuth가 없는 Backend: 명시적인 이용 불가 상태를 보여주며 slash 또는 localhost fallback으로 전환하지 않습니다.
- 브라우저와 WPF에 Discord OAuth access/refresh token을 전달하지 않습니다. WPF에는 별개의 Remote credential만 전달됩니다.

운영 URL 및 callback은 그대로입니다:

- Backend: `https://overlay.revo32.cloud`
- Callback: `https://overlay.revo32.cloud/auth/discord/callback`

## 5. Existing Credential Compatibility

기존 `client-credentials.v1.json` 구조와 registry 구현은 바꾸지 않았습니다.

- schemaVersion 1, hash-only persistence.
- 최대 128개, 180일 만료.
- installation / Discord User / Target Guild binding.
- atomic persistence 및 backup recovery.
- 발급 경로에 따른 차별이나 강제 재발급 없음.

회귀 테스트는 과거 slash issuer의 고정 v1 JSON fixture와 WebAuth service가 발급한 credential을 각각 두 번의 Kestrel 재시작에서 인증·Bootstrap했습니다. 재발급 없이 동일 credential이 유효하고 registry 파일 바이트가 변하지 않는 것을 확인했습니다.

Volume 판단은 소스 및 로컬 격리 fixture 기준입니다. 구형 PairingService 상태는 메모리 전용이며 삭제할 별도 pairing 영속 파일이 없습니다. 실제 Railway `/data` Volume 내부를 조회하지 않았고, 운영 registry·backup·DPAPI 파일을 읽거나 삭제하지 않았습니다.

## 6. Discord Bot Architecture

Bot은 제거하거나 재초대하지 않습니다. 이번 작업에서 운영 Bot 설치를 변경하지 않았습니다.

Gateway, 채팅 수신, 반응 수신, Presence, Sales write-back은 유지됩니다. 기존 intents는 다음과 같으며 변경하지 않았습니다:

`Guilds | GuildMessages | GuildMessageReactions | GuildMessagePolls | MessageContent | GuildPresences`

`GuildMembers`는 OFF이고, 사용자 전체 다운로드도 켜지 않았습니다. Bot 권한을 추가하지 않았습니다.

신규 인증에 Application Commands 권한은 필요하지 않습니다. 따라서 일반 채팅 채널의 기존 앱 명령어 제한을 해제할 필요가 없습니다.

## 7. Web OAuth Security

M9.14 Backend WebAuth 구현은 그대로 유지했습니다.

- Authorization Code Grant, `identify` scope만 사용.
- state 검증, PKCE S256.
- state와 독립적인 WebAuth ClaimSecret.
- 일회성 claim 및 세션 만료·취소.
- Target Guild 구성원 여부 확인 후 credential 발급.
- OAuth token은 Backend 처리 중에만 사용하고 영속 저장하지 않음.
- 브라우저에 Remote credential을 노출하지 않음.
- Remote credential은 WPF의 Windows 사용자별 DPAPI로 보호.
- 기존 WebAuth rate limit 및 안전한 오류/로그 정책 유지.

이번 수정으로 OAuth scope, callback, secret 배포 위치, 회원 확인 정책을 변경하지 않았습니다.

## 8. Client UX

설정 및 온보딩에서 신규 인증 수단은 “Discord로 로그인” 하나입니다. 코드 출력/복사, slash 명령 안내, 페어링 시작/대기 UI를 삭제했습니다. 한국어·영어·일본어 문구를 함께 정리했습니다.

인증 중에는 중복 로그인을 막고 취소를 제공합니다. 이미 유효한 credential이 있는 Live/재연결/일시적 authorization 조회 불가 상태에서는 새 로그인을 강요하지 않습니다. 무효·취소된 credential에는 재로그인을 제공합니다.

HUD 톱니바퀴, 트레이 설정 진입, 설정창 재사용은 그대로입니다. WebAuth를 지원하지 않는 loopback Backend에서도 구형 인증 경로는 나오지 않습니다.

## 9. Security / Attack Surface

공개 pair 생성·조회 route, interaction handler, 코드 승인 상태, UserCode 생성 및 구형 claim parser를 제거해 사용하지 않는 진입점을 줄였습니다. 제거된 두 route는 HTTP 통합 테스트에서 404이고 credential 발급도 발생하지 않습니다.

Remote 채널 권한, Target Guild 확인, 판매글 소유권, AccessRevoked의 fail-closed 동작은 유지했습니다. 검증 불가와 명백한 접근 취소를 구분하는 기존 정책도 유지했습니다.

TransportProbe는 새 인증을 발급하지 않고 기존 Remote credential만 사용합니다:

- developer-only SecureString prompt 또는 `LSO_PROBE_ACCESS_TOKEN` 환경변수.
- credential을 CLI 인자에 받거나 출력하지 않음.
- endpoint 외 추가 인자, userinfo/query/fragment 및 안전하지 않은 주소 거절.
- 토큰 누락 시 네트워크 요청 전에 종료.
- Bot Token/OAuth secret을 probe/WPF 자식 프로세스에 전달하지 않도록 helper 환경을 정리.

구형 isolated Backend helper는 사전에 WebAuth와 해당 격리 Backend로 연결되는 등록 callback이 준비된 개발 환경만 허용합니다. 일반 사용자 검증에는 이 helper를 사용하지 않고 publish된 WPF를 기존 운영 서비스에 연결합니다.

## 10. Regression Safety

전체 회귀 테스트에 Chat, Sales, Presence, write-back, 채널 전환, 재연결, 중복 방지, 권한 거절, lifetime 취소·해제, 미디어 및 mention 처리가 포함됩니다.

M9.14.1 사용자가 보고한 HUD gear, Tray, 진단 파일, 서버 닉네임/mention 실제 PASS는 확정된 이전 baseline으로 기록합니다. 이를 M9.15 운영 실사용 PASS로 전용하지는 않습니다.

새 단일 EXE를 격리된 합성 데이터로 실행한 오프라인 WPF 검사 결과:

- 단일 EXE 실행 PASS.
- 설정 재사용·톱니바퀴 경로 PASS.
- 진단 파일 2회 생성, 필수 JSON 6개 파싱 PASS.
- 남은 임시 파일 0.
- 실제 사용자 데이터 읽기 없음, 네트워크 시작 없음.
- 실제 GTA 물리 입력 검증은 NOT RUN.

## 11. Tests

| 항목 | 최종 결과 |
|---|---|
| 이전 baseline | 1,401 |
| 제거한 obsolete 테스트 | 26 |
| 추가한 대체/회귀 테스트 | 29 |
| 최종 합계 | 1,404 = 1,401 − 26 + 29 |
| Debug | 1,404 PASS |
| Release | 1,404 PASS |
| Failed / Skipped | 각각 0 / 0 |
| Debug·Release build warnings / errors | 각각 0 / 0 |
| Restore | PASS |
| format --verify-no-changes | PASS |
| git diff --check | PASS |
| Backend linux-x64 publish | PASS |
| WPF win-x64 self-contained single-file publish | PASS |
| 격리 Docker context restore/publish | PASS |
| 실제 Docker image build | NOT RUN — Docker CLI 없음 |
| Linux에서 Backend 실행 | NOT RUN |
| M9.15 Railway 배포 | NOT RUN |

제거한 26개:

- M9.12.1 slash permission/등록·승인 테스트: 21개.
- 구형 PairingService 단위 테스트: 3개.
- slash command metadata 테스트: 1개.
- 구형 pair 생성 rate-limit HTTP 테스트: 1개.

추가한 29개:

- migration 정확한 대상/부재/반복/실패/취소/불명확 구조: 13개.
- 과거 slash-issued 및 WebAuth-issued credential 재시작 호환성: 2개.
- 제거된 HTTP route의 404 및 미발급: 2개.
- production 구조 정리/security/lifetime 경계: 4개.
- credential와 health에 따른 로그인 버튼 상태: 6개.
- loopback에서도 slash fallback 없음: 1개.
- 삭제됐지만 아직 Git index에 남은 소스의 Docker context 처리: 1개.

기존 인증 테스트 중 transport/재연결/권한 검증 목적이 남아 있는 것은 credential 또는 WebAuth fixture로 변경해 유지했습니다. OAuth 보안 테스트를 없애 테스트 수만 맞추지 않았습니다.

검증 메모: 전체 테스트 실행 한 번이 진행되지 않아 해당 검증 프로세스를 중단했습니다. 동일 소스에서 진행 로그와 90초 무응답 감지를 켠 Debug/Release 재실행과 Debug 추가 반복 실행이 모두 1,404개를 통과했습니다. 그 정지의 원인은 확정하지 않았으며, 수정된 제품 결함으로 보고하지 않습니다.

증거:

- `artifacts/m915-validation/m915-complete-debug.trx`
- `artifacts/m915-validation/m915-complete-release.trx`
- `artifacts/m915-validation/m915-repeat-debug.trx`
- 상세 검증 메타데이터: `docs/deployment/m915-validation.json`
- 오프라인 EXE 증거: `%TEMP%/GachaOverlay-M9141-14bbaf0cc9a34b7cb43ce84d2eb0f8df/output/result.json`

배포용 산출물:

- Backend: `artifacts/m915/backend-linux-x64`, 11 files / 5,571,148 bytes.
- Backend DLL SHA256: `A181A511B6A871C625363EC4A7A2F4FAA54F7C905F43299FD268E11E27497BB3`
- Windows: `artifacts/m915/wpf-win-x64/GachaOverlay.App.exe`, 179,118,667 bytes.
- WPF SHA256: `836ED6F0C7CB0735EA709615E7F1008B0C7DC50C3C57CC9B6FA25F6FDB41CB7A`

Docker context는 현재 추적 파일 및 무시되지 않은 새 소스에서 Backend/Core/Protocol 3개 project의 필요한 소스 134개를 격리해 restore/publish했습니다. Publish 결과는 12 files / 5,572,106 bytes입니다. 이것은 Docker image 실행 성공을 의미하지 않습니다.

## 12. Files Removed

다음 5개를 제거했습니다. 모두 기존 Git HEAD에 남아 있어 복구 가능한 소스이며, 사용자 운영 데이터는 삭제하지 않았습니다.

| 경로 | 이전 용도 |
|---|---|
| `src/LSOverlay.Backend/Pairing/DiscordPairingCommand.cs` | slash 명령 등록·interaction 승인 |
| `src/LSOverlay.Backend/Pairing/PairingHealth.cs` | 구형 명령 준비 상태 |
| `src/LSOverlay.Backend/Pairing/PairingService.cs` | pair code/session/claim 상태 |
| `tests/GachaOverlay.Tests/Backend/M9121PairCommandPermissionTests.cs` | 구형 slash 권한·승인 검증 |
| `tools/dev/run-ls-m9121-local.ps1` | 구형 ordinary-user slash pairing audit |

## 13. Files Changed

경로는 repository 기준입니다. 아래 표는 삭제 항목 외 변경/추가 파일 전체입니다.

| 경로 | 목적 |
|---|---|
| `src/LSOverlay.Backend/Migrations/SlashPairingRetirementMigration.cs` | 신규: 정확한 legacy 명령 삭제·부재 확인 |
| `src/LSOverlay.Backend/Migrations/SlashPairingRetirementWorker.cs` | 신규: 독립·제한된 startup migration |
| `src/LSOverlay.Backend/Program.cs` | 구형 DI 제거, migration worker 등록 |
| `src/LSOverlay.Backend/Discord/DiscordGatewayAdapter.cs` | slash event/registration 의존 제거 |
| `src/LSOverlay.Backend/Security/CryptographicSecrets.cs` | 구형 UserCode 기능 제거 |
| `src/LSOverlay.Backend/Transport/BackendWebApi.cs` | pair route와 전용 rate limiter 제거 |
| `src/LSOverlay.Backend/Transport/TransportAuthentication.cs` | old claim parser 제거 |
| `src/LSOverlay.Backend/Transport/TransportMetrics.cs` | 구형 pairing metric 제거 |
| `src/LSOverlay.Protocol/ProtocolContracts.cs` | old pairing 계약 제거 |
| `src/LSOverlay.RemoteClient/ILSOverlayRemoteClient.cs` | old pairing API 선언 제거 |
| `src/LSOverlay.RemoteClient/LSOverlayRemoteClient.cs` | old pairing HTTP 호출 제거 |
| `src/LSOverlay.RemoteClient/RemoteClientExceptions.cs` | 재인증 안내 용어 정리 |
| `src/LSOverlay.TransportProbe/Program.cs` | 기존 credential 기반 probe와 입력 경계 |
| `src/GachaOverlay.App/Services/RemoteChatProductionCoordinator.cs` | WebAuth-only coordinator와 인증 용어 |
| `src/GachaOverlay.App/Services/RemoteChatProductionCoordinator.WebAuth.cs` | localhost fallback 제거 |
| `src/GachaOverlay.App/Services/RemoteChatContracts.cs` | pair-code 상태 제거, login 상태 정리 |
| `src/GachaOverlay.App/Services/TrayIconService.cs` | 변경된 login health enum 대응 |
| `src/GachaOverlay.App/Lifecycle/ApplicationHost.cs` | login/cancel/forget callback 연결 |
| `src/GachaOverlay.App/Presentation/RemoteChatSettingsViewModel.cs` | 로그인 명령 및 code UI 상태 제거 |
| `src/GachaOverlay.App/Presentation/FoundationWindow.xaml` | 설정 pair-code UI 제거 |
| `src/GachaOverlay.App/Presentation/FoundationWindow.xaml.cs` | login 취소 명칭 대응 |
| `src/GachaOverlay.App/Presentation/OnboardingWindow.xaml` | 온보딩 pair-code UI 제거 |
| `src/GachaOverlay.App/Presentation/OnboardingWindow.xaml.cs` | login 취소 명칭 대응 |
| `src/GachaOverlay.Infrastructure/Localization/Resources/Strings.resx` | 영어 인증 문구 정리 |
| `src/GachaOverlay.Infrastructure/Localization/Resources/Strings.ko.resx` | 한국어 인증 문구 정리 |
| `src/GachaOverlay.Infrastructure/Localization/Resources/Strings.ja.resx` | 일본어 인증 문구 정리 |
| `tests/GachaOverlay.Tests/Architecture/M915WebAuthFinalizationTests.cs` | 신규: retired architecture·UI 상태 회귀 |
| `tests/GachaOverlay.Tests/Backend/M915SlashRetirementTests.cs` | 신규: 명령 migration 안전성 |
| `tests/GachaOverlay.Tests/Backend/M915CredentialCompatibilityTests.cs` | 신규: 양쪽 발급 credential 호환성 |
| `tests/GachaOverlay.Tests/Backend/M92BoundaryTests.cs` | obsolete metadata 테스트 제거 |
| `tests/GachaOverlay.Tests/Backend/M92PairingCredentialTests.cs` | obsolete PairingService 테스트만 제거 |
| `tests/GachaOverlay.Tests/Backend/M92KestrelIntegrationTests.cs` | credential fixture, removed-route 검증 |
| `tests/GachaOverlay.Tests/Backend/M92TransportStateTests.cs` | legacy claim parser 제거 확인 |
| `tests/GachaOverlay.Tests/Backend/M93KestrelChatIntegrationTests.cs` | 채팅 fixture에서 old pairing 제거 |
| `tests/GachaOverlay.Tests/Backend/M94ProductionRemoteModeTests.cs` | WebAuth fake·재연결·재인증 테스트 유지 |
| `tests/GachaOverlay.Tests/Backend/M914WebAuthCoordinatorTests.cs` | WebAuth-only 및 loopback 회귀 |
| `tests/GachaOverlay.Tests/Backend/M9131DockerBuildGraphTests.cs` | pending deletion context 회귀 및 격리 fixture 정리 |
| `tests/GachaOverlay.Tests/Presentation/OptionDisplayTests.cs` | 새 snapshot와 login callback 대응 |
| `tools/dev/LSOverlay.WebAuthEnvironment.ps1` | 신규: 격리 Backend OAuth 환경 사전 검사·자식 격리 |
| `tools/dev/run-ls-m92-local.ps1` | 기존 credential의 개발 probe |
| `tools/dev/run-ls-m93-local.ps1` | 기존 credential의 개발 probe |
| `tools/dev/run-ls-m94-local.ps1` | WebAuth 환경 조건과 browser login 안내 |
| `tools/dev/run-ls-m99-audit.ps1` | slash readiness 의존 제거, WebAuth audit |
| `tools/dev/run-ls-m911-local.ps1` | browser login 검증 문구 |
| `tools/dev/verify-backend-docker-context.ps1` | index에만 남은 삭제 파일을 제외하고 필수 graph는 검증 |
| `docs/deployment/m915-web-auth-finalization.md` | 신규: 이 보고서 및 배포 후 검증 |
| `docs/deployment/m915-validation.json` | 신규: 수치·증거·미검증 범위 기록 |

## 14. User Actual Validation

M9.15 WEB AUTH FINALIZATION: NOT RUN

- Existing credential auto-connect: NOT RUN
- Clean Web OAuth: NOT RUN
- Legacy slash command absent: NOT RUN

사용자 검토·commit·main push 후 Railway 자동 배포가 완료되면 다음 순서로 확인합니다.

1. Railway 새 배포가 Online인지, `/healthz`가 HTTP 200인지 확인합니다.
2. 위 migration 완료 로그를 확인합니다. pending/deferred만 보이면 아직 명령 폐기 완료가 아닙니다.
3. 기존 앱을 트레이에서 종료한 뒤 `artifacts/m915/wpf-win-x64/GachaOverlay.App.exe`를 실행합니다.
4. 먼저 기존 credential을 삭제하지 않은 상태로 브라우저 없이 Chat/Sales/Presence 자동 연결을 확인합니다.
5. 재인증도 검증하려면, 4번 결과를 확인한 후 사용자가 명시적으로 Remote credential 삭제/로그아웃을 선택합니다.
6. “Discord로 로그인”만 보이고 code/slash 안내가 없는지 확인합니다.
7. 브라우저 로그인 후 앱 연결과 재실행 시 자동 연결을 확인합니다.
8. Discord를 Ctrl+R 또는 정상 새로고침한 뒤 LS Overlay의 `/lsoverlay pair`가 더 이상 보이거나 사용되지 않는지 확인합니다.

기존 인증을 보존하는 검증을 먼저 수행합니다. Bot 재초대, secret 재발급, 일반 채널의 Application Commands 권한 완화는 필요하지 않습니다. 일반 사용자는 개발 helper의 Bot Token/OAuth 설정을 입력하지 않습니다.

## 15. Final Legacy Pairing Status

SLASH PAIRING RETIREMENT BLOCKED

정확한 남은 항목은 운영 배포 및 운영 상태 확인입니다. 로컬 신규 코드는 slash 인증에 의존하지 않지만, 사용자가 main에 푸시하기 전에는 기존 운영 Backend 코드와 Discord 등록 명령이 M9.15로 교체·폐기되었다고 판정할 수 없습니다.

사용자 push → Railway M9.15 배포 → migration 부재 확인 → 기존 credential / WebAuth 실사용 확인까지 끝나야 최종 “fully retired”로 변경할 수 있습니다. 이번 작업에서 이를 대신 실행하지 않았습니다.

## 16. Optional Discord Server Cleanup

명령 폐기 확인 후, 서버 설정 → 연동 → LS Overlay의 구형 명령 전용 override가 남아 있다면 사용자가 선택적으로 정리할 수 있습니다. 다른 앱 또는 서버 전체 권한은 건드리지 않습니다. 이 정리는 앱 정상 동작의 필수 조건이 아닙니다.

Bot 설치 때 `applications.commands` scope가 포함되어 있어도 등록된 명령과 인증 의존성을 제거하면 그 scope 자체가 slash 인증 기능을 되살리지 않습니다. 현재 설치 scope를 직접 조회·수정하지 않았고 Bot 재초대도 하지 않았습니다. [Discord 설치/명령 scope 설명](https://docs.discord.com/developers/interactions/application-commands)

## 17. Deferred Work

다음은 시작하지 않았습니다.

- `status.revo32.cloud`
- LS Overlay 2.0 UI/UX + Branding Overhaul
- 최종 namespace/project/package rename
- 다중 사용자 4–5시간 RC soak
- release engineering

M9.15 운영 검증과 이후 migration 코드 제거 판단은 별도로 남습니다. 테스트 한 번의 진행 정지는 원인 미확정 기록으로 남기며, 장시간 안정성 PASS를 주장하지 않습니다.

## 18. Recommended Next Stage

먼저 사용자 검토·push와 위의 짧은 운영 검증을 완료합니다.

slash retirement까지 확인되면 다음 권장 단계는 **LS Overlay 2.0 UI / UX + Branding Overhaul**입니다. 자동으로 시작하지 않습니다.

그 시점의 제품 인증·데이터 구조는 Remote-only, Web OAuth onboarding, no UIA, no Local RPC, no Legacy OAuth, no slash pairing, no Application Commands authentication dependency입니다.
