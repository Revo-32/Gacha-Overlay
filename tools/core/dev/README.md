# Core 개발 격리 경로

현재 소스는 **M3 합성 채팅 계약 검증 서버**다. 실제 Discord 서비스가 아니며, 임의의 실 Bot/운영 credential 재사용은 금지한다. `/fixture/manifest`의 `syntheticAuthentication=true`, `liveDiscord=false`로 모드를 명시한다. 합성 인증·Core snapshot/heartbeat/resume·합성 revision 업데이트만 제공한다. 제품 Backend의 API에 인증 우회 경로를 등록하지 않는다.

- M8 M3 배치: `/srv/apps/lsoverlay-core-dev/m3/` (이전 m0/m2 배치는 보존)
- 전용 Compose project: `lsoverlay-core-dev`
- M8 origin: internal Docker network의 fixture 컨테이너 IP, port 8080. IP는 실행 시 조회하며 고정하지 않는다.
- 별도 internal Docker network, 외부 egress 없음, 운영 데이터/환경/인증 mount 없음.
- 운영 5188/5189, 기존 Compose, Cloudflare, 방화벽을 수정하지 않는다.
- 로컬에 이미 있는 검증된 런타임 이미지를 재사용하되 fixture entrypoint만 실행한다. Bot은 실행하지 않는다.
- M2 인증·capability 계약 위에 기존 Backend projection으로 만든 합성 한국어 채팅을 전달한다. 인증 claim은 합성 서버 안에서만 승인되며 실제 OAuth 검증 결과로 취급하지 않는다.
- M4 캐시는 `/srv/cache/lsoverlay-core-dev/media/`를 사용하도록 설계하되, 현재 이 경로를 만들거나 mount하지 않는다. 운영 `/srv/data`에 넣지 않는다.

Windows에서 아래 스크립트를 실행한 터미널을 유지한다. 로컬 15188이 이미 사용 중이면 스크립트의 `-LocalPort`로 다른 빈 로컬 포트를 명시한다. 이 환경의 Docker internal network에서는 요청한 host port mapping이 실제로 생성되지 않았으므로, published port를 제거하고 호스트가 접근 가능한 컨테이너 IP를 매번 조회한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\CODEX\Worktrees\Gacha_Overlay-core\tools\core\dev\connect-fixture.ps1"
```

다른 터미널에서 `http://127.0.0.1:15188/healthz`를 확인한다. 이 경로는 SSH 암호화 안의 loopback HTTP다. `http://192.168.0.10:5188` 같은 운영 origin 노출 우회는 사용하지 않는다. 보안 검증을 통과하지 않은 직접 LAN HTTP를 제품에 허용하지 않는다.

합성 fixture가 발급한 임시 token도 콘솔/로그에 기록하지 않는다. 사용자 승인에 따른 실제 Discord 일회 조회는 별도 `LSOverlay.CoreReadProbe`와 [실데이터 검증 안내](DISCORD-SETUP.ko.md)를 사용한다. **합성 fixture에 운영 credential을 넣지 않는다.** 제품의 auth/Remote 저장 파일, 운영 OAuth/DNS는 변경하지 않는다.

네이티브 HTTP/WebSocket probe는 loopback 합성 manifest를 확인한 뒤에만 동작한다.

```powershell
& 'E:\CODEX\Worktrees\Gacha_Overlay-core\artifacts\core\m3\native\Release\LSOverlayCoreTransportProbe.exe' 'http://127.0.0.1:15188'
```

배포 전 Compose project가 `lsoverlay-core-dev`, 서비스가 `fixture` 하나뿐인지 확인한다. 운영 compose를 대상으로 사용하지 않는다. Docker image/runtime 재사용은 운영 환경 변수나 볼륨의 복사를 뜻하지 않는다.
# M4 media 검증 추가

`run-media-probe-m8.py`는 `/srv/apps/lsoverlay-core-dev/m4/probe`에 별도로 올린 Linux publish만 실행한다. network `none`인 일회성 컨테이너이며 bot/OAuth/운영 환경 변수를 읽지 않는다. 캐시/합성 파일은 `/srv/cache/lsoverlay-core-dev/media-validation/<run>`에 남기며 `/srv/data`나 운영 backup 대상에 넣지 않는다. 부모 45초 deadline, non-root, read-only root, 512 MiB/0.5 CPU 제한을 적용하고 자체 컨테이너만 정리한다. 실행 전후 운영 ID/image/start/health가 같은지 확인한다. 실제 CDN 다운로드나 사용자용 media service 배포를 대신하지 않는다.
