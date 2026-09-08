# LS Overlay 2.3 — 알림/멘션/판매 UI 후속 수정

2026-09-08 실제 사용자 화면 피드백 반영. 기존 작업은 보존하고 아래 UI만 조정했습니다.

## 최신 수정 — 알림센터 12pt

- 알림 목록 글자 크기만 채팅 크기 연동에서 고정 12pt(16 DIP)로 변경했습니다. 글꼴·굵기와 줄 높이 배율은 기존 채팅 설정을 재사용합니다. 메인 채팅 설정값과 판매 UI는 변경하지 않았습니다.
- 관련 설정/타이포그래피 테스트 1 PASS, Release publish PASS. 전체 회귀와 실제 UI 검증은 실행하지 않았습니다.
- 최신 EXE: [notification-12pt-win-x64/GachaOverlay.App.exe](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/notification-12pt-win-x64/GachaOverlay.App.exe). 아래 경로들은 이전 후보입니다.

## 이전 수정 — 왼쪽 정렬 및 상세 이모지 수명주기

- 문구는 14 DIP를 유지하고 세로 중앙·가로 왼쪽(상태 아이콘 옆) 정렬로 변경했습니다. 아래 ‘중앙 정렬’ 기록은 이전 후보입니다.
- 크기 변경/잠금/로컬 표시 갱신이 상세 행을 다시 만들면서 이미지 토큰도 초기화하지만, 이미지 로딩은 판매 snapshot에만 연결돼 있던 문제를 재현했습니다. 수정 전 테스트에서 로드된 Bitmap이 너비 변경 후 null이 되는 것을 확인했습니다.
- MessageId와 원문이 같은 행은 기존 토큰/이미지를 재사용합니다. 별도 캐시는 만들지 않았습니다.
- 상세 행 갱신 이벤트에 기존 이미지 로딩을 연결했습니다. 이미지가 이미 있는 정적 토큰은 재요청하지 않고, 원문 변경/삭제 후 이전 비동기 결과는 반영하지 않습니다. 해제 시 이벤트 구독도 제거합니다.
- 수정 전 재현 실패 1건, 수정 후 관련 테스트 14 PASS. 화면 렌더링, Release publish, 관련 서식 및 diff 검사 PASS. 전체 회귀/실제 Discord 이미지 로딩은 재검증하지 않았습니다.
- 최신 EXE: [sales-detail-fixed-win-x64/GachaOverlay.App.exe](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/sales-detail-fixed-win-x64/GachaOverlay.App.exe).
- 이전 후보는 보존했습니다. 커밋/푸시/서버 배포 없음.

## 추가 미세 수정 — 판매 안내 중앙 정렬

- 판매 바 양쪽 영역을 같은 폭으로 확보해 안내 문구를 전체 바의 가로 중앙에 배치했습니다. 세로 방향도 가운데로 맞췄습니다.
- 안내 글자 크기를 16 → 14 DIP로 줄였습니다. 상세정보와 판매완료 동작은 유지합니다.
- 관련 9개 테스트 PASS, WPF 렌더링 확인, Release publish 및 diff 검사 PASS. 전체 회귀는 재실행하지 않았습니다.
- 최신 EXE: [sales-centered-win-x64/GachaOverlay.App.exe](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/sales-centered-win-x64/GachaOverlay.App.exe).
- 아래 ux-corrective-win-x64 경로는 이전 후보입니다. 이번 중앙 정렬 수정은 위 최신 후보로 확인해야 합니다.

## 변경

- 알림 버튼을 일반 HUD/미니멀 HUD 모두 설정 톱니바퀴 바로 왼쪽으로 이동했습니다.
- 열린 알림센터는 판매 영역을 제외한 메인 채팅 영역 전체를 덮습니다. 창 크기를 바꾸지 않고 내부 스크롤을 사용하며 닫기 버튼을 제공합니다.
- 알림 배경은 항상 불투명합니다. 채팅/전체 HUD 투명도가 0이어도 알림 패널은 불투명하게 유지합니다. 테마 색상은 유지합니다.
- 알림 본문은 채팅 본문 글꼴·크기·굵기·줄 높이를, 문맥/작성자는 채팅 닉네임 글꼴·굵기를 재사용합니다. 별도 글꼴 설정은 추가하지 않았습니다.
- 설정 → 채팅에 ‘나를 멘션한 메시지 배경 강조’를 추가했습니다. 기본값 ON으로 기존 표시를 유지하며 저장/다시 불러오기를 지원합니다. OFF에서는 행 배경과 멘션 토큰 배경을 제거합니다. 멘션 글자색·좌측 표시·알림 생성은 유지합니다.
- 접힌 판매 바의 원문/이모지 요약은 제거하고 차례 안내 글자를 12 → 16 DIP로 키웠습니다. 원문과 이모지는 아래 펼친 상세에 유지합니다. 판매완료 및 접기/펼치기는 그대로입니다.

## 검증

- WPF Release build: PASS, 경고 0 / 오류 0.
- UI/설정/알림/판매 집중 테스트: 168 PASS, 실패 0, 건너뜀 0.
- 실제 WPF 합성 화면에서 버튼 위치, 패널 크기, 잠금, 불투명도를 확인했습니다.
- 새 멘션 설정의 저장/재로딩, 배경 ON/OFF, 기존 의미 분류 유지 및 채팅 글꼴 재사용을 검증했습니다.
- 관련 파일 format 및 git diff --check: PASS.
- self-contained win-x64 publish: PASS.
- 이번 후속 수정에서는 전체 회귀 테스트를 다시 실행하지 않았습니다. 이전 보고서의 1,810개 결과는 이전 후보 기준입니다.
- 실제 사용자 게임 화면 검증: 미검증.

## 새 검증 EXE

[GachaOverlay.App.exe](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/ux-corrective-win-x64/GachaOverlay.App.exe)

기존 오버레이를 트레이에서 완전히 종료하고 위 EXE를 실행합니다. ux-corrective-win-x64 폴더 전체를 유지해야 합니다.
이전 win-x64 후보는 덮어쓰지 않았습니다. 새 UI는 반드시 새 경로에서 검증해야 합니다.

## 상태

UNCOMMITTED / UNPUSHED. 커밋·푸시·서버 배포 없음.
