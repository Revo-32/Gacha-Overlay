# Gacha Overlay

Gacha Overlay는 Discord Desktop의 채팅과 판매 대기열을 게임 화면 위 Windows HUD로 보여주는 .NET 8 WPF 애플리케이션입니다. 메인 채팅, 이미지·스티커 fallback, 판매 순서, HUD 잠금/클릭 통과, 테마와 진단 기능을 한 프로그램에서 제공합니다.

> 현재 배포 대상은 `1.0.0-rc.1` Release Candidate입니다. 자동 업데이트와 코드 서명은 아직 제공하지 않습니다.

![Gacha Overlay HUD](docs/manual/assets/1.0.0-rc.1/13-hud.png)

## Download

일반 사용자는 [GitHub Releases](../../releases)의 Assets에서 다음 파일을 다운로드하세요.

`Gacha-Overlay-1.0.0-rc.1-win-x64.zip`

GitHub가 자동 생성하는 **Source code (zip)** 및 **Source code (tar.gz)** 는 실행 가능한 Windows 배포본이 아닙니다. Release ZIP을 새 폴더에 완전히 압축 해제한 뒤 실행하세요.

배포 ZIP의 루트는 다음 네 항목으로만 구성됩니다.

- `Gacha Overlay.exe`
- `Gacha Overlay 빠른 시작.pdf`
- `Gacha Overlay 사용자 설명서.pdf`
- `Licenses/`

## Why Gacha Overlay?

Discord 채팅을 확인하려고 게임과 Discord 창을 반복해서 전환하거나, 판매 순서를 별도로 기억하는 불편을 줄이는 것이 목적입니다. HUD는 F9로 표시를 전환하고 F10으로 잠금 상태를 바꿀 수 있습니다. 잠긴 상태에서는 마우스 입력이 게임으로 통과하며, 잠금을 풀면 HUD 이동·크기 조절과 Queue Detail 조작을 다시 사용할 수 있습니다.

## Discord Legacy Overlay와 비교

아래 표는 우열이나 직접 성능 비교가 아니라, 이 프로젝트가 선택한 범위와 공개적으로 확인 가능한 사용 방식의 차이를 정리한 것입니다. Discord 자체 오버레이의 내부 구현은 버전과 환경에 따라 달라질 수 있으며, 이 프로젝트는 Discord Legacy Overlay와 일대일 벤치마크를 수행하지 않았습니다.

| 항목 | Gacha Overlay | Discord 자체/Legacy 오버레이 |
|---|---|---|
| 실행 위치 | 사용자 PC의 독립된 .NET 8 WPF HUD | Discord Client가 제공하는 오버레이 |
| 표시 동작 | Always-on-top HUD와 locked click-through | Discord가 지원하는 게임 내 UI |
| 연결 방식 | Discord Desktop Local RPC/IPC, 판매 관찰용 읽기 전용 UI Automation | Discord가 관리하는 기능 |
| 게임 프로세스 접근 | 게임 DLL 주입, 게임 메모리·그래픽 API hook, 패킷 가로채기를 사용하지 않음 | 내부 방식은 이 저장소의 검증 범위가 아님 |
| 상태 관리 | 표시 개수와 media failure cache를 제한하는 bounded 구조 | 내부 방식은 이 저장소의 검증 범위가 아님 |
| 운영상 trade-off | 개인 PC 리소스와 Discord Desktop이 필요함 | 지원 게임·표시 동작이 Discord 환경에 좌우됨 |

## Bot 기반 도구와 비교

Gacha Overlay는 Discord Bot 계정, Bot invite, Bot Token, Bot role/permission을 요구하지 않고 서버에 Bot을 추가하지 않습니다. 따라서 이 프로그램에는 Bot Token 탈취와 Bot permission 범위라는 유형의 공격 표면이 없습니다.

다만 **No Bot은 No Credential을 뜻하지 않습니다.** 사용자는 개인 Discord Application의 Client ID, Client Secret과 OAuth credential을 사용하며, 민감한 credential은 Windows 현재 사용자 범위의 DPAPI로 보호됩니다.

