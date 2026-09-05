# LS Overlay 2.2

## 빠른 시작 가이드

GTA Online 화면 위에서 Discord 채팅, 판매 순서와 2.2의 보조 HUD를 빠르게 시작하는 안내서입니다.

**대상:** Windows x64 · LS Overlay 2.2.0

LS Overlay는 Rockstar Games, Take-Two Interactive, Discord와 제휴하거나 이들의 승인을 받은 제품이 아닌 독립적인 비공식 도구입니다.

<!-- PAGEBREAK -->

# 1. 다운로드와 압축 해제

1. 정식 배포 후 [GitHub Releases](https://github.com/Revo-32/Gacha-Overlay/releases)에서 `LS-Overlay-2.2.0-win-x64.zip`을 받습니다.
2. GitHub가 자동 생성한 `Source code`가 아니라 이름이 정확히 일치하는 Windows x64 ZIP인지 확인합니다.
3. 다운로드한 ZIP의 SHA-256을 릴리즈 페이지의 `LS-Overlay-2.2.0-SHA256.txt`와 비교합니다.
4. 문서, `Licenses` 폴더와 `LSOverlay.exe`가 함께 보이도록 원하는 새 폴더에 **모두 압축 해제**합니다.
5. `LSOverlay.exe`를 실행합니다. 별도 설치 프로그램이나 .NET 설치는 필요하지 않습니다.

기존 버전 사용자는 실행 중인 앱을 트레이 메뉴의 **종료**로 먼저 끈 뒤 새 파일을 사용하세요. `%LOCALAPPDATA%\GachaOverlay`를 삭제하지 않으면 호환되는 설정과 로그인을 계속 사용합니다.

ZIP에는 실행 파일, 두 PDF와 라이선스 고지가 함께 들어 있습니다.

<!-- SCREENSHOT REQUIRED: 04-three-huds-overview.png | 사용 예: 왼쪽 컴패니언 · 가운데 사업장 관리자 · 오른쪽 Main HUD / 대화는 가림 -->

<!-- PAGEBREAK -->

# 2. Windows 첫 실행 경고

LS Overlay 2.2.0은 코드 서명된 설치 프로그램이 아니므로 Windows SmartScreen 또는 보안 프로그램의 확인 화면이 나타날 수 있습니다.

경고가 보이면 다음 순서를 지킵니다.

1. 파일 이름이 `LSOverlay.exe`인지 확인합니다.
2. 파일을 공식 GitHub Releases의 Stable ZIP에서 받았는지 확인합니다.
3. SHA-256이 배포된 체크섬과 일치하는지 확인합니다.
4. 확인된 파일에 한해서 Windows가 제공하는 세부 정보 흐름으로 실행을 계속합니다.

Windows 보안, 백신 또는 SmartScreen을 전역으로 끄지 마세요. 출처나 체크섬이 다르면 실행하지 말고 파일을 다시 받으세요.

## 체크섬 확인 예

PowerShell에서 다운로드한 ZIP이 있는 폴더로 이동한 뒤 다음 명령을 실행합니다. 표시되는 Hash를 릴리즈의 체크섬과 비교하세요.

```powershell
Get-FileHash .\LS-Overlay-2.2.0-win-x64.zip -Algorithm SHA256
```

체크섬 일치는 파일의 무결성 확인입니다. 배포처 자체가 신뢰할 수 있는지도 함께 확인해야 합니다.

<!-- PAGEBREAK -->

# 3. Discord 연결

LS Overlay에서 **Discord로 로그인**을 누르면 시스템 기본 브라우저가 열립니다.

1. 브라우저에서 Discord 계정을 확인합니다.
2. LS Overlay가 요청하는 현재 OAuth 승인 내용을 확인합니다.
3. 승인 후 완료 화면이 보이면 LS Overlay로 돌아옵니다.
4. 허용된 메인 채널을 고르고 **선택 채널 사용**을 누릅니다.

LS Overlay는 Discord 사용자 토큰, Bot Token 또는 Client Secret을 붙여 넣으라고 요구하지 않습니다. 연결에 필요한 클라이언트 자격 정보는 현재 Windows 사용자 기준 보호 저장소를 사용합니다. Discord Desktop 앱을 켜 둘 필요는 없습니다.

인증이 끝나지 않으면 브라우저 탭을 닫지 말고 네트워크와 [서비스 상태](https://status.revo32.cloud)를 확인하세요.

<!-- SCREENSHOT REQUIRED: 02-discord-connected.png | 인증 완료 후 앱의 Discord 설정 - 브라우저 승인 화면이 아닙니다 -->

<!-- PAGEBREAK -->

# 4. 첫 화면 이해하기

Main HUD에는 선택한 Discord 채널의 최근 채팅과 판매 대기열이 표시됩니다.

- **Chat:** 작성자, 메시지, 언급, Reaction과 지원되는 이미지·스티커를 표시합니다.
- **Sales:** 현재 판매 차례, 대기 인원과 설정한 상품 정보를 표시합니다.
- **상태 영역:** 연결 또는 판매 상태를 짧게 알려 줍니다.

상단의 작은 설정 버튼은 HUD 잠금이 풀렸을 때 사용할 수 있습니다. 판매 상세는 읽기 전용이며, 자신의 판매 행에서는 서버 확인을 거치는 **판매완료** 버튼을 사용할 수 있습니다.

<!-- SCREENSHOT REQUIRED: 01-main-hud.png | 1 세션 인원 · 2 채팅 · 3 판매 요약과 상세 / 타인의 정보는 가림 -->

<!-- PAGEBREAK -->

# 5. F9와 F10

두 기본 단축키만 기억하면 HUD를 안전하게 다룰 수 있습니다.

## F9 - 전체 HUD 표시/숨김

Main HUD, GTA 컴패니언, 사업장 관리자의 공통 표시 상태를 전환합니다. 설정에서 개별적으로 꺼 둔 보조 HUD를 강제로 켜지는 않습니다.

## F10 - HUD 잠금/해제

- **잠금:** HUD가 마우스 입력을 가로채지 않아 게임을 그대로 조작할 수 있습니다.
- **잠금 해제:** HUD 위치 이동, 지원되는 크기 조절, 스크롤과 버튼 사용이 가능합니다.

F9를 눌러도 보조 HUD의 개별 사용 여부와 위치는 보존됩니다. F10 잠금도 세 HUD에 함께 적용됩니다.

<!-- SCREENSHOT REQUIRED: 03-settings-hud-hotkeys.png | F9/F10 입력 칸과 잠금 해제 안내가 보이는 설정 -->

<!-- PAGEBREAK -->

# 6. 2.2 기능 빠르게 켜기

HUD 잠금을 풀고 설정을 연 다음 필요한 기능을 켭니다.

## GTA 컴패니언

설정의 **GTA 컴패니언**에서 기능과 표시할 일일·주간 항목을 켭니다. 오늘의 도전은 매일 15:00 KST, 주간 정보는 매주 목요일 18:00 KST 기준입니다.

## 사업장 관리자

설정의 **사업장 관리자**에서 기능을 켜고 실제로 사용하는 사업장만 선택합니다. 이 기능은 GTA 내부 재고를 직접 읽지 않으므로, 보급·파견·습격을 시작한 시점에 해당 버튼을 누릅니다.

## 움직이는 미디어

설정의 **미디어**에서 `움직이는 미디어 재생`을 켜면 지원되는 GIF, WebP, 스티커와 Custom Emoji를 재생합니다.

<!-- SCREENSHOT REQUIRED: 15-settings-companion.png | GTA 컴패니언 사용과 표시할 정보 - 지정 키와 수치는 사용자 설정 예시 -->

<!-- PAGEBREAK -->

# 7. 문제가 있으면

## HUD가 보이지 않음

F9를 한 번 누르고, Windows 우하단 숨겨진 아이콘의 LS Overlay를 우클릭해 **HUD 표시** 또는 **설정**을 엽니다. GTA 컴패니언·사업장 관리자는 각 기능의 사용 설정도 확인합니다.

## HUD를 클릭할 수 없음

F10을 눌러 잠금을 해제합니다. 잠금 상태는 정상적인 클릭 통과 모드입니다.

## Discord가 연결되지 않음

인터넷 연결과 [서비스 상태](https://status.revo32.cloud)를 확인한 뒤 설정의 Discord 연결 흐름을 다시 시작합니다.

## 단축키가 적용되지 않음

단축키 칸을 누르고 키를 입력한 뒤 **단축키 적용**을 누릅니다. 중복·충돌은 거절됩니다. 입력 중 ESC는 해당 매핑을 해제합니다.

## 도움이 더 필요함

상세 사용자 설명서의 증상별 문제 해결을 확인하고, 필요하면 설정의 **진단 파일 만들기**로 로컬 ZIP을 만든 뒤 내용을 확인해 문의하세요. 진단 파일은 자동 업로드되지 않습니다.

- [개인정보처리방침](https://overlay.revo32.cloud/privacy)
- [이용약관](https://overlay.revo32.cloud/terms)
- [문의](mailto:revo.32.39.41@gmail.com)

<!-- SCREENSHOT REQUIRED: 12-diagnostics-button.png | 설정 > 진단의 진단 파일 만들기 -->
