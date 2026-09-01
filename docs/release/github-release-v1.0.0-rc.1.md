# Gacha Overlay 1.0.0-rc.1

## 먼저 다운로드하세요

Release 아래의 **`Gacha-Overlay-1.0.0-rc.1-win-x64.zip`**을 받으세요. GitHub의 자동 생성 `Source code` ZIP은 실행용 배포본이 아닙니다.

- ZIP SHA-256: `69B760C09177E5CE323F0AC191ABCFF4A60CB85B2BBBED9A76585BA529F32522`
- EXE SHA-256: `785B63439E1F2D617866956180A4B00D90921EDDECEFDB38494B4DC7017F34C7`
- 대상: Windows x64
- 상태: Draft / Prerelease Release Candidate

> 이 버전은 소규모 사용자 검증을 위한 Release Candidate입니다. Long-run Functional Soak는 통과했지만 종료 시점의 정량 Diagnostic Snapshot은 수집되지 않았습니다.

## 빠른 시작

1. ZIP을 새 폴더에 압축 해제합니다.
2. 포함된 `Gacha Overlay 빠른 시작.pdf`를 먼저 확인합니다.
3. `Gacha Overlay.exe`를 실행합니다.
4. 온보딩에서 Discord 인증, 대상 서버 및 메인 채널 설정을 완료합니다.

OAuth·HUD·판매 대기열·보안과 상세 문제 해결은 같은 폴더의 `Gacha Overlay 사용자 설명서.pdf`를 확인하세요.

## 주요 변경 사항

- Discord 메인 채팅 HUD와 판매 큐
- SOLD/닫힘 반응을 통한 판매 완료 처리
- 이미지, 스티커 및 전달 메시지 표시 보정
- 5개 테마, 타이포그래피, F9/F10 HUD 제어
- 온보딩, 자동 시작/Discord 자동 실행, 진단 ZIP
- 안정성 및 단일 파일 배포 준비 강화

## 검증 결과

- Debug/Release build: warning 0 / error 0
- Debug tests: 1,164 passed / 0 failed / 0 skipped
- Release tests: 1,164 passed / 0 failed / 0 skipped
- `dotnet format --verify-no-changes`: PASS
- self-contained single-file publish: PASS
- 새 폴더 압축 해제 및 실행: PASS
- 현재 트리, Git 인덱스·이력, EXE/두 PDF/ZIP credential 및 개인 정보 감사: PASS
- 빠른 시작: 10페이지 / App Tester 기본 흐름 언급 0 / PDF validator PASS
- 전체 사용자 설명서: 40페이지 / App Tester 조건부 문제 해결만 유지 / PDF validator PASS
- 패키지 루트: EXE, 빠른 시작, 전체 설명서, Licenses의 정확히 네 항목 / 13 files / PASS
- Long-run Functional Soak: PASS

## 알려진 제한 사항

- 고정된 Production Guild/판매 채널과 선택 가능한 메인 채널 하나를 사용합니다.
- 판매 반응 추적에는 Discord 채널 UI 접근성이 필요합니다.
- 일부 스티커 또는 전달 콘텐츠는 Local RPC가 제공하는 정보에 따라 일반화될 수 있습니다.
- 자동 업데이트와 코드 서명은 현재 제공되지 않습니다.
- 일반 공개용 OAuth 배포는 추후 검토합니다.
- 종료 시점의 정량 Diagnostic Snapshot은 수집되지 않았으며, 장시간 idle/active, Discord 재시작, Sleep/Wake는 별도 사용자 환경 검증 항목입니다.

앱을 종료한 뒤 새 ZIP의 파일로 교체하고 다시 실행하는 방식으로 업데이트합니다. 사용자 설정은 별도로 유지됩니다.

문제 발생 시 앱의 진단 ZIP 내보내기를 사용할 수 있습니다. 공유 전 민감한 정보가 없는지 확인해 주세요.
