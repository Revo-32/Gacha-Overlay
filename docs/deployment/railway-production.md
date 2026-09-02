# M9.13 — Railway production deployment foundation

이 문서는 **아직 배포하지 않은 코드 기반**과 운영자가 직접 수행할 첫 배포 절차를 설명합니다.
GitHub push, Railway 서비스 변경/배포, DNS 및 Web OAuth2 구현은 이번 작업에 포함하지 않습니다.
기존 slash pairing은 개발 검증용으로 유지하며, 일반 사용자의 채널 명령 권한은 완화하지 않습니다.

## 1. 운영 모델과 필수 조건

사용자가 준비한 구성: Railway Hobby / Singapore / `LSOverlay.Backend` 서비스 하나,
replica **1**, Discord Gateway 하나, `/data` Volume 하나(현재 500 MB).
이 환경에는 신뢰할 수 없는 다른 서비스를 함께 두지 마세요. 공개 진입점은 Railway HTTPS edge만 사용합니다.
Raw TCP Proxy를 만들거나 Backend 포트를 다른 공개 경로로 노출하면 아래 프록시 신뢰 조건을 충족하지 못합니다.

Replica를 늘리거나 같은 Bot을 사용하는 다른 Backend를 동시에 실행하지 마세요.
파일 기반 인증 레지스트리, 메모리 pairing/stream 상태는 다중 프로세스 공유를 지원하지 않습니다.
Volume이 연결된 서비스의 재배포에는 중단 시간이 생길 수 있으므로 무중단 배포를 보장하지 않습니다.
[Railway Volume 문서](https://docs.railway.com/volumes)

## 2. Docker 구조

루트 `Dockerfile`은 `mcr.microsoft.com/dotnet/sdk:8.0`에서
`src/LSOverlay.Backend/LSOverlay.Backend.csproj`만 restore/publish합니다.
실제 프로젝트 참조는 `GachaOverlay.Core`, `LSOverlay.Protocol` 두 개입니다.
솔루션/WPF/Infrastructure/RemoteClient/테스트 프로젝트는 컨테이너 빌드 대상이 아닙니다.

최종 이미지는 `mcr.microsoft.com/dotnet/aspnet:8.0`이며 framework-dependent 출력만 받습니다.
실행은 `ENTRYPOINT ["dotnet", "LSOverlay.Backend.dll"]`; 셸, supervisor, 프록시, 별도 DB 없이 단일 프로세스입니다.
인증서/HTTPS 개인 키는 넣지 않습니다. .NET 9로 변경하지 않습니다.
[Railway ASP.NET Core 안내](https://docs.railway.com/guides/aspnet-core)

`.dockerignore`는 세 프로젝트와 빌드 설정만 허용한 뒤 bin/obj, Git, 로그, state/data,
환경 파일, 인증정보, DPAPI 파일, 인증서, 압축 파일, 테스트/audit 결과 등을 다시 제외합니다.
Bot Token, Guild/Host ID, Remote Token 또는 OAuth Secret은 build args/ENV/COPY 입력이 아닙니다.

Railway Volume의 root 소유권과 맞추기 위해 최종 런타임은 명시적으로 UID 0을 사용합니다.
이는 컨테이너 내부 권한을 최소화한 구성은 아닙니다. 대신 현재 플랫폼 소유권 모델을 지원하고,
시작용 root 셸/chown 작업이나 전체 쓰기 권한(chmod 777)을 추가하지 않습니다.
호스트 소켓이나 추가 호스트 경로를 연결하지 마세요. 향후 비-root 전환은 Volume 소유권 지원을 확인한 별도 작업입니다.
[Volume 소유권 안내](https://docs.railway.com/volumes)

## 3. 서비스 변수와 포트

이미 등록한 변수는 그대로 유지합니다. 실제 비밀 값이나 실제 ID를 문서/저장소에 붙여넣지 마세요.

| 변수 | 설정 / 처리 |
| --- | --- |
| `ASPNETCORE_URLS` | 기존 `http://+:${PORT}` 유지 가능 |
| `LSO_DISCORD_BOT_TOKEN` | Railway 런타임 비밀 변수, Backend만 사용 |
| `LSO_DISCORD_GUILD_ID` | 실제 대상 서버 ID를 런타임에 설정 |
| `LSO_SESSION_HOST_1_ID` | 실제 Host 1 ID를 런타임에 설정 |
| `LSO_SESSION_HOST_2_ID` | 실제 Host 2 ID, 두 번째 Host를 사용할 때 설정 |
| `PORT` | Railway 제공 값을 사용, 코드의 고정 운영 포트 없음 |
| `RAILWAY_VOLUME_MOUNT_PATH` | 연결된 Volume에서 Railway가 자동 제공; 수동 중복 등록 불필요 |
| `LSO_BACKEND_DATA_DIR` | 선택 사항. 미설정 권장; 설정 시 Volume 내부 절대 경로만 허용 |
| `RAILWAY_DEPLOYMENT_DRAINING_SECONDS` | **운영자가 추가: `30`**, Backend 종료 제한 20초보다 길게 설정 |

Railway 감지 시 PORT 1–65535를 단일 기준으로 `0.0.0.0:PORT`에 HTTP 바인딩합니다.
exec-form 컨테이너에서는 셸 변수 확장을 기대할 수 없으므로 `${PORT}`와 `${{PORT}}`를 코드에서 처리합니다.
이미 확장된 wildcard HTTP URL도 PORT와 일치할 때만 허용합니다.
`ASPNETCORE_URLS`가 없더라도 PORT를 사용하며, 포트 불일치/복수 URL/HTTPS 내부 URL은 시작 오류입니다.
Railway에서는 `LSO_LISTEN_URL`을 제거해야 합니다. 이 설정으로 운영 PORT를 덮어쓸 수 없습니다.

로컬에서는 `LSO_LISTEN_URL` > `ASPNETCORE_URLS` > 기존 `http://127.0.0.1:5188` 순서입니다.
로컬 개발에 Railway 변수나 `/data`가 필요하지 않습니다. `LSO_DEV_SHUTDOWN_FILE`은 로컬 helper 전용이며 Railway에 설정하지 마세요.

## 4. TLS 및 프록시 신뢰 계약

외부 클라이언트는 HTTPS/WSS만 사용하고, Railway edge 이후 컨테이너까지는 내부 HTTP입니다.
`/api/v1/stream`의 WebSocket 업그레이드와 기존 subprotocol/인증/heartbeat는 유지합니다.
Railway는 장기 WebSocket 연결을 지원합니다. [공개 네트워크 사양](https://docs.railway.com/networking/public-networking/specs-and-limits)

처리 순서: forwarded header 형식 확인 → ASP.NET Core Forwarded Headers → 전송 보안 검사 → rate limit → endpoint 인증/권한 검사.
라우팅된 endpoint가 실행되기 전에 보안 검사가 수행됩니다.

정확한 정책:

- Railway 실행으로 감지된 경우에만 전달 헤더를 해석합니다.
- 실제 직전 peer가 loopback, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `100.64.0.0/10`, `fc00::/7`인 경우만 신뢰 후보입니다.
  이는 **Railway가 보장한 edge CIDR 목록이 아니라 비공개 주소 범위**입니다.
- `ForwardLimit=1`, `RequireHeaderSymmetry=true`. 단일 `X-Forwarded-Proto`와 유효한 단일 `X-Real-IP`만 받습니다.
  임의의 긴 전달 체인/복수 값은 거부합니다.
- Railway가 문서화한 `X-Real-IP`를 전달 IP 입력으로 사용합니다. 임의 `X-Forwarded-For`는 신뢰하지 않으며 rate-limit 식별에도 사용하지 않습니다.
- `X-Forwarded-Host`는 사용하지 않습니다. 공개 OAuth URL을 생성하는 코드도 없습니다.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`에 의한 전역 무제한 신뢰는 사용하지 않습니다. 자동 파이프라인 활성화 설정도 코드에서 끕니다.
- 공개 peer가 위조한 HTTPS 헤더는 무시되며, 평문 API/WS 요청은 403입니다.
  Railway 모드에서는 loopback API도 전달된 HTTPS 또는 실제 HTTPS가 아니면 거부합니다.
- 일반 로컬 개발의 실제 loopback HTTP/WS와 실제 HTTPS 동작은 유지합니다.

주소 범위만으로 Railway를 인증하는 것은 아닙니다. 신뢰 근거는 **서비스 하나만 둔 격리된 프로젝트/환경,
신뢰되는 운영자, Railway edge만 통한 공개 유입, raw TCP 공개 없음**의 조합입니다.
같은 환경의 악성 서비스는 비공개 주소에서 헤더를 위조할 수 있으므로 이 조건을 위반하면 안전하지 않습니다.
다른 서비스를 추가하거나 실제 edge peer가 범위를 벗어나면 신뢰 정책을 먼저 재검토해야 합니다.
알 수 없는 공개 proxy IP를 허용하기 위해 KnownNetworks/Proxies를 지우지 마세요.
[Railway private network 격리](https://docs.railway.com/networking/private-networking/how-it-works),
[ASP.NET Core 프록시 지침](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-8.0)

Railway 공개 HTTP 요청은 edge에서 HTTPS로 리다이렉트됩니다. Backend가 평문 요청을 안전하다고 허용하는 것은 아닙니다.
인증정보를 HTTP로 전송한 뒤 리다이렉트에 의존해서는 안 됩니다. WPF의 HTTPS 주소 검증은 유지됩니다.
[Railway HTTP redirect 동작](https://docs.railway.com/networking/troubleshooting/405-method-not-allowed)

## 5. 영구 저장소와 상태 목록

데이터 루트 순서:

1. `LSO_BACKEND_DATA_DIR`
2. 기존 로컬 helper 호환용 `LSO_STATE_DIRECTORY` (명시적 override)
3. `RAILWAY_VOLUME_MOUNT_PATH`
4. 로컬 실행 파일 디렉터리 아래 `state`

Railway에서는 4번으로 대체하지 않습니다. Railway project/environment/service 메타데이터 또는 Volume 변수가 있으면
Volume 절대 경로가 필수입니다. `/proc/self/mountinfo`에서 실제 Linux mount를 확인하고,
루트가 Volume 내부인지, symlink를 통과하지 않는지, 생성/쓰기/flush/rename/delete가 되는지 시작 시 점검합니다.
검사용 임시 파일은 정리합니다. 단순히 `/data`라는 폴더만 만드는 것으로는 통과하지 않습니다.
누락, 잘못된 경로, 권한/디스크 오류, 손상된 primary+backup은 시작 실패이며 임시 저장소 fallback은 없습니다.

실제 구현을 기준으로 한 server-side inventory:

| 상태 / 구현 | 분류 | 재시작 시 처리 |
| --- | --- | --- |
| `ClientCredentialRegistry`: 발급 credential hash, installation/user/guild 결합, 만료 시각 | PERSISTENT | Volume의 primary에서 복원. 별도 installation authorization 파일은 없으며 같은 레코드가 담당 |
| `client-credentials.v1.json.bak` | PERSISTENT | primary 불능 시 검증된 이전 커밋 상태 복구 |
| 쓰기/복구/preflight의 고유 임시 파일 | EPHEMERAL | 같은 디렉터리에서 원자적 교체 용도, 정상 완료 시 삭제 |
| `PairingService` 요청 코드/승인 상태/claim secret hash/일회성 claim | EPHEMERAL | 메모리 최대 64건, 2분; 재시작 시 새 요청 필요 |
| claim 완료 후 클라이언트가 받은 credential 원문 | Backend 영구 저장 안 함 | Backend는 hash만 저장; 클라이언트 보호 저장은 WPF의 별도 정책 |
| `BackendEventJournal`, `RemotePublicationHub` replay journal/generation | EPHEMERAL | 메모리 bounded journal, 재시작 후 새 generation/bootstrap |
| `ActiveChatStreamRegistry` 최근 메시지 | DERIVED | Discord에서 재조회; 최대 16채널, recent20, idle eviction |
| Chat mutation journal/subscription queues | EPHEMERAL | 채널별 128 journal / 256 outbound, 연결 시 재구독 |
| `ActiveSalesStreamRegistry` 메시지/완료 관측 | DERIVED | 기존 authoritative 판매 window 30과 Discord reaction으로 재구성 |
| Sales journal/subscriptions | EPHEMERAL | 256 journal / 256 outbound, 재시작 후 bootstrap |
| `TrackedHostPresenceStore`, publication host snapshots | DERIVED | 설정 Host slot 및 Gateway presence로 재구성; 새 서버 프로세스에서 옛 세션 수치 재사용 안 함 |
| `DiscordGuildMembershipVerifier` 구성원 lease | DERIVED | 최대 128건/5분; 다시 확인 |
| `ChatAuthorizationService` 권한 lease/catalog | DERIVED | 최대 128건/2분; Discord에서 다시 확인 |
| `CanonicalRemoteAuthorResolver` 이름 cache | DERIVED | 최대 512건/15분, 일시 실패 backoff 30초; 다시 조회 |
| REST refresh coalescer/in-flight 작업 | EPHEMERAL | 프로세스 단위 작업 조정, 파일 저장 없음 |
| `RemoteSalesActionService` dedupe/version/rate windows/gates | EPHEMERAL | bounded 메모리 제어; 재시작 후 Discord가 상태 원본 |
| HTTP rate-limit partitions, `RemoteConnectionLimiter` | EPHEMERAL | 요청 창/연결 lease 재생성 |
| WebSocket/socket/heartbeat/connection Chat·Sales state | EPHEMERAL | 종료 취소 및 lease/subscription 해제; 재연결 |
| Gateway session, callback drain gate, health, metrics, pairing health | EPHEMERAL | 새 Gateway와 런타임 상태로 시작 |
| 런타임 설정/TargetGuildFilter/Host slot mapping | DERIVED | Railway 환경 변수로 재구성 |
| 콘솔 로그 | EPHEMERAL (앱 로컬 파일 없음) | Railway 로그 수집 정책을 따름. `/data`에 채팅 로그를 만들지 않음 |
| `DeveloperShutdownWatcher` 제어 파일 경로 | EPHEMERAL / 개발 전용 | 환경 변수 미설정 시 비활성; 운영 인증 저장소 아님 |

이미지/스티커/emoji/첨부 파일의 Backend media cache는 없습니다.
승인된 URL을 WPF가 직접 가져오는 기존 구조를 유지합니다.

## 6. Credential 원자적 저장 및 Linux 검토

Volume 아래 `client-credentials.v1.json`과 `.bak`을 사용합니다. schema 1, 최대 128 credential, 기존 만료 정책을 유지합니다.
토큰 원문 대신 hash만 저장하지만 파일에는 계정/installation 연결 정보가 있으므로 Volume과 backup도 비공개로 보호하세요.

`Path.Combine`/`GetFullPath`를 사용하며 Windows 드라이브나 역슬래시 경로에 의존하지 않습니다.
고유 임시 파일을 같은 디렉터리에 `CreateNew`/`FileShare.None`으로 작성하고 `Flush(true)` 및 내용 검증 후 교체합니다.
기존 파일은 `File.Replace`(지원하지 않는 플랫폼에서는 기존 copy/move fallback), 최초 파일은 move로 저장합니다.
backup은 이전 커밋 상태이며 최신 발급 직전으로 복구될 수 있습니다. primary와 backup 모두 불능이면 fail closed입니다.
성공한 발급은 반환 전에 저장되므로 종료 시 뒤늦게 flush해야 하는 credential 큐는 없습니다.

이번 변경은 레지스트리 직렬화/교체 알고리즘을 재설계하지 않았습니다.
격리된 임시 경로에서 중첩 디렉터리, 반복 발급/재로드, 원문 미저장, primary 손상 시 backup 복구를 검증합니다.
Windows에서의 자동 테스트 및 Linux 대상 publish는 **실제 Linux Volume에서의 재시작/정전 내구성 검증을 대체하지 않습니다**.
단일 프로세스 전용이며 디렉터리 fsync/분산 lock/외부 backup을 새로 보장하지 않습니다.

## 7. Health와 종료

인증 없는 `GET /healthz`는 다음을 모두 만족하면 `200 {"status":"ok"}`:
Host 시작 완료, 종료 중 아님, credential registry 정상, 기존 Discord/Guild connection health가 Ready.
그 외에는 `503 {"status":"unavailable"}`입니다. 네트워크 호출, 브라우저/UI 상태 의존, 계정/메시지/설정 반환은 없습니다.
Railway의 내부 HTTP health probe를 위해 **정확한 GET /healthz만** 평문 전송 검사에서 예외 처리합니다.
이 예외는 인증정보 발급/조회나 다른 API/WS에는 적용되지 않습니다.

Healthcheck Path를 `/healthz`로 설정하세요. 기본 readiness timeout은 300초이며,
Railway 배포 healthcheck는 상시 장애 모니터가 아닙니다.
503이 지속되면 로그에서 Volume, Discord 인증, Gateway/대상 서버 상태를 먼저 확인하세요.
일반 사용자 slash 명령 채널 권한 문제는 별도 제품 인증 제약이며 이번 healthcheck로 해결되지 않습니다.
[Railway healthcheck 문서](https://docs.railway.com/deployments/healthchecks)

exec-form PID 1이 SIGTERM을 받으면 Generic Host가 종료를 시작합니다.
WebSocket 요청은 ApplicationStopping과 연결되어 취소되고 Chat/Sales/Presence subscription 및 연결 lease가 정리됩니다.
협조하지 않는 peer 때문에 종료가 무한 대기하지 않도록 연결은 취소/해제되며 클라이언트에는 재연결이 필요합니다.
Discord worker의 기존 callback drain/idempotent stop은 유지합니다. Host 종료 제한은 20초입니다.

**Railway 종료 유예를 30초로 설정해야 합니다.** 기본 0초에서는 정상 종료 전에 SIGKILL될 수 있습니다.
`RAILWAY_DEPLOYMENT_DRAINING_SECONDS=30` 또는 서비스의 Draining time을 설정하고 overlap은 늘리지 마세요.
[Railway 배포 종료 문서](https://docs.railway.com/deployments/reference)

## 8. 운영자가 수행할 첫 배포

1. 소스 검토/보안 확인 후 필요한 파일이 GitHub에 올라가도록 운영자가 직접 커밋·push합니다. 이 작업에서는 수행하지 않았습니다.
   현재 작업 트리에는 이전 마일스톤 변경도 있으므로 무작정 전체 파일을 포함하지 마세요.
2. 기존 `LSOverlay.Backend` Railway 서비스를 해당 GitHub 저장소에 연결합니다.
   Root Directory는 저장소 루트이며 루트 `Dockerfile`을 사용합니다. WPF 솔루션 build 명령을 지정하지 않습니다.
3. Singapore / replica 1 / `/data` Volume(500 MB) / 런타임 변수와 위 종료 유예 설정을 확인합니다.
   별도 Start Command, pre-deploy 작업, raw TCP Proxy는 설정하지 않습니다.
4. Healthcheck Path를 **배포 전에 가능하면 `/healthz`로 지정**하고 배포합니다. 이미 배포를 시작했다면 설정 후 재배포합니다.
5. build/start 로그를 확인합니다. 안전한 시작 요약에는 Production/Railway, Railway PORT, storage available,
   Discord configuration valid, Host 개수만 표시됩니다. 실제 토큰/ID/hash는 공유하지 마세요.
6. Networking에서 Railway public domain을 생성하고 서비스의 PORT를 대상으로 지정합니다.
7. `https://<railway-domain>/healthz`를 열어 HTTP 200과 `{"status":"ok"}`를 확인합니다.
8. 이후 현재 개발 pairing이 가능한 계정으로 Remote 연결과 WSS stream 복구를 수동 확인합니다.
   일반 사용자 slash 권한을 열어주는 절차가 아닙니다. 일반 사용자 최종 onboarding은 후속 Web OAuth2 작업 전까지 미완성입니다.
9. 첫 발급 후 재시작/재배포하여 기존 credential로 재연결되는지도 확인합니다. `/data`를 삭제하거나 새 빈 Volume으로 교체하지 마세요.
10. 배포 상태, health HTTP 상태, 비밀을 제거한 build/start 오류만 공유합니다. Bot Token/Remote Token/레지스트리 내용은 보내지 마세요.

## 9. 다음 단계와 검증 범위

임시 Railway HTTPS domain이 정상인 다음 수동 인프라 단계는 Cafe24의 `overlay.<user-owned-domain>`입니다.
DNS 수정은 아직 하지 않았습니다. 사용자 지정 HTTPS domain이 확인된 후에만 Web Discord OAuth2를 별도 설계합니다.
향후 callback 형태는 `https://overlay.<domain>/auth/discord/callback`이며 **현재 존재하는 endpoint가 아닙니다**.
Client Secret/identify/state/callback/브라우저 로그인은 추가하지 않았습니다.

현재 로컬 검증 결과는 `m913-validation.json`에 별도 기록합니다.
Docker가 없는 개발 환경에서는 이미지 build 및 이미지 layer 실검사는 NOT RUN이며,
Dockerfile/context 정적 검사와 `linux-x64` framework-dependent publish 결과만 보고합니다.
실제 Linux 실행, Railway edge의 HTTPS/WSS, Volume mount/권한 및 재배포 내구성은 첫 수동 배포에서 확인해야 합니다.
