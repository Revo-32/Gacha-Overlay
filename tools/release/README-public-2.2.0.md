# LS Overlay 2.2.0

GTA Online 화면 위에서 Discord 채팅, 판매 순서, 일일·주간 정보와 사업장 타이머를 확인하는 Windows x64 도구입니다.

2.2.0은 클라이언트 메모리 사용량과 렌더링 효율, 움직이는 미디어의 복구와 리소스 정리를 개선한 릴리즈입니다.

## 시작하기

1. ZIP을 원하는 새 폴더에 모두 압축 해제합니다.
2. 이전 앱이 실행 중이면 Windows 우하단 트레이 아이콘을 우클릭해 종료합니다.
3. **LSOverlay.exe**를 실행합니다. 별도 .NET 설치는 필요하지 않습니다.
4. Discord로 로그인하고 시스템 브라우저에서 승인합니다.
5. 허용된 메인 채널을 고르고 선택 채널 사용을 누릅니다.

기본 **F9**는 전체 HUD 표시/숨김, **F10**은 잠금/해제입니다. 잠기면 클릭이 게임으로 통과합니다. HUD가 보이지 않으면 트레이 아이콘의 설정 메뉴를 이용하세요.

## 함께 읽을 문서

- [빠른 시작 가이드](LS-Overlay-2.2-Quick-Start-ko.pdf): 압축 해제, 인증, F9/F10, 첫 사용
- [상세 사용자 설명서](LS-Overlay-2.2-User-Guide-ko.pdf): 채팅, 판매, 컴패니언, 사업장 관리자, 단축키와 문제 해결

GTA 컴패니언과 사업장 관리자는 설정에서 직접 켭니다. 일반 12/24/48분 타이머는 사업장 관리자에 있습니다. 생산 타이머는 확인된 GTA Online 플레이 시간만 누적하며 앱을 꺼 둔 시간은 추정하지 않습니다.

## 업데이트와 보안

기존 `%LOCALAPPDATA%\GachaOverlay` 폴더는 삭제하지 마세요. 호환되는 설정과 로그인 정보를 이어서 사용합니다.

배포 파일은 [공식 GitHub Releases](https://github.com/Revo-32/Gacha-Overlay/releases)에서 받고 제공된 SHA-256과 비교하세요. 코드 서명과 자동 업데이트는 제공하지 않습니다. Windows 보안 기능을 전역으로 끄지 마세요.

일반 사용자는 Discord 사용자 토큰, Bot Token이나 Client Secret을 입력할 필요가 없습니다. Discord Desktop도 필수가 아닙니다. 연결 자격 정보는 현재 Windows 사용자 기준으로 보호됩니다. 진단 ZIP은 자동 업로드되지 않습니다.

## 지원과 라이선스

- [서비스 상태](https://status.revo32.cloud)
- [개인정보처리방침](https://overlay.revo32.cloud/privacy)
- [이용약관](https://overlay.revo32.cloud/terms)
- [문의](mailto:revo.32.39.41@gmail.com)
- [프로젝트 소스](https://github.com/Revo-32/Gacha-Overlay)
- [MIT 라이선스](LICENSE), 타사 고지는 `Licenses` 폴더

LS Overlay는 Rockstar Games, Take-Two Interactive, Discord와 제휴하거나 이들의 승인을 받은 제품이 아닌 독립적인 비공식 도구입니다.
