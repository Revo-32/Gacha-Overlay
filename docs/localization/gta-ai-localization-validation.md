# GTA AI 현지화 — 한국어 표면 품질 교정 및 검증

2026-09-08. 미커밋 개발 후보의 후속 검증 보고서.
이 문서는 기존 구현 보고서를 최신 결과로 갱신한다. 승인 전 감사는 [별도 기준 감사](gta-ai-localization-baseline-audit.md)에 보존되어 있다.

## 1. 상태

**PARTIAL — 통제 예제 품질·자동 검증 PASS / 최신 실제 릴레이 E2E 미검증.**

기존 아키텍처와 459개 승인 용어집은 유지했다. 이번 요청의 조사·기간·배수·이중 조사 오류는 실제 최종 18필드에서 0건이었다. 실제 Discord 최신 공지를 수신했다고 주장하지 않는다.

기준 v2.3.0 / 3e4a12434b6790752024c984dde71addaaf719cf.
브랜치 feat/gta-ai-localization.
작업트리: E:\Codex\Worktrees\Gacha_Overlay-gta-ai-localization.

## 2. 수정한 번역 품질 문제

- RP을 → RP를, HSW를, CEO가/CEO는, MC가/MC는 등 알려진 약어 조사.
- 한글 마지막 음절 받침에 따른 은/는, 이/가, 을/를, 과/와, 으로/로. ㄹ 받침의 로 예외 포함.
- 습격을(를) → 습격을 등 보호 토큰 뒤의 이중 조사 제거.
- 숫자와 기간 단위를 분리하여 초/분/시간/일/주/개월로 표시.
- 배수 1.5X/2X/3X/4X → 1.5배/2배/3배/4배.
- 독립된 GTA$와 RP 보호 토큰 사이의 알려진 연결 구문만 GTA 달러와 RP로 표기. GTA$500,000 같은 정확한 금액과 이름 내부 문자는 보존.

## 3. 구현 방식

Gemini는 문장 어순과 일반 한국어 표현을 결정한다. 애플리케이션은 보호된 사실/용어 복원 및 제한된 표면 규칙을 결정한다.

새 KoreanLocalizationSurface는 한글 음절의 받침을 계산하고, 알려진 비한글 토큰의 발음 분류만 작은 내부 사전으로 처리한다. 459개 용어에 수동 메타데이터를 추가하지 않았다. 영어 마지막 ASCII 문자로 발음을 추정하지 않는다.

조사 교정은 보호 토큰 바로 뒤의 독립 조사에 한정한다. 임의의 문장 전체나 고유명 내부를 치환하지 않는다. 알 수 없는 영어 고유명의 발음을 추정하지 않으며, 그런 이름 뒤에 모델이 이중 조사를 내보내면 결과를 거절한다. 목록식 표현 또는 검수된 override가 필요하다. 정상 단일 조사의 모든 영어 고유명 발음을 보장하는 일반 NLP 엔진은 아니다.

따옴표 안 창작 작업명은 내부에 숫자·영문 단위·이중 조사 형태가 있어도 그대로 보존한다. 그 내부 표기를 개선하기 위해 이름을 바꾸지 않는다.

새 규칙은 ko-surface-1, 최종 프롬프트는 gta-ko-3.1이다. 처음 gta-ko-3에서 모델이 RP/이번 주 토큰 대신 표시값을 직접 쓴 오류를 확인하고, 보호 토큰 출력 예시·개수 검사를 명시했다. 숫자/토큰 validator는 완화하지 않았다.

캐시 identity에 프롬프트 버전과 표면 규칙 버전을 모두 반영했다. 승인 용어집 버전은 2026-09-08.1로 유지한다. 이전 gta-ko-2 저장 파일을 별도 로컬 검증 디렉터리에 복사하여 새 버전에서 재번역되는 것을 확인했다. 원래 Last-Good 파일은 삭제하지 않았다.

## 4. 실제 Before / After

