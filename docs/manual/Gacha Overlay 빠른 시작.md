# Gacha Overlay

## 빠른 시작 가이드

- Version: 1.0.0-rc.1
- Document: Quick Start 1.0
- Updated: 2026-09-01

<!-- BODY -->

# 시작하기 전에

## 이 문서는 필수 설정 경로만 안내합니다

시간이 충분하다면 같은 폴더의 `Gacha Overlay 사용자 설명서.pdf`를 먼저 읽는 것을 권장합니다. 빠르게 설정하고 싶다면 이 문서만 따라 진행해도 됩니다.

준비물:

- Windows x64 PC
- Discord Desktop과 Discord 계정
- 대상 Discord 서버 접근 권한
- Gacha Overlay Release ZIP

별도 .NET 설치는 필요하지 않습니다. ZIP을 새 폴더에 완전히 압축 해제한 뒤 시작하세요.

<!-- FLOW: ZIP 압축 해제 | Discord Application | OAuth 정보 준비 | Gacha Overlay 실행 | Discord 인증 | HUD와 판매 대기열 -->

> [!RESULT]
> 압축을 푼 폴더에 EXE와 두 PDF, Licenses 폴더가 보이면 준비가 끝났습니다.

<!-- PAGE -->

# Discord Developer Application 만들기

## 개인 Application을 하나 만드세요

Client ID는 Discord Application을 식별하는 번호입니다. Client Secret은 Application의 비밀번호와 비슷한 값입니다. OAuth는 Gacha Overlay가 Discord 계정과 연결되는 인증 절차입니다.

**Discord Developer Portal → Applications → New Application**

1. Discord Developer Portal을 엽니다.
2. `New Application` 또는 `신규 애플리케이션`을 누릅니다.
3. 알아보기 쉬운 이름을 입력하고 Application을 생성합니다.

![① New Application](assets/1.0.0-rc.1-guide/02-new-application.png)

> [!RESULT]
> 새 Application의 설정 화면이 열리면 정상입니다.

<!-- PAGE -->

# Client ID · Client Secret · OAuth 설정

## OAuth2 화면에서 필요한 값만 준비하세요

**Discord Developer Portal → OAuth2 → Redirects**

OAuth2 화면에서 Client ID를 복사하고 Client Secret을 확인합니다. Client Secret이 이미 표시된다면 그대로 복사하세요. 이 설명서를 따라가기 위해 `Reset Secret` 또는 `비밀 키 초기화`를 누를 필요는 없습니다.

![compact: ① OAuth2 메뉴, ② Client ID와 Client Secret](assets/1.0.0-rc.1-guide/03-oauth2-page.png)

Redirects에 아래 주소를 정확히 추가하고, OAuth scope는 세 개만 사용합니다.

![compact: ① Redirect URI](assets/1.0.0-rc.1-guide/04-redirect-uri.png)

> [!COPY]
> Redirect URI
> https://127.0.0.1

> [!COPY]
> Required Scopes
> rpc
> identify
> messages.read

Client Secret은 비밀번호처럼 보호하고 screenshot, 채팅 또는 공개 Issue에 올리지 마세요.

<!-- PAGE -->

# Gacha Overlay에 인증정보 입력

## 실행 후 Discord 인증을 완료하세요

1. `Gacha Overlay.exe`를 실행합니다.
2. 언어를 선택합니다.
3. Client ID와 Client Secret을 입력합니다.
4. Redirect URI가 `https://127.0.0.1`인지 확인합니다.
5. Discord 인증 창에서 연결을 허용합니다.

![① Client ID/Secret 입력, ② 다음](assets/1.0.0-rc.1-guide/09-discord-authentication.png)

> [!RESULT]
> Discord 연결 상태가 정상으로 표시되고 서버 확인 단계로 이동하면 인증이 완료된 것입니다.

> [!TROUBLESHOOT]
> 인증 문제가 계속되면 전체 사용자 설명서의 `Discord 인증 문제 해결`을 확인하세요.

<!-- PAGE -->

# 서버 · Main Channel · Sales Compatibility

## HUD에 표시할 채널과 판매 호환 모드를 확인하세요

Server와 Sales Channel은 현재 RC의 고정 Production 대상입니다. Main Channel은 HUD에 표시할 채팅 채널입니다.

![tiny: ① 서버 확인 · ② 다음](assets/1.0.0-rc.1-guide/10-target-server-check.png)

**Settings → Server → Main Channel**

![tiny: ① Main 선택 · ② 저장](assets/1.0.0-rc.1-guide/11-main-channel-selection.png)

판매 대기열을 사용할 경우 Discord Desktop의 판매 채널 호환 상태를 확인하고 안내된 선택지를 적용합니다.

