# LS Overlay 2.4.1

GTA Online 화면 위에서 Discord 채팅, 판매 순서, 일일·주간 정보와 사업장 타이머를 확인하는 Windows x64 도구입니다.

2.4.1은 주간 보너스·할인·차량 등 누락된 항목과 실제 금주의 도전 선택을 수정하고, 안전한 주간 필드의 한국어 번역 및 한글 Snapshot 전송 크기를 개선한 서버 측 핫픽스입니다. UI와 Remote Protocol 스키마는 유지됩니다.

Google Gemini를 이용한 AI 번역은 지정 채널·작성자가 모두 확인된 공개 GTA 온라인 이벤트 소스에만 적용됩니다. 일반 Discord 채팅·판매 메시지·사용자 정보·인증정보는 전송하지 않습니다. URL·멘션 포함 필드는 AI에서 제외하고 기존 번역 또는 원문을 표시합니다.

## 시작하기

1. ZIP을 원하는 새 폴더에 모두 압축 해제합니다.
2. 이전 앱이 실행 중이면 Windows 우하단 트레이 아이콘을 우클릭해 종료합니다.
3. **LSOverlay.exe**를 실행합니다. 별도 .NET 설치는 필요하지 않습니다.
4. Discord로 로그인하고 시스템 브라우저에서 승인합니다.
5. 허용된 메인 채널을 고르고 선택 채널 사용을 누릅니다.

기본 **F9**는 전체 HUD 표시/숨김, **F10**은 잠금/해제입니다. 잠기면 클릭이 게임으로 통과합니다. HUD가 보이지 않으면 트레이 아이콘의 설정 메뉴를 이용하세요.

## 함께 읽을 문서

- [빠른 시작 가이드](LS-Overlay-2.4-Quick-Start-ko.pdf): 압축 해제, 인증, F9/F10, 첫 사용
- [상세 사용자 설명서](LS-Overlay-2.4-User-Guide-ko.pdf): 채팅, 판매, 컴패니언, 사업장 관리자, 단축키와 문제 해결

두 PDF는 승인된 2.4 계열 문서를 그대로 재사용합니다. 문서의 2.4.0 파일명 예시는 이번 릴리즈의 `LS-Overlay-2.4.1-win-x64.zip`과 `LS-Overlay-2.4.1-SHA256.txt`로 읽어 주세요. 조작 방법은 동일합니다.

GTA 컴패니언과 사업장 관리자는 설정에서 직접 켭니다. 일반 12/24/48분 타이머는 사업장 관리자에 있습니다. 생산 타이머는 로컬 GTA 실행 중에만 누적하며 스토리 모드도 포함합니다. Alt+Tab은 종료로 처리하지 않고, 앱을 꺼 둔 시간은 추정하지 않습니다.

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

LS Overlay는 Rockstar Games, Take-Two Interactive, Discord, Google과 제휴하거나 이들의 승인을 받은 제품이 아닌 독립적인 비공식 도구입니다.
