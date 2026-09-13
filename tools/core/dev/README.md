# Core 개발 격리 경로

이 서버는 **M0 인프라 점검 전용**이다. LSOverlay.Backend의 대체 서버나 실제 인증/Chat/Sales/Session 구현이 아니다. `/healthz`와 `/fixture/manifest`만 제공한다. 그 밖의 경로는 404다.

- M8 배치: `/srv/apps/lsoverlay-core-dev/m0/`
- 전용 Compose project: `lsoverlay-core-dev`
- M8 origin: internal Docker network의 fixture 컨테이너 IP, port 8080. IP는 실행 시 조회하며 고정하지 않는다.
- 별도 internal Docker network, 외부 egress 없음, 운영 데이터/환경/인증 mount 없음.
- 운영 5188/5189, 기존 Compose, Cloudflare, 방화벽을 수정하지 않는다.
- 로컬에 이미 있는 검증된 런타임 이미지를 재사용하되 fixture entrypoint만 실행한다. Bot은 실행하지 않는다.
- M2에서는 같은 격리 경계에 별도 canonical fixture와 인증·capability 구현을 넣고 검증한다. 임의의 실 Bot/운영 credential 재사용 금지.
- M4 캐시는 `/srv/cache/lsoverlay-core-dev/media/`를 사용하도록 설계하되, 현재 이 경로를 만들거나 mount하지 않는다. 운영 `/srv/data`에 넣지 않는다.

Windows에서 아래 스크립트를 실행한 터미널을 유지한다. 로컬 15188이 이미 사용 중이면 스크립트의 `-LocalPort`로 다른 빈 로컬 포트를 명시한다. 이 환경의 Docker internal network에서는 요청한 host port mapping이 실제로 생성되지 않았으므로, published port를 제거하고 호스트가 접근 가능한 컨테이너 IP를 매번 조회한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\CODEX\Worktrees\Gacha_Overlay-core\tools\core\dev\connect-fixture.ps1"
```

다른 터미널에서 `http://127.0.0.1:15188/healthz`를 확인한다. 이 경로는 SSH 암호화 안의 loopback HTTP다. `http://192.168.0.10:5188` 같은 운영 origin 노출 우회는 사용하지 않는다. 보안 검증을 통과하지 않은 직접 LAN HTTP를 제품에 허용하지 않는다.

현재 fixture에는 비밀값 자체가 없고 합성 manifest만 있으므로 제품 OAuth endpoint를 흉내 내는 인증 우회를 제공하지 않는다. 실제 인증·WebSocket 통합 PASS로 집계하지 않는다.
