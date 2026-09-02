# M9.14 — Discord 브라우저 로그인

## 상태와 범위

M9.13.1 커밋 `250b20d`의 원격 Backend에 identity-only 웹 로그인을 추가한다.
기존 Remote credential 형식, DPAPI 파일, 채팅/판매/세션 프로토콜, Bot 권한과 Gateway intents는 유지한다.
M9.14를 GitHub/Railway에 배포하거나 Discord 설정을 변경하는 작업은 자동으로 수행하지 않는다.
실제 일반 구성원의 웹 로그인은 **배포 후 사용자 검증 필요**이며 자동 테스트 통과와 구분한다.

## 사용자 흐름

1. 신규 WPF의 기본 서버는 `https://overlay.revo32.cloud`이다. 별도 주소 입력 없이 **Discord로 로그인**을 선택한다.
2. WPF는 기존 설치 ID로 Backend에 로그인 세션을 만든다.
3. Backend가 만든 공식 `https://discord.com/oauth2/authorize` 주소를 Windows 기본 브라우저로 연다.
4. Discord 동의 후 Backend callback이 신원과 Target Guild 구성원 여부를 확인한다.
5. 브라우저에는 LS Overlay 성공/실패 안내만 반환한다. 토큰 복사나 사용자 지정 URI는 없다.
6. WPF가 비공개 claim 헤더로 승인 결과를 받아 기존 Remote credential을 DPAPI로 저장한다.
7. 기존 채팅·판매·Presence 연결 절차를 시작한다. 허용된 메인 채널 선택은 기존 정책을 유지한다.
8. 다음 실행부터는 저장된 credential을 재사용한다. 일시적인 장애는 credential을 삭제하거나 브라우저를 강제로 열지 않는다.

기존에 저장한 서버 주소는 덮어쓰지 않는다. 로컬 개발용 주소도 유지한다. 따라서 이전 개발 설치본은
필요할 때 Discord 설정의 주소를 운영 주소로 직접 변경해야 한다. 신규 일반 사용자에게는 불필요하다.
기존 slash-pair credential도 그대로 유효하며 재로그인을 강제하지 않는다.

## 운영 설정 — 사용자가 직접 수행

### Discord Developer Portal

LS Overlay Application → OAuth2 → Redirects에 정확히 다음을 등록한다.

`https://overlay.revo32.cloud/auth/discord/callback`

Client Secret은 ChatGPT, GitHub, 로그, 셸 명령 인수에 붙여넣지 않는다.
Discord Application의 Client ID/Secret은 운영자가 Railway Variables UI에 직접 입력한다.
Public Client 설정, Bot 권한, Gateway intents, 채널의 Use Application Commands 권한을 확대할 필요가 없다.

### Railway Variables

| 이름 | 값 |
| --- | --- |
| `LSO_DISCORD_WEB_AUTH_ENABLED` | 준비 완료 후 `true` |
| `LSO_DISCORD_OAUTH_CLIENT_ID` | 해당 Application의 Client ID를 운영자가 입력 |
| `LSO_DISCORD_OAUTH_CLIENT_SECRET` | 해당 Application의 Client Secret을 운영자가 입력 |
| `LSO_PUBLIC_BASE_URL` | `https://overlay.revo32.cloud` |

기존 `LSO_DISCORD_BOT_TOKEN`, `LSO_DISCORD_GUILD_ID`, `LSO_SESSION_HOST_1_ID`,
`LSO_SESSION_HOST_2_ID`, `ASPNETCORE_URLS`, `RAILWAY_DEPLOYMENT_DRAINING_SECONDS`와 `/data` Volume은 유지한다.
Replica는 1개. Secret은 설정 검증 후 Railway의 sealed variable 사용을 권장한다.

### 단계적 배포

- 코드가 먼저 배포되어도 enabled가 없거나 `false`이면 OAuth 서비스/라우트는 등록되지 않는다(404).
  기존 Backend와 slash pairing은 계속 동작한다. 기존 인증정보 사용자도 그대로 연결한다.
- enabled가 `true`일 때 ID, Secret, public origin을 검증한다. 누락/잘못된 설정이면 **Backend 시작을 거부**하고
  값이 포함되지 않은 설정 오류를 반환한다. 불완전한 인증 상태로 시작하지 않는다.
- public origin은 HTTPS 절대 주소이며 사용자정보·경로·쿼리·fragment·경로 정규화 트릭을 거부한다.
  선택적으로 끝의 `/` 하나만 허용한다. Callback은 고정 경로에서 파생한다.
- HTTP는 명시적인 Development 환경의 loopback에서만 허용한다. Railway에서는 Development여도 허용하지 않는다.
- 운영 Redirect 등록 → ID/Secret/origin 입력 → enabled=true → 사용자가 커밋·푸시/배포 → `/healthz` 200 확인 순서로 진행한다.
  이미 코드가 배포된 경우에는 변수 적용에 따른 재배포를 확인한다.
- 잘못된 활성 설정으로 readiness가 실패하면 올바르게 수정하거나 enabled=false로 되돌려 재배포한다.
  credential Volume을 지우거나 재발급할 필요는 없다.

