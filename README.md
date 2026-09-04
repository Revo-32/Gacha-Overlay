# LS Overlay

![LS Overlay](assets/branding/LS_Overlay_logo.png)

GTA Online을 보면서 Discord 채팅과 판매 순서를 함께 확인하는 Windows HUD 도구입니다.

현재 배포 버전은 **2.0.0**입니다. LS Overlay 2.0의 첫 정식 안정 버전입니다.

## 주요 기능

- 선택한 Discord 채널의 최근 채팅, 이미지·이모지·스티커·전달 내용 표시
- Discord 역할 색상·역할 아이콘·Reaction 표시, 같은 작성자의 연속 메시지 묶음
- 판매 순서와 상품 확인, 자신의 판매글을 일반 판매 영역에서 바로 **판매완료**
- 상세 판매 목록은 읽기 전용, 잠금 후에도 펼친 상태 유지
- 선택한 호스트의 GTA Online 세션 인원 표시
- 일반·벙커·LSD GTAO 타이머와 완료 알림음
- HUD 위치·크기·투명도, 조선굴림체를 포함한 글꼴과 다섯 가지 테마
- 브라우저를 통한 Discord 로그인

## 사용 환경

Windows x64, 인터넷 연결, GTA V Enhanced / GTA Online, 지원 Discord 서버에 접근 가능한 계정이 필요합니다.
일반 채팅 확인과 로그인에는 Discord Desktop이 필수는 아닙니다.
독점 전체 화면에서 HUD가 가려지면 테두리 없는 창 모드를 사용하세요.

## 다운로드

[GitHub Releases](https://github.com/Revo-32/Gacha-Overlay/releases)에서 **LS-Overlay-2.0.0-win-x64.zip**을 받으세요.
GitHub가 자동 생성하는 **Source code** 파일은 실행용이 아닙니다.

2.0.0은 T 빠른 전환 기능을 제거한 버전입니다. [2.0.0 릴리즈 안내](docs/releases/LS-Overlay-2.0.0-github-release.md)에서 주요 기능을 확인할 수 있습니다.

ZIP에는 **LSOverlay.exe**, 빠른 시작 README, 한국어 안내서 PDF, LICENSE와 구성요소 라이선스만 포함됩니다.
별도 .NET 설치는 필요 없습니다. 자동 업데이트와 실행 파일 코드 서명은 제공하지 않습니다.
Windows 보안 경고가 나타나면 출처와 배포된 SHA-256을 먼저 확인하세요. 보안 프로그램을 끄지 마세요.

## 빠른 시작

1. ZIP을 새 폴더에 모두 압축 해제합니다.
2. 기존 앱은 트레이 메뉴의 **종료**로 끕니다.
3. **LSOverlay.exe**를 실행하고 언어를 선택합니다.
4. **Discord로 로그인**을 누르고 브라우저에서 승인합니다.
5. 앱으로 돌아와 허용된 채널을 선택하고 안내를 완료합니다.
6. GTA Online을 실행하고 HUD를 확인합니다.

기존 사용자는 같은 Windows 계정에서 저장된 설정과 로그인을 계속 사용할 수 있습니다.
HUD가 보이지 않으면 우하단 숨겨진 아이콘의 LS Overlay를 우클릭해 **HUD 표시** 또는 **설정**을 선택하세요.

[한 장 빠른 시작](docs/user/QUICK-START-ko.md) · [한국어 사용자 안내서 원문](docs/user/LS-Overlay-2.0-RC-User-Guide-ko.md)

## 단축키

| 키 | 동작 |
|---|---|
| F9 (기본) | HUD 표시 / 숨김 |
| F10 (기본) | HUD 잠금 / 해제 |
| 이전 / 다음 채널 | 기본 미지정, 설정에서 지정 가능 |

잠금 중에는 마우스가 게임으로 통과합니다. 이동·스크롤·판매완료 버튼을 쓰려면 잠금을 푸세요.
Discord 창으로 이동하려면 Windows의 Alt+Tab을 사용하세요. T 빠른 전환 기능은 제거되었습니다.

## 개인정보와 서비스 안내

[개인정보처리방침](https://overlay.revo32.cloud/privacy) · [이용약관](https://overlay.revo32.cloud/terms) · [서비스 상태](https://status.revo32.cloud)

진단 파일은 사용자가 직접 만들며 자동 업로드하지 않습니다. 공유 전에 내용을 확인하고 공개 게시판에는 올리지 마세요.
LS Overlay는 독립 도구이며 Rockstar Games·Take-Two·Discord의 공식 제품이나 승인을 의미하지 않습니다.

## 문의

이용 문의·개인정보 관련 문의·데이터 삭제 요청:
[revo.32.39.41@gmail.com](mailto:revo.32.39.41@gmail.com)

## 릴리즈와 개발

[2.0.0 릴리즈 안내](docs/releases/LS-Overlay-2.0.0-github-release.md) · [패키징 안내](tools/release/README.md)

개발 빌드는 Windows와 .NET 8 SDK가 필요합니다.
`dotnet build GachaOverlay.sln`, `dotnet test GachaOverlay.sln`으로 확인할 수 있습니다.
일반 사용자는 소스 빌드나 서버 설정을 할 필요가 없습니다.

## 라이선스

프로젝트 소스는 [MIT](LICENSE)입니다. 포함된 런타임·글꼴·테마의 별도 고지는 배포 ZIP의 **Licenses** 폴더에 있습니다.
