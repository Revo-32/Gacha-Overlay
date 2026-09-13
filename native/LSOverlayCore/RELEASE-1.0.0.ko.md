# LS Overlay Core 1.0.0 — 운영 전환 및 릴리즈 검증

2026-09-14. 사용자가 실제 판매 완료/취소와 최종 UI 동작을 확인하고 릴리즈를 승인했다. 정식 배포 제품은 **LS Overlay Core 1.0.0**, 태그 이름은 `core-v1.0.0`이다. 기존 기본판 `v2.4.2` 태그/배포 자산을 대체하거나 삭제하지 않는다.

## 제품 및 서버 변경

- 네이티브 C++20 / Win32 / Direct2D / DirectWrite 클라이언트. 배포 EXE를 실행하면 `https://overlay.revo32.cloud`로 직접 연결하며 개발 SSH·PowerShell launcher에 의존하지 않는다.
- 채팅·미디어·판매 완료/취소·세션 인원과 사용자 확인된 설정/UI를 포함한다. Full의 GTA 컴패니언·사업장 관리자·AI 번역·알림센터 전체를 이식한 버전은 아니다.
- `LS_CORE_ENABLED=1`인 Backend에 `/api/v1/core/{manifest,stream,channel,media}` 경로를 등록한다. 기존 OAuth/HTTPS/trusted-proxy/client-IP 및 판매 상태 변경 서비스의 소유자·권한·중복 요청 검사를 재사용한다.
- 세션은 installation별 하나, 사용자별 채팅방/판매/미디어 상태 분리. 기본 전체 연결 한도 64개를 기존 Full 연결과 공유한다. 채팅과 판매 bootstrap/pump는 독립되어 느린 판매 조회가 채팅 준비를 막지 않는다.
- 메시지 이벤트와 재동기화 신호를 구분하고, 손실된 구간은 canonical bootstrap으로 복구한다. heartbeat 15초, ACK 제한 75초, 연결 수명 6시간 후 재연결한다. 미디어 ID 수명은 8시간이며 매 fetch에서 현재 권한을 검사한다.
- 자격증명 만료·교체를 지속 검사하고, 인증 연결이 끊기면 네이티브 표시를 비운다. 유효한 Core DPAPI 로그인은 재사용하며 Full 로그인/설정을 읽지 않는다. 브라우저 승인을 거부/만료시킨 경우 앱 재실행으로 다시 시도한다.
- 미디어는 전용 비공개 worker로 분리. 포트는 M8 loopback `15191`에만 노출하고 별도 키, non-root, read-only, 512 MiB/0.5 CPU/64 PID, 폐기 가능한 캐시를 사용한다. Backend 전체 동시 요청 8개, 세션별 4개. 변환 전후 권한 재확인.
- 판매 ID의 GUID 대소문자 불일치 수정. 완료/취소 요청의 결과는 HTTP 결과와 공식 상태 read-back으로 확인하며 불확실한 쓰기를 자동 재시도하지 않는다.
- Backend에 Full Infrastructure 어셈블리를 참조하지 않는다. 판매 카탈로그와 한국어 문자열 두 원본 리소스만 공유하여 서버의 기존 SalesStateEngine을 사용한다. 관련 보안 테스트는 CoreClient의 명시적 판매 표현만 허용하고 Presence/Gateway 경계를 유지하도록 조정했다.

## 검증 결과

| 범위 | 결과 |
| --- | --- |
| 전체 .NET 테스트 | 2,583 PASS / 실패·스킵 0 |
| 네이티브 단위 테스트 | 7/7 PASS |
| 합성 Sales GUI | 64 assertions PASS, Discord 쓰기 0 |
| 기존 개발 bridge self-test | 101 assertions PASS |
| M8 정확한 운영 이미지 DLL, 외부 네트워크 차단 Linux 컨테이너 | 11 PASS: 두 사용자/채널 분리, 재연결 정리, 느린 Sales와 독립 Chat, 권한 철회, 인증 만료·교체, 미디어 권한/경계 |
| 실제 운영 Core 인증 최초 연결 | Chat live 20개, Sales live 1행, host 2개, Ready 3,464 ms |
| 실제 운영 재연결 | 새 generation/동일 사용자 확인, Chat/Sales live, Ready 636 ms |
| 실제 운영 인증 미디어 | 1,545 bytes / 32×29 derivative 수신 PASS |
| 전용 worker Klipy / Giphy | 18 / 15 frames, 모든 프레임 SHA-256 검증 PASS |
| worker 잘못된 요청 | 허용되지 않은 source·로컬/file 주소·무인증 요청 4개 거부 |
| Docker 소스 경계 | 211개 필요 파일, 격리 restore/publish와 실제 M8 Docker build PASS |