## 프로토콜과 비밀 경계

| 요청 | 의미 |
| --- | --- |
| `POST /api/v1/auth/discord/sessions` | protocolVersion + clientInstallationId만 허용. 추가 신원/redirect/secret 필드는 400 |
| `GET /auth/discord/callback` | code/state 또는 OAuth error. 신원 확인 후 승인만 기록 |
| `GET /api/v1/auth/discord/sessions/{sessionId}` | `Authorization: LSOAuthClaim <ClaimSecret>`으로 상태/일회성 credential 수령 |
| `DELETE /api/v1/auth/discord/sessions/{sessionId}` | 같은 claim 헤더로 해당 로그인 취소 |

Authorization Code Grant, scope는 정확히 `identify`만 요청한다.
Client ID/Secret, Guild ID, callback 주소는 Backend가 결정한다.
state와 ClaimSecret은 각각 독립적인 CSPRNG 256-bit 값이다. Backend에는 둘 다 SHA-256 hash만 보관한다.
비교는 고정시간 hash 비교를 사용한다. state는 callback의 외부 호출 전에 소비하여 동시 callback/replay를 차단한다.
별도의 256-bit PKCE verifier와 S256 challenge로 다른 세션의 code가 섞이는 것을 추가로 차단한다.
verifier는 메모리에만 존재하고 callback 처리 시작 시 세션에서 제거한다.

Token 교환은 Backend 공유 HttpClient의 form-urlencoded POST다. 임의 redirect 및 자동 HTTP redirect는 허용하지 않는다.
access token은 `/api/v10/users/@me`에만 사용한다. bot/system 계정과 잘못된 응답은 거부한다.
OAuth access/refresh token은 DB, `/data`, 설정, WPF, DPAPI에 저장하지 않는다.
refresh 로직 및 revocation 호출은 추가하지 않았다. 로컬 참조를 버리는 것이 Discord 서버에서의 token 폐기를 뜻하지는 않는다.
관리 언어 메모리의 즉각적인 완전 삭제를 보장하지는 않는다.

기존 Bot-side `IGuildMembershipVerifier`를 재사용한다. Member만 승인하고 NotMember는 거절,
VerificationUnavailable은 재시도 가능한 실패로 닫는다. 일시 실패를 구성원 결과로 캐싱하지 않는 기존 보정을 유지한다.
승인 이후에도 채널 접근·Target Guild·AccessRevoked·판매글 소유권 검사는 계속 적용된다.
`GuildMembers` intent는 OFF이며 기존 다른 intents와 Bot 권한은 수정하지 않았다.

승인된 세션의 owning claimant에게만 기존 `ClientCredentialRegistry.Issue`로 발급한다.
설치 ID/Discord user/Target Guild 바인딩, 180일 만료, 서버 hash-only 저장, 최대 128개 registry,
원자적 파일 저장·backup recovery와 Windows CurrentUser DPAPI를 유지한다.
발급 시도 전에 세션을 Claimed로 소비한다. 디스크 오류나 HTTP 전달 실패가 나도 같은 세션에서 재발급하지 않는다.
이때는 새 브라우저 로그인을 해야 한다. 동일 설치의 새 credential은 기존 registry 정책대로 이전 것을 대체한다.

## 제한과 수명

- 로그인 세션 최대 128개, 5분 만료. 완료/거절/claimed 결과도 최대 그 기한까지만 보관한다.
- 요청 시 정리 + 단일 30초 정리 worker. 인증 시도마다 영구 timer를 만들지 않는다.
- IP별 start 10/min, callback 60/min, claim/cancel 3,000/min.
- 전역 start/callback 각각 600/min, claim/cancel 12,000/min. source hash table 최대 1,024개.
- IP별 값은 메모리의 hash key이고 로그/metric label/파일로 내보내지 않는다.
  같은 NAT에서 50명이 2초 간격으로 polling해도 claim 한도를 넘지 않는다.
  같은 NAT에서 10회가 넘는 신규 시작은 다음 1분 창까지 기다려야 한다.
- Railway client IP는 기존 trusted proxy/X-Real-IP 처리 이후 값만 사용한다. 사용자 X-Forwarded-For는 사용하지 않는다.
- callback query 최대 4,096자, code 최대 2,048자, auth request body 최대 1,024 bytes.
  기존 Kestrel request-line/header 제한도 유지한다.
- Discord HTTP 각각 10초·응답 16KiB, callback 전체 30초. 무한 재시도 없음.
- WPF는 2초 간격·최대 5분 polling, 요청당 15초. 취소 정리는 최대 3초이며 실패하면 Backend TTL로 정리된다.
- 중복 클릭을 막고 설정/온보딩 닫기, 취소, 주소 변경, 앱 종료 시 polling을 취소한다. 사용자 브라우저를 종료하지 않는다.
- Backend 재시작 중 pending OAuth는 유실되어 재시도해야 하지만 `/data`의 Remote credential은 유지된다.

## 로그와 브라우저 응답

