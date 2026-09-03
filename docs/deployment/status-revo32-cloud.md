# M10.1 — 공개 문서·상태 서비스 수동 배포

작성: 2026-09-03. 이 문서는 **사용자가 검토한 뒤 직접 수행할 절차**입니다.
이번 작업에서 commit/push, Railway 배포·서비스·변수, Cafe24 DNS, Discord Portal은 변경하지 않았습니다.
운영 URL에서 이번 코드가 이미 실행 중이라는 의미가 아닙니다.

## 1. 제출 전 준비

운영자가 공식 문의 이메일을 **revo.32.39.41@gmail.com**으로 확정했습니다.
`src/LSOverlay.Backend/PublicWeb/PublicServicePages.cs`의 **ContactEmail 한 곳**에서
표시 주소와 `ContactUrl`의 mailto 링크를 파생하며 Privacy/Terms의 공통 문의 영역에 사용합니다.
LS Overlay 이용 문의·개인정보 관련 문의·데이터 삭제 요청을 위한 의도된 공개 정보입니다.
배포 후 정확한 주소·링크를 확인하세요. 실제 삭제 요청의 본인 확인·처리 절차는 별도로 운영해야 합니다.

**PUBLIC CONTACT VERIFIED**

개인정보처리방침의 근거는 [소스 감사](../compliance/M10.1-data-processing-audit.md)입니다.
법률 검토·인증 완료나 Discord 심사 통과를 의미하지 않습니다.

## 2. 배포 전 오프라인 화면 확인

Windows에서 .NET 8 SDK가 설치된 상태로 실행합니다. Discord/Bot/운영 인증정보가 필요 없습니다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\Codex\Projects\Gacha_Overlay\tools\dev\preview-ls-m101-public.ps1"
```

`http://127.0.0.1:5191/`에서 Privacy/Terms와 정상·지연·점검·불가·확인 중·API 실패 예시를 엽니다.
모든 상태 값은 **오프라인 테스트 예시**이며 운영 상태가 아닙니다.
종료는 해당 창의 Ctrl+C입니다. 이 도구는 Backend의 production host를 시작하지 않습니다.
5191이 사용 중이면 `-Port 5192`처럼 비어 있는 로컬 포트를 지정하세요.
`/responsive/320/privacy`, `/responsive/768/terms`, `/responsive/1280/operational` 등은
미리보기 전용 CSS viewport 검사이며 운영 서버에는 포함하지 않습니다.

## 3. 서비스별 빌드 계약

| 항목 | 기존 Backend | 새로운 상태 사이트 |
| --- | --- | --- |
| 저장소 | 사용자가 검토한 현재 GitHub 저장소 | 같은 저장소, 별도 Railway 서비스 |
| Root Directory / Docker context | 저장소 루트 | **저장소 루트** |
| Dockerfile | `Dockerfile` | `web/status/Dockerfile` |
| Dockerfile별 입력 제외 규칙 | `.dockerignore` | `web/status/Dockerfile.dockerignore` |
| 실행 | 기존 .NET Backend | BusyBox 1.37.0 `httpd`, UID/GID 10001 |
| 공개 주소 | `overlay.revo32.cloud` | `status.revo32.cloud` |
| Healthcheck | 기존 `/healthz` | `/` (페이지 제공 여부만 검사) |
| 포트 | 기존 `PORT` 처리 유지 | `0.0.0.0:$PORT`, 미지정 시 8080 |
| Secret/Volume | 기존 설정 유지 | **불필요. 복사·공유하지 않음** |

상태 서비스의 Root Directory를 `web/status`로 좁히지 마세요.
Dockerfile은 루트의 원본 `assets/branding/LS_Overlay_logo.png`를 빌드 중 복사합니다.
Dockerfile 전용 ignore는 정확한 정적 파일 7개만 허용합니다. 최종 웹 루트는 HTML/CSS/JS/로고 4개입니다.
Backend 이미지에는 이 상태 사이트나 미리보기 도구가 들어가지 않습니다.

