# GTA AI Localization — Staging corrective 후보 검증

기준 기능 커밋: `2c0c247902ac319dd1302eb591362bd4f5028f70`.
대상 브랜치: `feat/gta-ai-localization`. Production 승격 승인은 포함하지 않는다.

## 확인한 원인

- `LocalizationProtection`의 미등록 영어 연속 구간 보호가 일반 문법을 이름으로 취급했다. `three`에서 시작한 구간이 승인 용어 `Bunker Research`까지 삼켰다.
- Staging에서 기존 Bot 인증을 서버 내부에서만 사용한 읽기 전용 Discord.Net 조회로 실제 릴레이를 확인했다. 정상 `DiscordNetGtaEventSource.Build` 경로에 같은 DTO를 전달했다. Gateway 시작이나 Discord 쓰기 작업은 하지 않았다.
- 실제 공개 원문은 2,423자, 정규화 후 2,351자였다. 정규화에는 누락이 없었다. 기존 `CanonicalEventDocumentBuilder.MaximumBlockLength = 2048`에서 처음 잘렸다.
- 완전한 구문은 `Bobcat Security & Gruppe Sechs depots).`이며, 정규화 문자열의 첫 2,048자는 정확히 `Bobcat Security & Gruppe Sechs de`로 끝난다. 모델이 원문을 자른 것이 아니다.
- 공개 텍스트만 `tests/GachaOverlay.Tests/Fixtures/GtaLocalization/real-business-rivalries.txt`에 보존했다. Discord ID, 프로필, 인증 정보는 포함하지 않았다.

## 수정 경계

- 일반 미등록 영어 보호를 제거했다. 숫자·날짜·플랫폼 사실, 최장 승인 용어, 명시적 엔터티 근거만 보호한다.
- 차량은 제조사+모델 형태, 제작자 작업은 명시적으로 이름이 지정된 Community 작업 문맥, 의류 보상은 금액 보상 뒤의 명명된 의류 항목, 창고 소유자는 명시적 창고 문맥을 사용한다. 대문자만으로 이름을 추정하지 않는다.
- 영어 수사 one~ten은 동일 숫자값으로 보호한다. 기존 승인 용어집과 preserve_until_verified 정책을 유지한다.
- 알려진 한국어 용어를 토큰 바깥에서 반복한 출력은 `RepeatedTerm`으로 거절한다. 문자열을 추측해 고치지 않는다.
- Canonical 블록의 2,048자 미리보기 제한을 제거했다. 전체 안전 제한은 16 KiB/64블록으로 유지하며, 실제 제한 적용 시 `OwnInputIntegrity`에 불완전 여부·원래 길이·사유를 기록한다.
- 의미 모델의 원문 필드에서 256/512자 표시 제한을 제거했다. 기존 Snapshot 화면 투영 제한은 유지한다. 알려진 불완전 입력은 추출/검증 단계에서 거절한다. 짧은 제목에 마침표를 요구하지 않는다.
- 과거 메모리 항목은 용량/개수 한도 안에서 retired 상태로 보존하고 현재 번역으로 제공하지 않는다. 수동 파일 삭제나 Volume 초기화는 하지 않는다.

## 버전

- 모델: `gemini-3.5-flash-lite` 유지.
- 보호 정책: `gta-protect-2`, 캐시 identity에 포함.
- 프롬프트: `gta-ko-3.2`. 보호되지 않은 대문자/고유명도 번역 대상임을 명시하고, 동일 금액의 목표/보상 구분과 보상 배율 적용 관계를 설명했다. 혼동 가능한 예제 토큰은 제거했다.
- 한국어 표면 규칙: `ko-surface-1` 유지.
- 용어집: `2026-09-08.1`, 459개 유지.

## 로컬 검증

- 보호/한국어 표면/GTA Companion 집중 테스트: 667 PASS.
- Backend 포함 집중 테스트: 1,214 PASS.
- 최종 Clean Release 빌드: 경고 0, 오류 0.
- 최종 전체 Release 테스트 1회: 2,452 PASS, 실패 0, 건너뜀 0.
- `dotnet format --verify-no-changes --no-restore`: PASS.
- `git diff --check`: PASS.
- Docker 소스 closure: 183/183 PASS. Docker 이미지 실행 검증으로 오인하지 않는다.
- 최종 공개 원문 통제 실호출: 24개 필드, Gemini 1회, 토큰/숫자/용어/출력 검증 PASS. 과도한 보호로 발생했던 영어 문법 누출은 사라졌다. 이 결과는 실제 인증 Snapshot 검증이 아니다.

초기 실호출의 토큰 누락·혼용/영문 잔존 및 표면 품질 문제도 검토했다. 실패한 응답을 성공으로 보고하거나 실제 Staging 메모리에 주입하지 않았다.

## 실제 Staging 판정

이 문서는 커밋 전 후보 검증 기록이다. 실제 배포/24개 필드 전수 검토/인증 Snapshot/캐시 반복/재시작 결과는 별도의 후속 Staging 실행 보고서에 기록한다. 그 결과와 사용자 번역 승인이 나오기 전에는 Production 승격을 승인하지 않는다.

Production: **NOT MODIFIED**.
