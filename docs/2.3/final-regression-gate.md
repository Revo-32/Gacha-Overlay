# LS Overlay 2.3 최종 기능 검증

2026-09-08, 사용자 확인을 마친 12pt 알림센터 및 판매 상세 이모지 수정 포함 후보.

- 기준 커밋: `5dde5fbfd3bd5a8ce0f6463d4a38772bb416dfb6` (v2.2.0).
- Clean Release build: PASS, 경고 0 / 오류 0.
- 전체 Release 테스트: 1회, 1,813 PASS / 실패 0 / 건너뜀 0.
- `dotnet format --verify-no-changes --no-restore`: PASS.
- `git diff --check`: PASS.
- self-contained win-x64 single-file publish: PASS.
- 알림 버튼 위치, 불투명 채팅 영역 패널, 12pt 글자, 멘션 배경 설정 기본 ON, ID 기반 강조 정책, 판매 14 DIP 왼쪽/세로 중앙 정렬과 상세 이미지 재사용 경로를 확인했습니다.
- 로컬 GTA 실행 중 생산 시간 누적(스토리 모드 포함), 포커스와 실행 상태 분리, 용어집 및 숫자 보호를 포함합니다.
- 사용자 실제 실행 확인 완료. 운영 배포, 공개 패키지 검증 및 릴리즈 완료를 의미하지 않습니다. 이후 릴리즈 준비 단계에서 별도 검증합니다.
- 테스트 결과 및 검증 바이너리는 `artifacts/validation-2.3-final`에 로컬 보관하며 커밋에 포함하지 않습니다.