아래 값은 기대 문자열을 보고서에 대신 쓴 것이 아니라 보존된 gta-ko-2 평가 JSON과 이번 최종 평가 JSON의 실제 결과다. 이번 최종 값은 Gemini 토큰 응답을 애플리케이션에서 검증·복원·표면 교정한 사용자 표시값이다. 원본 모델 토큰 응답도 로컬 검증 산출물에 별도로 보존했다.

이번 18개는 승인된 공개 GTA 용어와 기존 합성 fixture 구조를 사용한 통제 예제다. 최신 공지의 현재 할인/보상 사실을 뜻하지 않는다.

### 예제 1

- 원문: Complete 5 Contact Missions to receive GTA$500,000
- 이전 실제 gta-ko-2 출력: 연락책 임무 5개를 완료하면 GTA$500,000을 받을 수 있습니다.
- 이번 실제 최종 출력: 연락책 임무 5개를 완료하면 GTA$500,000을 받을 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 2

- 원문: GTA$500,000
- 이전 실제 gta-ko-2 출력: GTA$500,000
- 이번 실제 최종 출력: GTA$500,000
- 검증: PASS, Snapshot 반영 확인

### 예제 3

- 원문: Earn 3X GTA$ and RP on Acid Lab Sell Missions.
- 이전 실제 gta-ko-2 출력: LSD 제조실 판매 임무에서 GTA$ 및 RP을 3X로 획득할 수 있습니다.
- 이번 실제 최종 출력: LSD 제조실 판매 임무에서 GTA 달러와 RP를 3배로 획득할 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 4

- 원문: Earn 1.5X GTA$ and RP for 90 minutes.
- 이전 실제 gta-ko-2 출력: 90 minutes 동안 GTA$ 및 RP을 1.5X로 획득할 수 있습니다.
- 이번 실제 최종 출력: 90분 동안 GTA 달러와 RP를 1.5배로 획득할 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 5

- 원문: Get 30% off Service Carbine this week.
- 이전 실제 gta-ko-2 출력: 이번 주 서비스 카빈을 30% 할인된 가격에 이용할 수 있습니다.
- 이번 실제 최종 출력: 이번 주 서비스 카빈을 30% 할인된 가격에 이용할 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 6

- 원문: 30% OFF Agency and Auto Shop
- 이전 실제 gta-ko-2 출력: 사무소 및 튜닝 샵 30% 할인
- 이번 실제 최종 출력: 사무소와 튜닝 샵 30% 할인
- 검증: PASS, Snapshot 반영 확인

### 예제 7

- 원문: 40% OFF Cocaine Lockup and Meth Lab
- 이전 실제 gta-ko-2 출력: 코카인 제조 아지트 및 필로폰 제조실 40% 할인
- 이번 실제 최종 출력: 코카인 제조 아지트와 필로폰 제조실 40% 할인
- 검증: PASS, Snapshot 반영 확인

### 예제 8

- 원문: 25% OFF Mansion Property
- 이전 실제 gta-ko-2 출력: 맨션 부동산 25% 할인
- 이번 실제 최종 출력: 맨션 부동산 25% 할인
- 검증: PASS, Snapshot 반영 확인

### 예제 9

- 원문: 30% OFF Grotti Turismo Omaggio
- 이전 실제 gta-ko-2 출력: Grotti Turismo Omaggio 30% 할인
- 이번 실제 최종 출력: Grotti Turismo Omaggio 30% 할인
- 검증: PASS, Snapshot 반영 확인

### 예제 10

- 원문: Service Carbine, Pistol Mk II, Combat MG Mk II
- 이전 실제 gta-ko-2 출력: 서비스 카빈, 피스톨 Mk II, 컴뱃 MG Mk II
- 이번 실제 최종 출력: 서비스 카빈, 피스톨 Mk II, 컴뱃 MG Mk II
- 검증: PASS, Snapshot 반영 확인

### 예제 11

