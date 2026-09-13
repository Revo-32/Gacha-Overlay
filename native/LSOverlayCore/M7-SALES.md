# Core 정식 전환 — 판매 조작 검증

2026-09-14. **구현 및 합성 검증 통과 / 실제 본인 판매 조작 확인 대기 / 공개 릴리즈 미완료**.

## 승인된 범위와 구현

사용자는 첫 정식 Core에 판매 완료·되돌리기를 포함하고, 검증 통과 후 릴리즈 진행을 승인했다. 실제 변경 대상이 지정되지 않은 운영 판매글을 자동으로 수정하지 않았다.

- 서버에서 현재 인증 사용자 소유의 판매글, 완전한 최신 판매 evidence, upstream Sales generation에 기반한 의미 힌트를 생성한다. 재동기화·권한 철회·삭제 시 힌트는 제거한다. 다른 사람의 완료 표시는 취소 대상으로 제공하지 않는다.
- C++는 Discord/Sales 내용을 파싱하지 않는다. 서버가 제시한 본인 대상에 완료 또는 완료 취소 버튼을 제공한다. 사용자 요청에 따라 확인창 없이 클릭 즉시 전송한다. 잠금 상태에서는 조작할 수 없다. 본인 완료글은 활성 대기열이 비어도 취소할 수 있다.
- 네이티브의 별도 작업자가 기존 `https://overlay.revo32.cloud/api/v1/sales/status`로 직접 요청한다. 기존 사용자/소유자/권한/generation/중복/rate-limit 검사를 재사용한다. 개발 Bridge는 쓰기 HTTP 요청을 계속 거부한다.
- 요청마다 GUID를 발급한다. 동시 의도는 한 개로 제한하며 UI 스레드에서 네트워크를 기다리지 않는다. 화면 표시 이후 대상/generation이 바뀌면 제출을 거절한다. 아직 전송하지 않은 의도는 연결 단절 시 취소한다.
- HTTP 성공과 공식 Snapshot의 봇 완료 상태를 모두 확인한다. 순서가 바뀌어 Snapshot이 먼저 와도 처리한다. 응답 유실 시 자동 재전송하지 않으며 기존 목록을 임의로 변경하지 않는다. 공식 확인이 계속 오지 않으면 미확인/대기 상태가 유지된다.
- 완료 취소는 봇의 상태 표시를 지우는 기존 `clear` 동작이다. 다른 사용자의 완료 표시가 남아 있으면 판매 대기열로 복귀하지 않을 수 있다.

## 실행 및 계측