Ready 수치는 해당 2회 연결의 계측이며 장기 평균이나 실제 Discord 새 메시지 도착 지연, Full과의 공정한 A/B 결과가 아니다. 장시간 실게임 부하 및 모든 사용자/모니터 조합을 다시 검증했다고 주장하지 않는다. 판매 실제 조작은 사용자 확인 결과를 사용했으며 릴리즈 점검에서 임의로 Discord 글을 변경하지 않았다.

## 운영 배포 및 복구 기록

- 운영 이미지: `lsoverlay/backend:core-1.0.0-rc1`, ID `sha256:c6933b9aa421f7b4f81f3ff492eb2e2ce340f32138117c43a9c723f6e127732c`.
- 정확한 이미지의 Backend DLL SHA-256: `304ce31344b0d209f2768baf3d83d62a4d734d103b12165bc7b6dc91dbfee58f`. 이미지에서 추출한 실제 DLL로 위 Linux 테스트를 실행했다.
- 서버 소스: `/srv/apps/lsoverlay-core-release/rc1/source`. 실제 시작 조합은 기존 `/srv/apps/lsoverlay/compose.yaml` + `/srv/apps/lsoverlay-core-release/rc1/compose.core.yaml`이다. 다음 배포에도 두 파일을 함께 사용한다.
- 기존 `/srv/data/lsoverlay/backend`, OAuth, trusted cloudflared peer, Status 컨테이너를 유지했다. 기존 Full API는 데이터 보존과 복구를 위해 남겨 두며 신규 기능/지원 기준은 Core다.
- 최초 배포는 점검용 Python HTTP 요청의 외부 403 때문에 자동으로 기존 이미지로 롤백했다. 기존 정상 서버도 같은 Python 요청은 403, 일반 curl은 200임을 확인했다. 점검 클라이언트만 바꾸어 재배포했고, native WinHTTP 인증 최초/재연결/미디어를 검증했다. Cloudflare/DNS 규칙은 변경하지 않았다.
- 두 차례의 정지 시점 백업을 보호 경로에 보존했다. 최종 백업: `/srv/apps/lsoverlay-core-release/backups/before-core-20260914-041342`. 비밀값/운영 데이터는 저장소·배포 ZIP에 포함하지 않는다.
- 롤백 시 기존 compose 파일만으로 `backend`를 `--no-deps --no-build` 재생성한다. 기존 이미지 `sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19`와 이전 소스를 보존했다. **새 인증정보를 잃지 않도록 실행파일/구성만 되돌리고 데이터를 자동으로 과거 백업으로 복원하지 않는다.** Railway는 사용하지 않는다.

## 배포물

- `LS-Overlay-Core-1.0.0-win-x64.zip`: EXE, 필수 폰트, 안내, MIT/yyjson/폰트 라이선스, build.json과 SHA256SUMS만 포함한다.
- 개발/테스트 EXE, PDB, 소스, SSH launcher, 미디어 캐시, 인증정보는 제외한다.
- EXE ProductVersion 1.0.0. CRT 정적 링크. 코드 서명은 **미제공**이며 SmartScreen 경고 가능성을 안내한다.
- 기본판 지원 종료 및 새 ZIP 설치 안내를 README/릴리즈 노트에 명시한다. 기존 기본판 설정을 삭제하거나 몰래 이전하지 않는다.

배포 원본 커밋은 ZIP의 `build.json`, ZIP 해시는 GitHub Release의 SHA256SUMS를 기준으로 확인한다. 위 결과는 로컬 `artifacts/core/core-release` 및 M8 `rc1`의 별도 검증 산출물에 보존한다.
