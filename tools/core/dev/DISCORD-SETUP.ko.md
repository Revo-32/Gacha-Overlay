# Core 실데이터 검증: 사용자 추가 설정 불필요

사용자가 기존 운영 봇의 제한적 읽기 전용 재사용을 승인했다. 별도 Discord 앱·봇·서버 생성, Token 재발급, OAuth callback 변경은 진행하지 않는다. 이전에 안내했던 별도 봇 입력 도구는 사용하지 않는다. 사용자 로그인/설정 파일은 수정하지 않는다.

## 현재 경로

`LSOverlay.CoreReadProbe`는 SSH로 인증된 운영자가 M8에서 일회 실행하는 진단 도구다. 제품 Backend를 시작하거나 HTTP endpoint를 열지 않는다. 사용자 OAuth를 우회하는 개발 서버도 아니다.

- 운영 컨테이너 환경에서 승인된 Bot Token과 공개 앱/서버 ID만 프로세스 메모리로 가져온다. OAuth Client Secret이나 운영 credential registry는 복사하지 않는다.
- 토큰은 별도 일회성 컨테이너 stdin으로만 전달한다. 명령행, 파일, Docker 환경 설정, 로그에 넣지 않는다. 메모리 안의 평문이 필요하다는 한계는 있다.
- 고정된 Discord HTTPS API로 GET만 허용한다. redirect/cookie/proxy, POST/PUT/PATCH/DELETE, 임의 URL, 다른 서버, 전체 회원 조회, 대량 history 조회, 업로드를 거부한다.
- 기존 Full 설정에서 **현재 선택된 채널 ID만 읽어** 명시한다. 해당 서버 채널인지, 봇과 앱 소유자(팀 앱은 팀 소유자)에게 읽기 권한이 있는지 확인한다. 이는 소유자 시점의 operator capture이며 사용자 OAuth 인증 완료를 뜻하지 않는다.
- 최근 메시지 20개를 한 번 읽고 기존 Backend normalizer → Full의 transport-neutral mapper → Core semantic projection을 재사용한다. 새 C++ 파서나 별도 판매 해석을 만들지 않는다.
- 전체 60초, HTTP 최대 40회, 요청 사이 최소 350ms 간격, 응답 최대 2MiB. SDK의 사전 bucket 대기는 존중하지만 실제 HTTP 오류/429는 재시도하지 않는다. Gateway 연결·Presence 변경·Sales write-back·명령 등록/삭제·미디어 다운로드는 없다.
- 결과는 전용 비공개 ignored artifact에만 저장한다. 채팅 본문을 로그/보고서/Git에 넣지 않는다. Sales/Session은 이 캡처에서 미검증이다.

M8 도구/결과 경로: `/srv/apps/lsoverlay-core-read-probe/`. 운영 `/srv/data`, Compose, 네트워크에는 연결하지 않는다. 컨테이너는 non-root/read-only/cap-drop/no-new-privileges, 256MiB/0.5 CPU/64 PID 제한이며 공개 포트가 없다. 종료 시 일회성 컨테이너와 전용 임시 네트워크는 제거한다. 전후 운영 Backend/Status의 identity·image·start time·health를 비교한다.

## 네이티브 화면 확인

가져온 `chat.json`은 기존 로컬 semantic snapshot 입력으로 확인한다. `--chat-capture`는 렌더러 자신의 첫/끝 화면 PNG 및 짧은 idle 수치를 저장하고 종료한다. 다른 앱의 화면이나 로그인 파일을 캡처하지 않는다.

```powershell
& 'E:\CODEX\Worktrees\Gacha_Overlay-core\artifacts\core\m3\native\Release\LSOverlayCore.exe' --no-hotkeys --chat-fixture '<캡처 chat.json 절대경로>' --chat-capture '<새 결과 폴더 절대경로>'
```

이는 **실제 채팅 데이터의 오프라인 렌더링 검증**이다. 실시간 Core 접속·OAuth·재연결·움직이는 미디어·Full/Core A/B 완료로 보고하지 않는다. 이후 그 검증이 필요한 시점에만 기존 인증 계약을 활용하는 최소 사용자 승인 경로를 확인한다. 운영 OAuth/DNS 변경이 필요하면 별도 승인을 받는다.