- 원문: Complete The Cluckin' Bell Farm Raid to receive GTA$500,000.
- 이전 실제 gta-ko-2 출력: 클러킹 벨 농장 기습을(를) 완료하면 GTA$500,000을 받을 수 있습니다.
- 이번 실제 최종 출력: 클러킹 벨 농장 기습을 완료하면 GTA$500,000을 받을 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 12

- 원문: Complete The Cayo Perico Heist to receive GTA$1,000,000.
- 이전 실제 gta-ko-2 출력: 카요 페리코 습격을(를) 완료하면 GTA$1,000,000을 받을 수 있습니다.
- 이번 실제 최종 출력: 카요 페리코 습격을 완료하면 GTA$1,000,000을 받을 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 13

- 원문: Play Mansion Raid this week.
- 이전 실제 gta-ko-2 출력: 이번 주 Mansion Raid 플레이
- 이번 실제 최종 출력: 이번 주 Mansion Raid 플레이
- 검증: PASS, Snapshot 반영 확인

### 예제 14

- 원문: Play KnoWay Out this week.
- 이전 실제 gta-ko-2 출력: 이번 주 KnoWay Out 플레이
- 이번 실제 최종 출력: 이번 주 KnoWay Out 플레이
- 검증: PASS, Snapshot 반영 확인

### 예제 15

- 원문: Community Race Series: "my strange creator job"
- 이전 실제 gta-ko-2 출력: 커뮤니티 레이스 시리즈: "my strange creator job"
- 이번 실제 최종 출력: 커뮤니티 레이스 시리즈: "my strange creator job"
- 검증: PASS, Snapshot 반영 확인

### 예제 16

- 원문: Get Bravado Banshee GTS for free.
- 이전 실제 gta-ko-2 출력: Bravado Banshee GTS을(를) 무료로 획득하세요.
- 이번 실제 최종 출력: Bravado Banshee GTS를 무료로 획득할 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 17

- 원문: Available through September 10 at 15:00.
- 이전 실제 gta-ko-2 출력: September 10 15:00까지 이용할 수 있습니다.
- 이번 실제 최종 출력: September 10 15:00까지 이용할 수 있습니다.
- 검증: PASS, Snapshot 반영 확인

### 예제 18

- 원문: GTA+ Member: Claim Body Armor for free.
- 이전 실제 gta-ko-2 출력: GTA+ 회원: 방탄복을(를) 무료로 획득하세요.
- 이번 실제 최종 출력: GTA+ 회원: 방탄복을 무료로 획득
- 검증: PASS, Snapshot 반영 확인

### 품질 판정

최종 통제 예제에서 숫자 손상 0, 승인 용어 위반 0, 보호 토큰 실패 0, 보호 고유명 발명 0, 기계적 이중 조사 0, 알려진 영문 기간 단위 누출 0, RP을 같은 알려진 약어 조사 오류 0이었다. 18필드 모두 Snapshot에 반영됐다.

사무소와 튜닝 샵, 코카인 제조 아지트와 필로폰 제조실처럼 문장의 연결도 자연스러워졌다. September 10 날짜 표기는 보호된 원문으로 남는다. 이번 대상인 숫자 기간 단위와는 다르며 날짜 전체 현지화로 범위를 확대하지 않았다. GTA+ 보상 예제의 명사형 마무리는 문체 선호의 영역이다.

## 5. Validator 변경

기간/배수는 LocalizationQuantity의 Value / Unit / Display로 분리한다. 예를 들어 90 minutes는 Value=90, Unit=minute, Display=90분이며 1.5X는 Value=1.5, Unit=multiplier, Display=1.5배다.

Gemini가 반환하는 것은 여전히 보호 토큰이다. 숫자나 단위를 모델이 직접 다시 작성하도록 허용하지 않는다. 보호 토큰의 단일 출현을 검증한 뒤 애플리케이션이 원래 의미값의 한국어 표시를 복원한다. 따라서 영문 단위의 문자 일치를 요구하지 않으면서도 숫자값이 바뀔 수 없다.

