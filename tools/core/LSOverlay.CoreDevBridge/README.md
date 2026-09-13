# Core 실제 데이터 개발 연결

이 도구는 **운영 Backend의 교체본/복제 배포가 아니다.** M8의 독립 개발 컨테이너에서 기존 HTTPS Remote에 일반 사용자 권한으로 접속하고, 기존 C# domain/projection을 통해 네이티브 Core DTO를 만든다.

## 격리

- 고정 upstream `https://overlay.revo32.cloud`. HTTP 허용 목록은 bootstrap, 허용 Chat channel 목록, Chat/Sales bootstrap뿐이다. POST bootstrap은 읽기 요청이다. 판매 상태 변경·Discord REST·Gateway 호출, 임의 URL proxy는 없다.
- 실시간 이벤트는 기존 `LSOverlayRemoteClient.StreamIndependentAsync`를 사용한다. 초기 Sales와 이후 Chat/Sales resync는 각각 bounded worker로 처리해 판매 HTTP가 채팅 전달을 막지 않는다. 정기 history polling은 없다.
- 새 Discord 앱/봇/OAuth callback/DNS가 필요하지 않다. native client가 기존 issuer에 직접 로그인하고 **Core 전용 DPAPI** 저장소를 사용한다. Full의 로그인 파일/설정은 읽거나 수정하지 않는다.
- M8 host `127.0.0.1:15190`, 전용 Docker network, RO root, uid1000, 256MiB/0.5CPU/64PID, 임시 /tmp만 쓰기 가능. Production 이미지의 .NET runtime만 재사용하며 별도 entrypoint/publish를 지정한다. Production env/data/socket은 mount/import하지 않는다.
- 로그 provider를 제거했다. token은 연결의 RAM에만 존재한다. 요청 query/Origin/다중 Authorization을 거부하고 연결 1개, 세션 30분, control frame 4KiB로 제한한다. client 연결은 명시적 SSH loopback에만 허용한다.
- 기존 개발 채팅 채널 ID를 고정 설정한다. 서버가 사용자에게 허용한 channel catalog에 없으면 연결을 거부한다. 계정별 state 분리, generation/sequence/완전한 판매 evidence 검증, 권한 철회 시 해당 표시 제거를 수행한다.

## 상태와 검증

합성 집중 검사 60 assertions PASS (외부 호출/Discord 쓰기 0), M8 Linux network-none 검사 통과. native Release/CTest 5/5 PASS. 실제 사용자 승인 이후 Chat/Sales/Session bootstrap/stream과 개발 bridge 재연결을 확인했다. 상세 범위는 `native/LSOverlayCore/M6.md`를 참조한다.

현재는 **인증된 Chat/Sales/Session 읽기 전용 + 네이티브 미디어 검증용**이다. opaque reference를 현재 사용자/메시지 권한으로 확인한 뒤 별도의 private media worker에서 변환·전송한다. native client는 원본 URL을 fetch하지 않는다. 미디어 worker는 공개 포트/운영 데이터 없이 개발 전용 키와 폐기 가능한 별도 캐시만 사용한다. 실제 미디어 전송과 재생은 확인했지만, 확대 UX, 전체 설정·상호작용 동등성, 실제 대형 GIF Full/Core 비교, M6 workload matrix는 아직 완료가 아니다. 이것을 M4 품질/성능 최종 통과나 M7 RC로 해석하지 않는다.

`--core-dev-observe`는 명시적 개발 계측 모드다. 5초마다 count/메모리/누적 CPU/연결 상태만 ignored artifact에 최대 2160줄 기록한다. 메시지 내용·사용자 ID·미디어 URL·token은 기록하지 않는다. 계측 오버헤드가 있는 수치임을 구분한다.

## 실행

2026-09-14 조작 검증: `launch-sales-core.ps1`은 `m7-sales-ux` 네이티브 실행본의 본인 판매 완료·완료 취소 UI를 활성화한다. 사용자 요청에 따라 클릭 즉시 기존 issuer의 `/api/v1/sales/status`로 직접 전송되며 별도 확인창 없이 실제 Discord 상태를 변경한다. Bridge는 조작 가능한 본인 글의 의미 힌트만 제공하고 여전히 쓰기 HTTP 경로를 거부한다. 옵션 없는 기존 읽기 전용 launcher는 계속 읽기 전용이다. 실사용 조작 검증 전에는 정식 배포 완료로 간주하지 않는다.

`tools/core/dev/launch-readonly-core.ps1`은 SSH tunnel과 자체 native client만 실행한다. 초기 1회 브라우저 승인 필요. 종료할 때 직접 만든 SSH child만 닫고 기존 tunnel/Full 앱은 보존한다. 다른 창의 F9/F10을 빼앗지 않도록 `--no-hotkeys`를 사용한다. Core 트레이에서 잠금/표시/종료할 수 있다.

개발 연결을 열기 위해 Production의 OAuth 구성을 바꾸거나, 봇 Secret/Token을 사용자에게 받아서는 안 된다. 자동 실행이 도구 정책에 막히면 우회하지 않고 사용자가 이 스크립트를 직접 실행한다.