Bot 기반 도구는 구현에 따라 server-wide event, moderation, command, automation과 더 풍부한 server-side integration을 제공할 수 있습니다. Gacha Overlay는 local HUD, Main Chat, Sales Queue와 작은 server footprint에 집중합니다. Bot 운영 없이 로컬 HUD가 필요한 경우에 적합하고, 서버 전체 자동화가 필요하다면 Bot 기반 도구가 더 적합할 수 있습니다.

## Features

- 선택한 Discord 메인 채널의 최근 채팅을 HUD로 표시
- 이미지, 커스텀 이모지, 스티커 및 전달 메시지의 안전한 fallback
- 판매 채널 Queue Detail, 현재/대기 순서, SOLD/closed 완료 처리
- F9 HUD 표시/숨김, F10 잠금/해제 및 locked click-through
- HUD 이동·크기 조절·불투명도와 multi-monitor 위치 복구
- GitHub Dark, One Dark Pro, Nord, Tokyo Night, Monokai 테마
- Clean, Modern, High Readability, GTA Legacy 타이포그래피
- English, 한국어, 日本語 UI
- Diagnostic ZIP 생성과 연결·판매 상태 진단

## Quick Start

1. Release ZIP을 새 폴더에 완전히 압축 해제합니다.
2. 포함된 `Gacha Overlay 빠른 시작.pdf`를 엽니다.
3. `Gacha Overlay.exe`를 실행합니다.
4. 개인 Discord Application의 Client ID와 Client Secret을 입력하고 Discord 인증을 완료합니다.
5. 대상 서버와 Main Channel을 확인합니다.
6. 판매 대기열을 사용할 경우 안내에 따라 판매 채널 접근 상태를 준비합니다.

HUD가 보이지 않으면 Windows 알림 영역의 숨겨진 아이콘에서 Gacha Overlay를 찾아 우클릭하고 `HUD 표시`를 선택하세요.

## User Manuals

- [Gacha Overlay 빠른 시작](<output/pdf/Gacha Overlay 빠른 시작.pdf>): 설치와 최초 연결에 필요한 경로만 정리한 10페이지 가이드
- [Gacha Overlay 사용자 설명서](<docs/manual/output/Gacha Overlay 사용자 설명서.pdf>): OAuth, HUD, 판매 대기열, 설정, 보안과 문제 해결을 포함한 40페이지 전체 설명서

App Tester 등록은 모든 사용자의 기본 절차가 아닙니다. Client ID, Secret, Redirect URI와 로그인 계정이 올바른데도 Discord 권한 오류가 계속될 때만 전체 설명서의 조건부 문제 해결 절차를 확인하세요.

## Discord OAuth 설정

Gacha Overlay는 공유 Client Secret을 소스나 바이너리에 포함하지 않습니다. 각 사용자는 자신의 Discord Developer Application을 만들고 다음 값을 준비해야 합니다.

- Client ID(App ID)
- Client Secret
- Redirect URI `https://127.0.0.1`
- OAuth scopes `rpc`, `identify`, `messages.read`

실제 User Token, Self-bot 또는 Bot Token을 입력하거나 브라우저 개발자 도구에서 token을 추출하면 안 됩니다.

## Security & Privacy

- Client Secret과 OAuth credential은 Windows 현재 사용자 범위의 DPAPI로 보호합니다.
- 인증 이후 실시간 Discord 이벤트의 주 경로는 PC 내부 Discord Desktop Local RPC/IPC Named Pipe입니다.
- 최초 인증, 필요한 token 교환·갱신, 미디어 다운로드에는 Discord 네트워크 통신이 사용될 수 있습니다.
- 판매 채널 관찰용 UI Automation은 읽기 전용이며 자동 클릭·스크롤·키보드 입력을 하지 않습니다.
- 게임 DLL 주입, 게임 메모리 읽기, 그래픽 API hook, Chromium/Electron hook, 패킷 가로채기를 사용하지 않습니다.
- 설정, 로그, cache와 보호된 credential은 `%LOCALAPPDATA%\GachaOverlay`에 저장됩니다.
- Diagnostic ZIP은 자동 업로드되지 않습니다. 공유 전 사용자가 내용을 직접 확인해야 합니다.