- 90 minutes 입력의 정상 보호 토큰 응답 → 90분: PASS.
- 같은 입력에서 60분으로 바꾼 응답: FAIL.
- 3X를 2배로, 1.5X를 15배로 바꾼 응답: FAIL.
- 보호된 기간/배수 뒤에 모델이 시간/분/배 같은 단위를 추가: FAIL.
- 누락·중복·변형·항목 간 토큰 이동 및 새 숫자 삽입: 기존처럼 FAIL.
- 교정할 수 없는 기계적 조사가 일반 문법에 남음: FAIL.

검증은 모든 자연어 의미 관계를 증명하지 않는다. 숫자를 보존하면서 다른 조건이나 행위 관계를 잘못 표현하는 문제는 기존처럼 사람 검토가 필요하다.

## 6. Gemini E2E

GEMINI_API_KEY: 감지됨. 값/일부/길이/해시를 출력하지 않았다.

- 모델: gemini-3.5-flash-lite 유지. 더 비싼 모델로 변경하지 않았다.
- 이번 작업 실제 생성 호출: **총 5회**.
  - gta-ko-3 최초 실행 2회(초기+허용된 교정): 검증 거절.
  - gta-ko-3 응답 관찰 실행 2회: RP와 이번 주 보호 토큰을 직접 문자열로 바꾼 문제 확인, 검증 거절.
  - 최종 gta-ko-3.1 실행 1회: 18필드 검증 PASS, 추가 교정 없음.
- 위 네 번의 거절 응답은 HTTP 생성 자체는 성공했지만 번역으로 채택되지 않았다. 실패 사실을 숨기지 않는다.
- 최종 HTTP 지연: **3,950ms**, 파이프라인 완료: **4,039ms**.
- 최초 회차: 요청 1, 캐시 적중 100, 진행 중 요청 병합 1.
- 동일 저장 파일로 재시작: 요청 0, 캐시 적중 102, 처리 37ms.
- 최종/실패 회차 모두 로컬 healthz 200. 실패 결과는 저장·Snapshot 번역에 채택되지 않았다.
- 시간은 단일 통제 측정이며 성능 벤치마크의 평균값이 아니다.

실제 Backend host + 기존 정규화/소스 정책/의미 추출/공급자/검증/영속화/Snapshot 경로를 실행했다. Discord gateway만 검증 대체물이며, 검증 도구의 Discord 명령어 정리 worker도 제외해 불필요한 외부 명령 정리 시도를 하지 않게 했다. 정상 Backend 등록/동작은 변경하지 않았다.

최종 증거: [평가 결과](../../artifacts/localization/surface-v31/evaluation.json), [보호 입력·실제 모델 응답](../../artifacts/localization/surface-v31/provider-observations.json).
앞선 거절 기록은 surface-v3 및 surface-v3-observed 디렉터리에 구분해 남겼다. 이전 평가 파일을 덮어쓰지 않았다.

## 7. 최신 실제 릴레이 검증

**최신 실제 릴레이 E2E: 미검증**

현재 프로세스, Windows User, Machine 범위에 LSO_DISCORD_BOT_TOKEN이 없다. 기존 Backend 설정은 이 환경 변수를 요구하고 로컬 실행 도구는 보안 입력으로 받아 해당 프로세스에만 적용하는 경로다.

사용자에게 토큰을 붙여넣으라고 요청하지 않았고, 다른 프로세스/자격증명 저장소에서 비밀을 추출하지 않았다. Railway 자격증명/환경 변수를 조회하거나 설정을 바꾸지 않았다. 실제 최근 신뢰 메시지를 안전하게 확보할 수 없어 실소스 PASS를 만들지 않았다.

## 8. 테스트