브라우저 페이지는 외부 이미지/폰트/script/analytics 없는 LS Overlay 텍스트 안내다.
고정 문구만 출력하며 쿼리/Discord 오류 본문을 반사하지 않는다.
`Cache-Control: no-store`, `Pragma: no-cache`, `Referrer-Policy: no-referrer`, `nosniff`,
CSP `default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`를 적용한다.

ASP.NET Hosting.Diagnostics의 요청 시작/완료 로그는 middleware보다 먼저 query를 기록하므로 해당 채널을 차단한다.
HTTP body/header logging도 차단하고, 배포 측 상세 로그 설정이 이 두 채널을 다시 켜지 못하도록 최종 정책을 적용한다.
OAuth HttpClient에 요청/응답 logging handler를 붙이지 않는다.
중앙 redactor는 callback 전체 query, code/state, access/refresh token, Client Secret, ClaimSecret, verifier를 삭제한다.
진단 파일에 새 OAuth 객체를 넣지 않는다. metric은 시작/승인/거절/만료/claim/일시실패의 고정 카운터뿐이다.

Railway **edge HTTP logs는 애플리케이션 로그와 별개**이며 앱이 이미 기록된 edge 요청을 지울 수는 없다.
공식 공개 로그 스키마는 path 등을 문서화한다. 실제 운영 dashboard에는 접근하지 않았으므로,
활성화 전 비밀이 아닌 합성 query marker로 edge/deploy 로그에 query가 저장되지 않는지 운영자가 확인해야 한다.
저장된다면 edge 쪽 비기록/마스킹 정책을 먼저 확인한 후 실제 OAuth를 진행한다.
실제 code/state/Secret을 로그 검색어로 붙여넣거나 보고서에 복사하지 않는다.

## 일반 구성원 실제 검증 — 아직 실행하지 않음

기존 개인 인증정보 삭제 대신, 일반 구성원의 새 설치/별도 Windows 테스트 프로필을 권장한다.
실제 테스트가 필요한 이유는 관리자 계정 성공이나 '해당 역할로 보기'가 일반 계정의 인증 성공을 증명하지 않기 때문이다.

1. Administrator/Manage Guild 권한이 없고 제한 채널의 Application Commands 사용도 막혀 있는 실제 계정을 준비한다.
2. 채널/역할/Integration 권한은 변경하지 않는다. 새 M9.14 WPF를 실행한다.
3. Discord로 로그인 → 공식 discord.com 주소 → identify 동의 → LS Overlay 완료 페이지를 확인한다.
4. WPF가 자동으로 연결되는지 확인한다. 처음이면 허용된 메인 채널을 선택한다.
5. 최근/새 채팅, Sales, 선택한 Session HUD를 확인한다. `/lsoverlay pair`는 사용하지 않는다.
6. 앱을 완전히 종료했다 다시 실행한다. 브라우저 없이 같은 계정으로 자동 재연결되어야 한다.
7. 가능하면 별도 새 로그인 시도에서 Discord 동의 취소, WPF 로그인 취소를 확인한다.
   운영 서버에서 계정을 탈퇴시키거나 역할을 바꾸는 파괴적 검사는 요구하지 않는다.
8. 실제 결과를 보고한 후에만 일반 사용자 Web OAuth PASS를 판정한다. 단기 검증은 다중 사용자 장시간 soak PASS가 아니다.

새 산출물은 `artifacts/m914-1788391468412/wpf-win-x64/GachaOverlay.App.exe`에 생성됐다.
Release / win-x64 / self-contained single-file 산출물이며 .NET 런타임을 포함한다.
기존 앱을 종료한 뒤 새 폴더의 실행 파일을 사용한다. 폴더만 새로 바꿔도 사용자 데이터가 자동 격리되는 것은 아니다.

## 임시 slash fallback / 유보 사항

Backend slash 등록·pairing API는 이번에 삭제하지 않았다. Web auth enabled에서는 일반 WPF의 주 흐름은 브라우저다.
OAuth가 없는 loopback 개발 Backend만 기존 코드 방식을 자동 사용할 수 있고, 운영 주소에서 404를 받으면
일반 사용자에게 slash 명령을 안내하지 않고 '브라우저 로그인 사용 불가'를 표시한다.
운영의 임시 관리자 fallback은 기존 버전/개발 도구를 사용할 수 있다.
실제 일반 사용자 검증 후 별도 좁은 수정에서 slash 등록·계약·state를 정리하는 것을 권장한다.

status.revo32.cloud, M10/LS Overlay 2.0 UI 전면 개편, 최종 branding, RC soak, release engineering은 유보한다.

## 참고한 공식 자료

- [Discord OAuth2](https://docs.discord.com/developers/topics/oauth2): Authorization Code Grant, identify, form token exchange.
- [Discord account linking — server-side PKCE exchange](https://docs.discord.com/developers/discord-social-sdk/development-guides/account-linking-with-discord): confidential client의 code_verifier 전송. Social SDK/RPC 자체는 도입하지 않는다.
- [Discord User resource](https://docs.discord.com/developers/resources/user): 현재 사용자 조회.
- [Railway HTTP logs](https://docs.railway.com/cli/logs): 앱 외부 edge 로그 경계와 공개 필드.
