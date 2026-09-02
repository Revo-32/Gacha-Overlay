# Gacha Overlay

## 빠른 시작 가이드

- Version: 1.0.0-rc.1 code line / M9.11 Remote-only architecture
- Document: Quick Start 1.1
- Updated: 2026-09-03

<!-- BODY -->

# 시작하기 전에

## Remote 페어링만 사용합니다

준비물은 Windows x64 PC, 대상 서버의 Discord 계정, 운영자에게 받은 Remote Backend 주소, Release ZIP입니다. Discord Desktop은 필수가 아니며, 개인 Discord Application이나 Client Secret도 만들지 않습니다.

<!-- FLOW: ZIP 압축 해제 | Remote 주소 적용 | 설치본 페어링 | 메인 채널 선택 | HUD 사용 -->

> [!IMPORTANT]
> WPF 사용자는 Bot Token을 입력하지 않습니다. 신뢰하는 운영자가 제공한 Remote 주소만 사용하세요.

<!-- PAGE -->

# 1 · 앱 실행과 Remote 주소

ZIP을 새 폴더에 완전히 풀고 `GachaOverlay.App.exe`를 실행합니다. 언어를 선택한 뒤 Remote 연결 화면에서 운영자가 제공한 HTTPS 주소를 입력하고 `주소 적용`을 누릅니다.

이 PC에서 개발용 Backend를 함께 실행할 때만 `http://127.0.0.1` 같은 loopback 주소를 사용할 수 있습니다.

> [!TIP]
> HUD가 안 보이면 Windows 오른쪽 아래 알림 영역의 Gacha Overlay 아이콘을 우클릭해 설정을 여세요.

<!-- PAGE -->

# 2 · 설치본 페어링

`페어링 시작`을 누른 뒤 화면에 표시된 `/lsoverlay pair code:...` 명령을 대상 Discord 서버에서 실행합니다. 웹, 모바일, 데스크톱 Discord 중 어느 쪽을 사용해도 됩니다.

완료되면 설치본별 Remote 접근 토큰이 Windows CurrentUser DPAPI로 보호됩니다. 페어링 코드나 보호 파일을 다른 사람에게 공유하지 마세요.

<!-- PAGE -->

# 3 · 메인 채널 선택

허용된 채널 목록에서 HUD에 표시할 메인 채널 하나를 선택하고 `선택 채널 사용`을 누릅니다.

정상 기준:

- Remote `Live`
- 최근 메인 채팅 20개 표시
- Remote Sales `RemotePrimary / Complete`
- 선택한 Host의 GTA 세션 표시

채널 전환에 실패하면 기존 채널과 기존 채팅이 유지됩니다.

<!-- PAGE -->

# 4 · HUD 조작

<!-- HOTKEYS -->

F10으로 잠금을 푼 뒤 HUD Shell을 한 번 클릭하면 이동과 크기 조절을 할 수 있습니다. 잠그면 Queue Detail이 펼쳐진 모습은 유지되지만 Scroll과 버튼 입력은 비활성화되고 마우스가 게임으로 통과합니다.

![실제 HUD와 판매 대기열](assets/1.0.0-rc.1/13-hud.png)

<!-- PAGE -->

# 5 · 판매와 상태 변경

판매는 Remote의 최신 30개 정규 근거를 사용합니다. 본인 판매글에는 `판협중 → 판매중 → 판매 완료 → Bot 상태 지우기` 버튼이 표시될 수 있습니다.

사람이 직접 추가한 반응은 오버레이가 제거하지 않습니다. Remote가 건강하고 본인 AuthorId가 확인된 경우에만 Bot 상태를 변경합니다.

![펼쳐진 실제 판매 Queue Detail](assets/1.0.0-rc.1/17-sales-queue.png)

<!-- PAGE -->

# 6 · Discord Desktop 없이 확인

Remote 페어링 후 Discord Desktop을 완전히 종료해도 Chat, Sales, Session, 판매 상태 변경, 판매 알림이 계속 작동해야 합니다. 새 메시지는 Discord 웹이나 모바일에서 보내 확인할 수 있습니다.

`Reconnecting`에서는 마지막 신뢰 상태를 보존하고 자동 복구합니다. `Access revoked`에서는 채팅·판매 캐시를 비우고 다시 페어링하도록 안내합니다.

<!-- PAGE -->

# 7 · 문제 해결

1. F9와 F10 상태 확인
2. 트레이 아이콘 우클릭으로 설정 열기
3. Discord 설정에서 Remote 재연결/새로고침
4. 미디어 문제면 캐시 비우기
5. 계속 실패하면 `설정 → 진단 및 복구`에서 Diagnostic ZIP 생성

Diagnostic ZIP에는 Bot Token, Remote 접근 토큰, 보호 credential, 원본 Discord 메시지 전체를 넣지 않습니다.

<!-- QUICK_REFERENCE -->