![tiny: ① 판매 호환 · ② 다음](assets/1.0.0-rc.1-guide/12-sales-compatibility.png)

- **SERVER · 고정**: Gacha Overlay가 연결할 Production Guild
- **MAIN · 선택**: HUD에 채팅을 표시할 채널 한 개
- **SALES · 고정**: 판매 대기열에 사용하는 Production Sales Channel

Main 연결과 Sales 상태가 정상으로 표시되면 초기 설정이 끝났습니다.

<!-- PAGE -->

# HUD 기본 사용

## F9와 F10 두 키를 먼저 기억하세요

<!-- HOTKEYS -->

<!-- LOCK_COMPARE -->

![HUD 준비 완료 화면](assets/1.0.0-rc.1/13-hud-guide.png)

잠금을 해제한 직후 마우스가 게임에 남아 있다면 HUD Shell을 한 번 클릭한 뒤 이동하거나 크기를 조절하세요.

> [!RESULT]
> UNLOCKED에서는 HUD 이동·크기 조절·Settings·Media 확대가 가능하고, LOCKED에서는 마우스 입력이 게임으로 통과합니다.

<!-- PAGE -->

# 판매 대기열

## Current와 Waiting 순서를 확인하세요

`Current`는 현재 판매 차례, `Waiting`은 다음 판매자들입니다. `Queue Detail`을 펼치면 전체 순서를 확인할 수 있습니다.

![compact: 실제 HUD의 Current 판매 행](assets/1.0.0-rc.1/17-sales-queue.png)

판매 게시물에 `SOLD` 또는 `:closed:` 반응이 확인되면 판매 완료로 처리되어 Active Queue에서 제거됩니다.

![compact: 판매 추적 설정과 상태](assets/1.0.0-rc.1-guide/17-sales-settings.png)

> [!TROUBLESHOOT]
> 판매 상태가 계속 `Paused`라면 전체 사용자 설명서의 `판매 상태 문제 해결`을 확인하세요.

<!-- PAGE -->

# 문제가 생겼을 때

## 증상부터 짧게 확인하세요

- **HUD가 안 보여요**: F9를 누르거나 Windows 알림 영역의 Gacha Overlay 트레이 아이콘을 우클릭해 `HUD 표시`를 선택합니다.
- **Discord 연결이 안 돼요**: Discord Desktop 계정과 Client ID/Secret을 확인한 뒤 다시 연결합니다.
- **Sales가 Paused예요**: Discord 판매 채널을 화면에 열고 접근성 상태를 확인합니다.
- **판매 완료 반응이 반영되지 않아요**: 판매 게시물과 `SOLD` 또는 `:closed:` 반응이 보이는지 확인합니다.
- **진단 ZIP이 필요해요**: 아래 경로에서 직접 저장합니다.

**Settings → 진단 및 복구 → 진단 파일 만들기**

![① 진단 파일 만들기, ② 저장 위치 열기](assets/1.0.0-rc.1-guide/18-diagnostics-zip.png)

> [!IMPORTANT]
> 문제가 계속되면 `Gacha Overlay 사용자 설명서.pdf`의 해당 문제 해결 절차를 확인하세요.

<!-- PAGE -->

# 보안과 전체 설명서 안내

## 마지막으로 이것만 기억하세요

- Client Secret을 다른 사람과 공유하지 않습니다.
- Discord User Token, Self-bot 또는 Bot Token을 사용하지 않습니다.
- Gacha Overlay는 Discord Bot을 서버에 추가하지 않습니다.
- 개인 OAuth credential은 Windows 현재 사용자 DPAPI로 보호합니다.
- Discord 인증 이후의 주 통신은 Discord Desktop과의 Local RPC/Named Pipe입니다.
- 판매 완료 관찰용 UI Automation은 읽기 전용이며 자동 클릭·스크롤·키보드 입력을 하지 않습니다.
- Diagnostic ZIP은 자동 업로드되지 않습니다. 공유 전 내용을 직접 확인하세요.

> [!SECURITY]
> 실제 Client Secret, Access Token, Refresh Token 또는 인증 코드를 screenshot이나 진단 문의에 포함하지 마세요.

## 더 자세한 설명이 필요하다면

같은 폴더의 `Gacha Overlay 사용자 설명서.pdf`에는 전체 Settings, Theme/Typography, Media, Product Mapping, Diagnostics, Security, Known Limitations와 상세 Troubleshooting이 포함되어 있습니다.

> [!RESULT]
> 여기까지 완료했다면 Gacha Overlay의 기본 HUD와 판매 대기열을 사용할 준비가 끝났습니다.
