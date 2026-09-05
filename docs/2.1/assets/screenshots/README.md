# LS Overlay 2.1 실제 UI 스크린샷

2026-09-05 사용자가 제공한 2.1.0 실제 HUD·설정 캡처에서 문서용 PNG 24개를 준비했습니다. 원본의 임시 경로나 개인 정보는 저장하지 않습니다. `provenance.json`에 원본 SHA-256, 자르기 좌표, 가림 영역, 결과 SHA-256을 기록합니다.

## 처리 정책

- AI 생성·재현 UI 없음. 실제 화면을 자르고 PNG로 무손실 저장.
- 다른 사용자의 닉네임과 채팅·판매 원문은 불투명하게 가림. 원본 픽셀을 PDF에 숨겨 넣지 않음.
- 화면의 단축키, 투명도, 수치는 사용자 설정 예시이며 기본값으로 안내하지 않음.
- 세 HUD 전체 화면은 배치 개요용. 조작 설명은 개별 확대 컷 사용.
- 연결 완료 화면은 OAuth 브라우저 승인 화면이 아님.
- 주간 데이터 준비 중 상태는 준비 중 예시로만 설명.
- 단축키 설정 캡처는 현재 지정 값·ESC 안내·적용 버튼을 보여 줌. 입력 대기 중 화면은 아님.
- 판매완료 버튼이 없는 타인 판매 캡처임을 명시. 버튼 합성 없음.

## 자산 목록

| 파일 | 실제 내용 |
|---|---|
| 01-main-hud.png | 세션 인원, 채팅, 판매. 타인 정보 가림 |
| 02-discord-connected.png | 연결 완료와 채널 선택 |
| 03-settings-hud-hotkeys.png | F9/F10 및 채널 단축키 |
| 04-three-huds-overview.png | 잠금 해제된 세 HUD 배치. 대화 가림 |
| 05-main-chat.png | 채팅·스티커. 대화 가림 |
| 06-sales.png | 판매 요약·상세. 판매 원문 가림 |
| 07-gta-companion.png | 일일 선택, 주간 정보 준비 중 |
| 08-business-unlocked.png | 벙커·나이트클럽·LSD 조작 |
| 08-business-cargo.png | 세차장·창고·항공 화물 |
| 09-business-locked.png | 잠긴 습격·하드 행 |
| 09-business-compact.png | 잠긴 사업장 표시 |
| 09-general-timer.png | 일반 12/24/48분 타이머 |
| 10-settings-hotkey-capture.png | 키 입력 칸, ESC, 적용·복원 |
| 11-settings-visual-media.png | 줄 높이·작성자 간격·역할·Reaction |
| 11-settings-themes.png | 다섯 색상 테마 |
| 11-settings-media.png | 미디어·애니메이션·캐시 |
| 12-settings-diagnostics.png | 진단 및 복구 |
| 12-diagnostics-button.png | 진단 생성 안내와 버튼 |
| 13-settings-sales.png | 판매 알림 |
| 14-sales-history.png | 내 히스토리 |
| 15-settings-companion.png | 컴패니언 사용·표시 항목·키 |
| 16-settings-business.png | 사업장 관리자 사용·키·일반 타이머 |
| 17-settings-business-options.png | 사업장별 조건 |
| 18-settings-timer-sound.png | 완료 소리와 미리 알림 |

핵심 슬롯은 실제 화면으로 충족했습니다. 본인 판매완료 버튼, 키 입력 대기 중, 주간 데이터가 채워진 화면은 선택적 보강 대상입니다. 현재 캡처가 그 상태라고 주장하지 않습니다.

원본 재처리가 필요할 때만 `tools/manual/prepare_21_screenshots.py --input-dir <private-input-directory>`를 사용합니다. 일반 PDF 재생성에는 이 폴더의 처리된 PNG만 있으면 됩니다.