- 집중 Localization 테스트: **596 PASS**(기존 510 + 새 표면 규칙 86).
- 신규 조사/기간/배수/고유명/공격적 응답 테스트: **86 PASS**.
- 신뢰 소스/개인정보/공급자/기존 validator: 기존 Localization 510개 및 전체 회귀에 포함, PASS.
- Backend 명칭 테스트: 전체 회귀 내 **545 PASS**.
- Clean Release build: **PASS, 경고 0, 오류 0**.
- 이번 최종 전체 Release suite: **1회 실행, 2,409 PASS / 실패 0 / 건너뜀 0**, 약 46초.
- dotnet format --verify-no-changes --no-restore: PASS.
- git diff --check: PASS.
- WPF self-contained win-x64 single-file publish: PASS.
- 실제 WPF 화면/GTA 실행 검증: 미실행.

집중 테스트 첫 실행에서 새 테스트가 완전 일치 용어 LSD 제조실 판매 임무를 두 토큰으로 잘못 가정해 실패했다. 실제 longest-match 구현을 바꾸지 않고 테스트를 올바른 단일 토큰 기준으로 수정했으며 이후 집중/전체 회귀 모두 통과했다.

검증 EXE:
`E:\Codex\Worktrees\Gacha_Overlay-gta-ai-localization\artifacts\localization\surface-client-publish\GachaOverlay.App.exe`

이 EXE만 실행한다고 Production Backend 번역 구현이 변경되는 것은 아니다. Backend 배포는 하지 않았다.

## 9. 변경 파일

이번 후속 작업에서 변경/추가한 파일만 아래에 열거한다. 이전 구현의 미커밋 변경은 그대로 보존했다.

- `src/GachaOverlay.Core/Gta/Localization/KoreanLocalizationSurface.cs`
- `src/GachaOverlay.Core/Gta/Localization/LocalizationProtection.cs`
- `src/LSOverlay.Backend/Gta/Localization/GeminiGtaLocalizationProvider.cs`
- `src/LSOverlay.Backend/Gta/Localization/GtaLocalizationService.cs`
- `tests/GachaOverlay.Tests/KoreanLocalizationSurfaceTests.cs`
- `tools/dev/LocalizationProbe/Program.cs`
- `docs/localization/gta-ai-localization-validation.md`

기존 baseline 감사, 459개 용어집, Backend 소스 정책, Remote Protocol, Sales/Chat/WPF UI/타이머는 이번 후속 교정에서 변경하지 않았다.

## 10. 보안 / 개인정보

허용 Channel ID 1417898156187713577 AND Author ID 1417898538385539085 정책은 그대로다. 중첩 전달 콘텐츠는 AI 입력에서 제외한다. 다른 Discord 채널/작성자는 허용하지 않는다.

통제 검증 도구의 원본 응답 보존은 고정된 공개 예제에만 적용되며 정상 Backend에 원문 로깅을 추가하지 않았다. API 키는 전용 공급자 내부에서만 사용된다.

소스/문서/영속 데이터/통제 응답/게시 EXE 등 26개 파일 검사에서 실제 API 키 값 발견 0. 이는 검사한 파일 범위의 결과이지 시스템 전체 비밀정보 검사를 뜻하지 않는다.

## 11. Git

**UNCOMMITTED / UNPUSHED**.

git add/commit/push/merge/tag/release/deploy/Railway 변경 없음. 인덱스 비어 있음.
HEAD/main/v2.3.0은 3e4a12434b6790752024c984dde71addaaf719cf로 유지했다.

## 12. 다음 권장 단계

**사용자 번역 품질 최종 승인 후 커밋 및 Staging 준비 가능.**

이번 표면 품질 수락 기준은 통제된 실제 Gemini 출력에서 통과했다. 최신 실제 릴레이 수집부터의 검증은 별도로 남으며, 승인 후 준비 단계에서 기존 안전한 자격증명/소스 경로를 사용해 확인해야 한다. 현재 배포 또는 운영 검증 완료로 판정하지 않는다.