기존 Core를 트레이에서 종료한 후:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\CODEX\Worktrees\Gacha_Overlay-core\tools\core\dev\launch-sales-core.ps1"
```

- 최신 실행 파일: `artifacts/core/m7-sales-polish/native/Release/LSOverlayCore.exe`. 초기 `m7-sales` 및 `m7-sales-ux` 실행본은 보존했다.
- 옵션 없는 launcher는 이전 `m6-hud-polish` 읽기 전용 실행본을 유지한다.
- 전용 `launch-sales-core.ps1`은 옵션 없이도 `-SalesActions`를 지정한다. 최초 실사용 시도에서 `m6-hud-polish/live-20260914-031043`의 읽기 전용 실행만 확인되어 모드 혼동을 줄이는 진입점을 추가했다. 해당 시도의 버튼 미표시는 새 조작 실행본의 실패로 판정하지 않는다. 실제 새 실행본은 아직 확인 대기다.
- 개발 관측 파일에 조작 활성/대기 여부와 submitted/completed/undone/rejected/uncertain 집계만 추가했다. 대상 메시지 ID, 내용, 자격증명은 기록하지 않는다.
- 실제 검증은 변경해도 되는 **본인 판매글**에서 완료 → 공식 반영 확인 → 완료 취소 → 공식 반영 확인이다. 임의의 타인 글, 테스트 글 자동 작성 또는 운영 채널 설정 변경은 하지 않는다.

## 이번 자동 검증 결과

| 범위 | 결과 | 근거 |
| --- | --- | --- |
| 서버 판매 조작 및 Core 계약 집중 테스트 | 46/46 통과 | `artifacts/core/m7-sales/dotnet-tests/core-sales-actions.trx` |
| 개발 Bridge | 101 assertions 통과 | 로컬 및 M8 Linux `--network none` self-test; 외부 호출/Discord 쓰기 0 |
| native CTest | 7/7 통과 | `artifacts/core/m7-sales/native/Testing/Temporary/LastTest.log` |
| native 조작 상태기계 | 33 assertions 통과 | `LSOverlayCoreSalesActionTests.exe`; 실제 HTTP/Discord 호출 0 |
| 판매 GUI | 23 assertions 통과 | `artifacts/core/m7-sales/sales-gui-02/native-sales-checks.json` 및 캡처 직접 확인 |
| 기존 설정/HUD GUI | 34 assertions 통과 | `artifacts/core/m7-sales/settings-gui-01/settings-checks.json` |

판매 GUI는 잠금 중 입력 금지, 처리 중 중복 입력 금지, canonical queue 보존, 빈 대기열의 완료 취소, 360 DIP 폭을 검증했다. 합성 GUI 통과를 실제 Discord 쓰기 성공으로 간주하지 않는다. 설정 GUI 검증 이후 마지막 소스 변경은 조작 집계/개발 상태 문구/판매 합성 검증 경로이며 전체 설정 검증을 다시 수행했다고 쓰지 않는다.

## 후속 UX 수정 — 2026-09-14

사용자는 조작 버전의 확인창과 버튼을 실제로 확인한 뒤, 확인창 제거 및 버튼 글자 가로·세로 중앙 정렬을 요청했다. `m7-sales/live-20260914-031424`에서 조작 모드 활성은 확인했으나 마지막 집계의 제출/완료/취소 횟수는 모두 0으로, 이를 실제 쓰기 성공으로 판정하지 않는다.

- 확인창 및 모달 오류 알림 없이 직접 클릭 경로에서 기존 controller에 요청한다. 오래된 대상/generation 및 중복 의도 차단은 유지한다.
- 버튼 위치와 판매 헤더 정렬은 유지한다. 버튼 안 글자만 실제 DirectWrite 텍스트 측정값으로 가로·세로 중앙에 배치한다. 외곽선용 여백이 텍스트 높이에 더해져 위로 치우치던 부분도 제외한다.
- `m7-sales-ux` Release CTest 7/7 통과. 판매 합성 GUI 33 assertions 통과 (`artifacts/core/m7-sales-ux/sales-gui-02/native-sales-checks.json`). 실제 클릭 dispatch의 무확인창 제출/중복 차단, 완료·취소·처리 중 및 좁은 창 정렬/영역 내 배치를 검증했다. 외부 호출 및 Discord 쓰기는 0이다. 생성된 화면 캡처도 확인했다.
- 판매 전용 launcher가 검증된 새 실행본을 선택하도록 변경했다. 이번 후속 UX 수정에서 Backend·개발 Bridge는 재배포하지 않았다.

## M8 변경 범위

### 안내 문구/잠금 표시 후속 수정

- `m7-sales-polish`: 판매 헤더 안내 문구를 DirectWrite 측정 높이 기준으로 세로 중앙 정렬했다. 왼쪽 여백 14 DIP와 글꼴/크기는 유지했다.
- Renderer의 실제 잠금 상태를 판매 뷰에 전달한다. 잠기면 완료·완료 취소·확인 중 버튼의 테두리/글자와 접기 화살표를 그리지 않고 hit target도 비운다. 잠금 해제 시 다시 표시하며 대기열, 접힘 상태, 처리 중 요청은 바꾸지 않는다.
- CTest 7/7, 판매 GUI 64 assertions 통과. `artifacts/core/m7-sales-polish/sales-gui-01/native-sales-checks.json` 및 잠금/해제 캡처를 확인했다. 안내 문구의 수직 중앙/왼쪽 위치, 완료·처리 중·취소 상태의 잠금 숨김/복원, 숨겨진 hit target 제거를 포함한다. 실제 Discord 쓰기 0, Backend/Bridge 재배포 없음.

격리된 개발 Bridge만 `bridge-r9`로 교체했다. 이전 r8 컨테이너는 `lsoverlay-core-readonly-bridge-1-before-bridge-r9`로 보존했다.

- 새 개발 컨테이너 ID: `ef7ed5f0ca93accf3b25229d46b31f2e2987805aa09e43df2d33e67db2035d1f`.
- loopback `127.0.0.1:15190`만 유지하며 연결 1개/세션 30분 개발 제한을 공개 배포용으로 완화하지 않았다.
- Production Backend/Status의 컨테이너 ID·이미지·시작 시각·healthy 상태가 배포 전후 동일함을 확인했다.
- 운영 Backend/Status 및 미디어 worker 재배포, OAuth/DNS/Cloudflare 변경, 버전 변경, 커밋·푸시·태그·공개 릴리즈는 하지 않았다.

## 남은 정식 릴리즈 조건

후속: 2026-09-14 사용자 최종 조작/UI 정상 확인 완료. Windows GUID의 대문자와 .NET 응답의 소문자 차이 때문에 HTTP 결과를 미확인으로 분류하던 원인을 찾아 요청 ID를 소문자로 정규화하고 회귀 테스트를 추가했습니다. 최신 후보의 native 7/7 및 Sales GUI 64 assertions PASS. 실제 운영 판매를 추가로 변경하지 않았으며, 후속 운영 연결/재연결 검증은 읽기 전용입니다. 정식 연결 전환은 [Core 1.0.0 기록](RELEASE-1.0.0.ko.md)을 참조하세요. 아래 수치는 기존 UX 검증 시점의 보존 기록입니다.

`m7-sales-ux/live-20260914-032130`의 마지막 관측에서 제출 2회, 공식 완료 확인 1회, 공식 취소 확인 1회, 대기 false, 거절 0회를 확인했다. 다만 uncertain 집계도 2회이므로 HTTP 응답 정상 수신 경로까지 통과했다고 간주하지 않으며 응답 식별/처리 경로의 추가 점검이 필요하다. 사용자가 조작한 대상 외에 별도 실제 판매 변경을 수행하지 않았다.

정식 HTTPS Core 접속 경로, 사용자별 격리, 인증/재연결 복구, SSH가 필요 없는 기본 실행 및 배포 패키지도 남아 있다. 이는 [정식 릴리즈 준비](RELEASE-PREPARATION.ko.md)의 기존 차이이며 조작 GUI만 통과했다고 완료 처리하지 않는다. 동일한 릴리즈 승인 자체를 다시 요청할 필요는 없다.
