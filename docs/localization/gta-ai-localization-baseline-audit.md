# GTA AI 현지화 — 승인 전 기준 감사 기록

> 이 문서는 입력 정책 승인 전의 역사적 감사 기록이다. 후속 승인으로 차단 사유가 해소되었으며,
> 현재 구현 결과는 `gta-ai-localization-validation.md`를 참고한다.

## 1. 상태

PARTIAL — 기존 경로 감사 완료, 외부 AI 입력 출처 정책 확인 필요.
Gemini 파이프라인 구현 완료 보고가 아니다. 2026-09-08 기준.

## 2. Branch / Worktree

- 기준: `v2.3.0`, `3e4a12434b6790752024c984dde71addaaf719cf`.
- 브랜치: `feat/gta-ai-localization`.
- 작업트리: `E:\Codex\Worktrees\Gacha_Overlay-gta-ai-localization`.
- 원래 저장소의 수정 파일과 미추적 보고서를 보존했다.
- 신규 작업트리를 기준 태그에서 생성했다. 이 감사 문서만 새로 추가했다.

## 3. 기존 번역 구조

경로는 다음과 같다. 경로는 저장소 루트 기준이다.

1. `src/LSOverlay.Backend/Gta/DiscordNetGtaEventSource.cs`:
   지정 Guild의 이벤트 채널에서 최근 최대 100개 메시지를 읽는다.
   `Build`는 Discord 본문, 임베드 제목·설명·필드 및 전달 스냅샷을 수집한다.
   원문 URL을 별도로 검증하거나 공개 웹 문서를 취득하는 경로는 없다.
2. `src/GachaOverlay.Core/Gta/CanonicalEventDocumentBuilder.cs`:
   전달 내용, 본문, 임베드를 정규화·중복 제거한다.
   최대 64블록, 블록당 2,048자, 합계 16KiB 문자 한도를 적용한다.
   NFKC, 줄바꿈, 일부 마크다운·공백 정규화이며 개인정보 제거기는 아니다.
3. `GtaEventClassifier.cs`:
   주간 제목·앵커·문구 및 게시자 문자열로 Weekly/Campaign/Candidate 등을 판정한다.
   `HasTrustedSource`는 서명된 출처 검증이나 공개 접근성 검증이 아니다.
   강한 본문 구조만으로 Weekly로 분류되는 경로도 있다. GTA+ 공지는 현재 제외한다.
4. `GtaEventParser.cs`, `GtaEventModels.cs`:
   Week/Challenge/EventItem/Campaign을 구성한다.
   `OriginalLabel`, `OriginalText`, `Theme`, `Target`, 요구·보상·캠페인 문자열은
   Discord에서 추출된 자유 텍스트를 포함한다. DTO 필드만 골라도 공개 출처가 입증되지는 않는다.
   Item/Challenge/Campaign key는 의미 자료 SHA-256의 선두 10바이트를 사용하며,
   WeekKey는 날짜 기반이다. 전체 번역 입력의 revision hash는 별도로 없다.
5. `GtaEventResolver.cs`:
   ActiveWeek/StagedWeek/관련 캠페인을 관리한다. 동일성은 JSON 비교를 사용한다.
   목요일 18:00 KST 전환과 준비 구간을 유지하며 오래된 주를 현재 주처럼 표시하지 않는다.
6. `src/LSOverlay.Backend/Gta/JsonGtaEventStore.cs`:
   StateDirectory의 `gta-companion-events.json`에 신뢰 이벤트 의미 상태를 저장한다.
   임시 파일·원자적 교체·`.bak` 복구가 있다. AI 번역 저장소는 아니다.
   생성 시 복구하고 새 신뢰 이벤트 및 주간 전환 때 저장한다.
7. `GtaKoreanFormatter.cs`, `GtaTranslationGlossary.cs`, `TranslationGlossary.ko.json`:
   임베디드 용어집 22항목, 버전 `2.3-1`을 사용한다.
   긴 용어 우선·단어 경계·별칭 매칭 뒤 정규식으로 일부 문장을 정리한다.
   규칙 버전은 `ko-2.3-1`; 용어집 내용 해시를 캐시 버전에 포함한다.
   프로세스 내 최대 256문자열 캐시만 있으며 재시작 후 번역 캐시는 유지되지 않는다.
   숫자 순서와 일부 보호 토큰을 검사해 실패 시 검증 가능한 대안 또는 원문으로 돌아간다.
   Gemini, 수동 번역 override, 영속 Translation Memory는 없다.
8. `src/LSOverlay.Backend/Gta/GtaEventService.cs`:
   서버의 singleton 이벤트 상태로부터 번역된 Snapshot을 생성한다.
   비교 서명은 Revision/GeneratedAt을 제외한 JSON이며, 변경 시 런타임 revision을 올린다.
   수집 실패·삭제는 기존 신뢰 상태를 지우지 않는다.
9. `DiscordGatewayAdapter.cs` 및 `GtaEventResetWorker`:
   Ready와 GuildAvailable에서 hydration을 요청하며 최소 2분 간격과 동시 실행 차단이 있다.
   새 메시지·수정 이벤트를 처리하고, 1분 타이머는 시간 전환을 확인한다.
   매분 전체 이력을 다시 읽는 구조가 아니다.