신규 상태 서비스에는 `RAILWAY_DOCKERFILE_PATH=web/status/Dockerfile`를 **서비스 전용 변수**로 설정하세요.
기존 Backend 서비스에 이 변수를 설정하지 마세요. 별도 Build/Start Command는 지정하지 않고
Dockerfile의 ENTRYPOINT를 사용합니다. [Railway Dockerfile 설정](https://docs.railway.com/builds/dockerfiles)

기존 Backend의 프록시 신뢰 계약을 보존하기 위해 **별도 Railway 프로젝트/격리 환경에 상태 서비스 하나**를
만드는 구성을 권장합니다. 기존 Backend 프로젝트에 임의의 새 서비스를 넣거나 private network를 공유하지 마세요.
브라우저는 공개 HTTPS API만 호출하므로 private networking, service references, Volume, Bot Token,
OAuth Client Secret, Remote credential, Host/Guild/Channel ID가 모두 불필요합니다.

## 4. A–P 수동 순서

A. 현재 diff를 검토합니다. M10 이후 판매완료 전용 UI와 기존 staged branding도 의도한 상태인지 확인합니다.
   확정된 공개 문의처와 정책 본문을 검토합니다.

B. 사용자가 필요한 변경을 commit/push합니다. 자동 전체 staging은 권장하지 않습니다.
   실제 토큰·진단 ZIP·로그·상태 파일·로컬 artifacts는 포함하지 않습니다.

C. 기존 Backend Railway 서비스의 GitHub 연동 배포를 확인합니다. 기존 루트 Dockerfile,
   Web OAuth 변수, Volume, 단일 replica, callback 주소를 유지합니다.

D. `https://overlay.revo32.cloud/healthz`가 준비 완료 후 HTTP 200과 `{"status":"ok"}`를 반환하는지 확인합니다.

E. `/privacy`가 로그인 없이 한국어 HTML로 열리고, 실제 문의처·업데이트 날짜가 올바른지 확인합니다.

F. `/terms`도 동일하게 확인합니다. 기존 공식 Discord OAuth 로그인과 자격 증명 재사용을 확인합니다.

G. `/status/public`의 명시적 네 서비스 상태, UTC 시각, `Cache-Control: no-store`를 확인합니다.
   준비되지 않은 서비스가 있으면 원인을 먼저 확인합니다. HTTP 200 자체가 모든 기능 정상이라는 뜻은 아닙니다.

H. 새 Railway 프로젝트/격리 환경에서 **같은 GitHub 저장소를 연결한 별도 상태 서비스**를 만듭니다.
   소스 연결 즉시 배포가 시작될 수 있으므로 가능하면 empty service로 설정을 먼저 준비합니다.
   기존 Backend를 복제하거나 비밀 변수/Volume을 복사하지 않습니다.

I. 저장소 루트 context와 `RAILWAY_DOCKERFILE_PATH=web/status/Dockerfile`를 설정하고 배포합니다.
   Build/Start Command override는 비워 둡니다. 로그에서 BusyBox 정적 이미지가 선택됐는지 확인합니다.
   이 서비스의 healthcheck는 `/`입니다. `PORT`를 직접 지정해야 한다면 8080 같은 비특권 포트를 사용하고
   public networking의 대상 포트도 같은 값으로 맞춥니다.

J. 신규 상태 서비스의 Settings → Networking → Public Networking에서 Generate Domain을 사용합니다.
   생성된 HTTPS 주소에서 HTML/CSS/JS/로고가 각각 열리는지 확인합니다.
   **임시 Railway 도메인은 API의 CORS 허용 출처가 아니므로 상태 값 확인이 실패하는 것은 예상된 동작입니다.**
   CORS를 `*`로 넓히지 말고 아래 최종 도메인을 연결하세요.

K. **신규 상태 서비스에만** Custom Domain으로 `status.revo32.cloud`를 추가합니다.

L. Railway가 실제로 제시한 CNAME 대상과 소유권 확인 TXT의 이름·값을 복사합니다.
   `<Railway-generated-hostname>`은 설명용 자리표시자이며 실제 DNS 값이 아닙니다.
   임의의 `*.railway.app` 주소를 만들거나 Backend의 대상 값을 재사용하지 마세요.

M. Cafe24의 `revo32.cloud` DNS에서 `status` CNAME을 실제 Railway 대상에 연결합니다.
   Railway에서 안내한 TXT도 그 정확한 이름·값으로 추가합니다. 현재 공식 안내는 CNAME과 TXT를 요구합니다.
   해당 이름의 기존 충돌 레코드가 있다면 소유·용도를 확인한 뒤 처리하세요.
   기존 `overlay` 레코드, 루트 도메인, MX 등 다른 레코드는 변경하지 않습니다.

N. DNS 검증과 TLS 인증서 준비를 기다립니다. `https://status.revo32.cloud`의 인증서가 유효하고
   HTTPS로 페이지·정적 리소스를 모두 제공하는지 확인합니다.
   [Railway Public Networking](https://docs.railway.com/networking/public-networking),
   [도메인 설정](https://docs.railway.com/networking/domains/working-with-domains)

O. 최종 도메인에서 네 가지 상태, KST 확인 시각, 60초 갱신, 모바일 화면, Privacy/Terms 링크를 확인합니다.
   개발자 도구의 네트워크에서 `/status/public` 요청과 정확한 ACAO를 확인하세요.
   정상 시 전체가 초록색이라는 추측이 아니라 JSON의 실제 상태와 화면을 비교합니다.
   실패 화면은 오프라인 예시로 점검할 수 있으므로 시험을 위해 운영 Backend를 중단할 필요가 없습니다.

P. 실제 문의처와 페이지 내용·접근성을 확인한 뒤 **사용자가 직접** Discord Developer Portal에 등록합니다.
   Privacy Policy URL: `https://overlay.revo32.cloud/privacy`
   Terms of Service URL: `https://overlay.revo32.cloud/terms`
   기존 callback `https://overlay.revo32.cloud/auth/discord/callback`은 변경하지 않습니다.

## 5. API 및 브라우저 수동 검증

아래는 배포 후 실행할 읽기 전용 확인 명령입니다. 이번 작업에서 운영 변경은 수행하지 않았습니다.

```powershell
curl.exe -i https://overlay.revo32.cloud/status/public
curl.exe -i -H "Origin: https://status.revo32.cloud" https://overlay.revo32.cloud/status/public
curl.exe -i -H "Origin: https://example.invalid" https://overlay.revo32.cloud/status/public
```

첫 요청: 자격 증명 없이 작은 상태 JSON. 두 번째: 정확한 `Access-Control-Allow-Origin`.
세 번째: 해당 헤더 없음. 어느 경우에도 `Access-Control-Allow-Credentials` 없음.
이 API는 원래 공개이므로 CORS는 인증/접근 제어가 아니라 브라우저 출처 제한입니다.

Bot은 Gateway Ready 후 **LS Overlay - 정상 가동 중**이라는 Custom Status인지 직접 확인하세요.
Playing/Watching 문구가 아니어야 합니다. 이 고정 문구는 공개 상태 페이지의 실제 상태와 자동 연동하지 않습니다.

## 6. 로컬 빌드 입력 검사와 Docker가 있는 PC의 추가 확인

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\Codex\Projects\Gacha_Overlay\tools\dev\verify-status-site-context.ps1"
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "E:\Codex\Projects\Gacha_Overlay\tools\dev\verify-backend-docker-context.ps1"
```

새 상태 사이트 도구와 미리보기는 Windows PowerShell 5.1에서 실행됩니다.
기존 Backend 검사 도구는 `#requires -Version 7.0`이 있으므로 **PowerShell 7의 pwsh.exe**로 실행해야 합니다.
이 PC에서 확인한 경로는 `C:\Users\Rev\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe`입니다.
PATH에서 찾지 못하면 이 절대 경로를 `&` 호출 연산자로 실행하거나 설치한 PowerShell 7의 실제 경로를 사용하세요.
검사 도구는 고유 Temp 폴더에서 입력 closure와 로고 원본 바이트를 확인합니다.
Docker CLI가 없는 이 작업 환경에서는 **Docker image build NOT RUN — Docker CLI unavailable**입니다.
실제 Linux 컨테이너의 bind/정적 HTTP 서빙은 배포 또는 Docker가 있는 PC에서 추가 확인해야 합니다.
다음 명령은 저장소 루트에서, 다른 컨테이너와 충돌하지 않는 이름/포트를 골라 사용합니다.

```powershell
docker build -f web/status/Dockerfile -t ls-overlay-status:m101-local .
docker run --rm --name ls-overlay-status-m101-check -e PORT=8097 -p 127.0.0.1:5192:8097 ls-overlay-status:m101-local
```

다른 창에서 `http://127.0.0.1:5192/`, `/styles.css`, `/status.js`, `/assets/ls-overlay-logo.png`를 확인하고
테스트 컨테이너를 Ctrl+C로 종료합니다. 로컬 origin에서는 운영 CORS 때문에 상태 fetch가 실패할 수 있습니다.
이 경우 정적 HTTP 제공과 실패 표시를 검증한 것이지 운영 연결 PASS는 아닙니다.

## 7. 장애와 되돌리기 범위

- Backend만 중단: 상태 사이트는 계속 열릴 수 있고 Backend를 이용 불가, 다른 세 서비스는 확인 불가로 표시합니다.
- 상태 사이트만 중단: Backend와 `/status/public`은 독립적으로 작동할 수 있습니다.
- Railway 전체 장애: 두 서비스 모두 열리지 않을 수 있습니다. 외부 감시·이력 시스템은 이번 범위가 아닙니다.
- 상태 사이트 배포 오류: **신규 상태 서비스만** 이전 배포로 되돌립니다. Backend Volume/변수/DNS를 건드리지 않습니다.
- Backend 배포 오류: 사용자 판단으로 기존 Railway rollback 절차를 따릅니다. 인증 레지스트리 파일·Volume을 삭제하지 않습니다.

현재 상태만 제공하며 uptime 비율·사건 이력·구독·점검 제어·로그인 웹사이트는 없습니다.
