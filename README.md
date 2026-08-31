# Gacha Overlay

Gacha Overlay는 Discord Desktop의 Local RPC/IPC 정보를 Windows 게임 위 HUD로 표시하는 .NET 8 WPF 애플리케이션입니다. 메인 채팅, 이미지·스티커 fallback, 판매 대기열, HUD 잠금/클릭 통과, 테마와 진단 기능을 제공합니다.

> 현재 버전은 `1.0.0-rc.1` Release Candidate입니다. 자동 업데이트와 코드 서명은 아직 제공하지 않습니다.

![Gacha Overlay HUD](docs/manual/assets/1.0.0-rc.1/13-hud.png)

## Download

일반 사용자는 [GitHub Releases](../../releases)의 Assets에서 다음 파일을 다운로드하세요.

`Gacha-Overlay-1.0.0-rc.1-win-x64.zip`

GitHub가 자동 생성하는 **Source code (zip)** 및 **Source code (tar.gz)** 는 실행 가능한 프로그램 배포본이 아닙니다. 프로그램을 사용하려면 반드시 위 Windows x64 ZIP을 받으세요.

## 주요 기능

- 선택한 Discord 메인 채널의 최근 채팅을 HUD로 표시
- 이미지, 커스텀 이모지, 스티커 및 전달 메시지의 안전한 fallback
- 판매 채널 Queue Detail, 대기 순서, SOLD/closed 완료 처리
- F9 HUD 표시/숨김, F10 잠금/해제 및 locked click-through
- 이동·크기 조절·불투명도와 multi-monitor 위치 복구
- GitHub Dark, One Dark Pro, Nord, Tokyo Night, Monokai 테마
- Clean, Modern, High Readability, GTA Legacy 타이포그래피
- English, 한국어, 日本語 UI
- Diagnostic ZIP 생성과 연결·판매 상태 진단

## Quick Start

1. Release ZIP을 새 폴더에 완전히 압축 해제합니다.
2. 포함된 `Gacha Overlay 사용자 설명서.pdf`를 먼저 확인합니다.
3. `Gacha Overlay.exe`를 실행합니다.
4. 첫 실행 온보딩에서 언어와 개인 Discord Application credential을 입력합니다.
5. Discord 인증 후 대상 서버와 메인 채널을 확인합니다.
6. 판매 대기열을 사용할 경우 안내에 따라 Discord 접근성 모드를 준비합니다.

HUD가 보이지 않으면 Windows 알림 영역의 숨겨진 아이콘에서 Gacha Overlay를 찾아 우클릭하고 `HUD 표시`를 선택하세요.

## User Manual

OAuth2 설정, Client ID/Secret 준비, 온보딩, HUD, 판매 대기열과 문제 해결은 [Gacha Overlay 사용자 설명서](<docs/manual/output/Gacha Overlay 사용자 설명서.pdf>)에 단계별로 정리되어 있습니다.

## Discord OAuth 설정

Gacha Overlay는 공유 Client Secret을 소스나 바이너리에 포함하지 않습니다. 각 사용자는 자신의 Discord Developer Application을 만들고 다음 값을 준비해야 합니다.

- Client ID(App ID)
- Client Secret
- Redirect URI `https://127.0.0.1`
- OAuth scopes `rpc`, `identify`, `messages.read`

실제 User Token 또는 Bot Token을 입력하거나 브라우저 개발자 도구에서 token을 추출하면 안 됩니다. 자세한 설정 위치와 현재 Developer Portal 화면은 사용자 설명서를 확인하세요.

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

개인 Discord credential은 저장소에 포함되지 않습니다. 처음 실행한 사용자가 자신의 값을 입력해야 합니다. Runtime 설정, 로그, cache 및 보호된 credential은 프로그램 폴더가 아니라 `%LOCALAPPDATA%\GachaOverlay`에 저장됩니다.

## 프로젝트 구조

- `src/GachaOverlay.Core`: 상태, 설정, HUD·판매 도메인 모델
- `src/GachaOverlay.Infrastructure`: Discord Local RPC, OAuth, 저장소, 진단 및 로깅
- `src/GachaOverlay.App`: WPF UI, tray, HUD와 application lifecycle
- `tests/GachaOverlay.Tests`: 자동 회귀 테스트
- `docs`: architecture, 사용자 설명서, release 문서
- `tools`: 검증, 설명서 및 release 도구

## Known Limitations

- Windows x64와 Discord Desktop을 대상으로 합니다.
- exclusive fullscreen에서는 일반 desktop overlay가 게임 위에 표시되지 않을 수 있습니다. Borderless Windowed 또는 Windowed Fullscreen을 사용하세요.
- 현재 Production Guild와 판매 채널은 고정되어 있고 메인 채널 하나를 선택합니다.
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