Client Secret, access/refresh token, 인증 코드나 진단 파일은 공개 Issue, 채팅 또는 스크린샷에 포함하지 마세요. 로컬 중심 설계는 외부 노출 지점을 줄이지만, credential 관리와 운영체제 계정 보안은 여전히 중요합니다.

## Performance

다음 값은 Release 빌드, Main Connected, HUD visible, Sales Tracking OFF 상태에서 30.35초간 측정한 한 환경의 기준입니다. PC, Discord 상태, 채널 활동량과 설정에 따라 달라지며 다른 도구와의 직접 비교 결과가 아닙니다.

- CPU 평균: 0.103%
- Working Set 평균: 223.81 MiB, 범위 221.70–233.17 MiB
- Private Bytes 평균: 147.06 MiB
- Handles: 798–865
- Threads: 15–27

별도 합성 재생에서는 채팅 5,000건, 서로 다른 실패 미디어 key 5,000개, F9/F10 100쌍을 총 193.30ms에 처리했습니다. 상세 조건과 bounded-cache 결과는 [M8.2 Performance Report](docs/performance/M8.2-performance-report.md)와 [Soak Report](docs/performance/M8.2-soak-report.md)를 확인하세요.

## Build from Source

필요 환경:

- Windows x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WPF를 지원하는 Windows 개발 환경

저장소 루트에서 실행:

```powershell
dotnet restore GachaOverlay.sln
dotnet build GachaOverlay.sln
dotnet build GachaOverlay.sln -c Release
dotnet test GachaOverlay.sln
dotnet test GachaOverlay.sln -c Release
dotnet format GachaOverlay.sln --verify-no-changes
```

Release 단일 파일 publish:

```powershell
dotnet publish src/GachaOverlay.App/GachaOverlay.App.csproj `
  -c Release `
  -p:PublishProfile=src/GachaOverlay.App/Properties/PublishProfiles/win-x64-singlefile.pubxml
```

## 프로젝트 구조

- `src/GachaOverlay.Core`: 상태, 설정, HUD·판매 도메인 모델
- `src/GachaOverlay.Infrastructure`: Discord Local RPC, OAuth, 저장소, 진단 및 로깅
- `src/GachaOverlay.App`: WPF UI, tray, HUD와 application lifecycle
- `tests/GachaOverlay.Tests`: 자동 회귀 테스트
- `docs`: architecture, 성능 보고서, 사용자 설명서와 release 문서
- `tools`: 검증, 설명서 및 release 도구

## Known Limitations

- Windows x64와 Discord Desktop을 대상으로 합니다.
- exclusive fullscreen에서는 일반 desktop HUD가 게임 위에 표시되지 않을 수 있습니다. Borderless Windowed 또는 Windowed Fullscreen을 사용하세요.
- 현재 Production Guild와 판매 채널은 고정되어 있고 Main Channel 하나를 선택합니다.
- 판매 반응 추적은 Discord 채널 UI 접근성에 의존합니다.
- Discord Local RPC가 일부 스티커나 전달 원본 정보를 제공하지 않으면 `[메시지]` 등으로 일반화될 수 있습니다.
- 자동 업데이트와 실행 파일 코드 서명은 제공하지 않습니다.
- Long-run Functional Soak는 통과했지만 종료 시점의 정량 snapshot은 수집되지 않았습니다.

## Contributing

Issue와 Pull Request를 환영합니다. 빌드·테스트 기준과 credential 금지 정책은 [CONTRIBUTING.md](CONTRIBUTING.md)를 확인하세요.

보안 취약점이나 credential 노출은 공개 Issue에 실제 값을 올리지 말고 [SECURITY.md](SECURITY.md)의 비공개 제보 절차를 이용하세요.

## License

Gacha Overlay 자체 소스 코드는 [MIT License](LICENSE)로 제공됩니다.

번들된 .NET Runtime, 글꼴 및 테마 참고 자료는 각각의 라이선스를 따릅니다. Release ZIP의 `Licenses` 폴더와 저장소의 third-party notice 파일을 확인하세요. 프로젝트의 MIT License는 third-party 구성요소의 별도 조건을 대체하지 않습니다.