10. `BackendWebSocketSession.cs` → `LSOverlayRemoteClient.cs` → `GtaCompanionViewModel.cs`:
    `gta_companion_v1` 지원 클라이언트에 현재 Snapshot과 후속 변경을 전달한다.
    클라이언트는 revision을 확인하고 `DisplayTextKo` 등 서버 결과를 표시한다.
    따라서 외부 AI는 클라이언트 요청 경로가 아니라 서버의 신뢰 상태 변경 이후
    비동기 번역 단계에 붙이는 것이 적절하다. 기존 전송 구조를 유지할 여지가 있다.

## 4. Gemini 통합 구조

미구현. 소스 출처 정책 확인 전 모델 선택·Google API 호출·패키지 추가를 하지 않았다.
기존 경로의 자유 텍스트를 좁은 DTO로 옮기는 것만으로는 개인정보 경계가 성립하지 않는다.

## 5. Glossary

새 승인 용어집은 아직 적용하지 않았다. 기존 22항목과 동작을 유지했다.
사용자가 승인한 새 용어·정책은 입력 경계 확정 후 구조화해야 한다.

## 6. 번역 보호 / 검증

새 placeholder·수치·schema·수동 override 검증 및 repair retry는 미구현이다.
기존 번역 무결성 검사는 그대로다. 새 기능의 검증 통과를 주장하지 않는다.

## 7. Cache / Translation Memory / Last-Good

기존 이벤트 Last-Good와 메모리 캐시를 변경하지 않았다.
향후 번역 identity에는 전체 허용 입력 hash와 용어집·규칙·prompt·모델 정책 버전이 필요하다.
기존 ItemKey만으로 번역을 캐시하면 원문 수정 감지가 불완전할 수 있다.

## 8. 실제 Gemini 검증

미검증 — 현재 실행 프로세스에서 `GEMINI_API_KEY` 미감지.
API 호출 0회. 값이나 일부 문자는 조회·출력·저장하지 않았다.
키 미감지 자체가 구현 중단 이유는 아니다. 중단 이유는 아래 입력 출처 정책이다.

## 9. 번역 품질 비교

Gemini 출력이 없으므로 비교 미실시. 기존 fixture는 분류 회귀 테스트 자료이지
실시간 공개 공지의 출처 증명이나 Gemini 품질 평가 결과가 아니다.

## 10. 보안 / 개인정보

요청은 공개 GTA 필드만 허용하면서 Discord 메시지 내용은 전송 금지한다.
현행 경로는 Discord 이벤트 채널 본문·임베드·전달 내용을 유일한 동적 원문으로 사용한다.
정규화와 분류를 거쳐도 텍스트 출처는 바뀌지 않으며, 별도 공개 출처 인증이 없다.
따라서 현재 모델을 그대로 Gemini에 연결하지 않았다. 외부 데이터 전송은 없다.

확정이 필요한 선택은 다음 두 방향이다.

- Discord 전송 금지를 엄격히 유지: 허용된 공개 웹 원문 또는 운영자가 검수한 공개 자료를
  입력으로 제공하는 경로가 필요하다. 현행 실시간 공지와 연결할 출처·검증 방식도 정해야 한다.
- 공개 공지에 대한 제한적 예외 승인: 승인된 공개 GTA 공지의 허용 필드만 전송하도록
  출처 검증을 추가한다. 일반 채팅·개인 정보·ID·Sales는 계속 차단한다.
  단순히 이벤트 채널에 있다는 이유만으로 모든 본문을 허용해서는 안 된다.

어느 방향도 사용자 확인 없이 기존 금지 규칙의 예외로 간주하지 않았다.

## 11. 테스트

- 빌드·focused·Backend·full·format·publish: 미실행, 제품 코드 미변경.
- Git 기준 태그/HEAD/main 일치 확인 및 diff check 수행.
- 새 현지화 구현에 대한 자동 검증 완료 상태가 아니다.

## 12. 변경 파일

- `docs/localization/gta-ai-localization-validation.md` — 이 감사 문서만 추가.

## 13. Railway에 필요한 설정

구현 및 사용자 검토 후 `GEMINI_API_KEY`가 필요하다.
`GEMINI_MODEL` 지원은 요청 사항이며 아직 구현하거나 기본 모델을 확정하지 않았다.
스테이징/운영에는 별도 자격증명을 사용하는 방향을 권장한다.
이번 작업에서 Railway 조회·변수 변경·배포는 수행하지 않았다.

## 14. Git 상태

UNCOMMITTED / UNPUSHED. git add/commit/push/merge/tag/release를 실행하지 않았다.
main/v2.3.0/공개 자산과 운영·스테이징은 변경하지 않았다.

## 15. 보류 / 한계

공개 GTA 공지와 Discord 메시지 전송 금지 사이의 허용 경계 확인이 필요하다.
이 상태를 생산 준비 완료나 Gemini 품질 PASS로 제시할 수 없다.

## 16. 다음 권장 단계

외부 전송 가능한 공개 공지의 출처 정책을 확정한 뒤 동일 작업트리에서 구현을 재개한다.
