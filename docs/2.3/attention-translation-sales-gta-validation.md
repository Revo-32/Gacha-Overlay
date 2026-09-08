# LS Overlay 2.3 — 통합 UX / 알림 / 번역 / 판매 / GTA 감지 검증 보고서

작성일: 2026-09-08. 사용자 실제 PC 검증 전, 로컬 검증 후보에 대한 기록입니다.

후속 사용자 UI 피드백은 [알림/멘션/판매 UI 후속 수정](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/docs/2.3/attention-ui-corrective.md)을 참고하세요. 아래 내용과 EXE/테스트 결과는 첫 후보의 기록이며, 알림 위치·불투명도·확대와 접힌 판매 바 구성은 후속 수정에서 변경되었습니다.

## 1. 상태

**PASS — 자동 검증 및 사용자 검증 후보 생성 기준.**

실제 GTA 실행/종료, Discord 실시간 상호작용, 다중 모니터 DPI 이동은 **미검증**입니다. 출시 승인이나 운영 반영 완료를 의미하지 않습니다.
사용자 승인에 따라 생산 타이머 정책을 **로컬 GTA 실행 중 누적(스토리 모드 포함)**으로 변경했습니다.

## 2. Branch / Worktree

- 기준: 공개 Stable v2.2.0
- 기준 SHA: `5dde5fbfd3bd5a8ce0f6463d4a38772bb416dfb6`
- 기준 tree: `edd9a318e4634ac56b3740c064a184ea4e666100`
- 브랜치: `feat/2.3-attention-translation-sales-gta`
- 작업 폴더: `E:\Codex\Worktrees\Gacha_Overlay-2.3-attention`
- HEAD는 기준 SHA 그대로입니다. 변경 파일은 미스테이징, 새 파일은 미추적 상태입니다.
- 원본 `E:\Codex\Projects\Gacha_Overlay`의 기존 수정 파일과 로컬 보고서는 보존했습니다.
- main 및 v2.2.0이 가리키는 커밋은 변경하지 않았습니다.

## 3. Notification Center

- 기존 메인 HUD 안에 접이식 알림 패널을 추가했습니다. 새 상위 창이나 전용 전역 단축키는 없습니다.
- 이벤트 6종: 나를 직접 멘션, 내 메시지 답장, 다음 판매 차례, 현재 판매 차례, GTA 감지, GTA 감지 상실.
- 기존에 수신한 허용 메인 채팅과 신뢰 가능한 판매 상태, 로컬 프로세스 관측만 사용합니다.
- 저장 위치: `%LOCALAPPDATA%\GachaOverlay\attention-history.json`.
- 최신 30건만 보존합니다. ID/타입/시각/출처/문맥/최대 180자 평문 미리보기/읽음/우선순위만 저장하며 미디어나 전체 메시지 모델은 저장하지 않습니다.
- 파일 로딩은 비동기이며 128 KiB 입력 상한을 둡니다. 잘못된 JSON/버전/항목은 안전하게 무시합니다.
- 저장은 임시 파일과 원자적 교체를 사용하고 쓰기 요청을 병합합니다. 삭제된 텍스트가 백업 파일에 남지 않도록 별도 이력 백업을 만들지 않습니다.
- 시작 시 파일 로딩 전에 도착한 소유 계정 및 관측 이벤트는 순서대로 처리합니다. 별도 대기열도 최대 64개로 제한합니다.
- 채팅 MessageId를 한 알림의 정체성으로 사용하므로 직접 멘션과 답장이 겹쳐도 하나입니다. 편집 시 중요도와 미리보기를 갱신합니다.
- 판매는 원본 판매글 MessageId와 next/current 단계로 구분합니다. 초기 동기화와 provider handoff는 조용한 기준 상태로 처리합니다.
- GTA는 감지 여부 전이만 알립니다. 동일 폴링, Alt+Tab, 패널/설정 재열기, F9가 새 알림을 만들지 않습니다.
- **읽음 정책:** 개별 읽음 처리 또는 모두 읽음의 명시적 조작만 인정합니다. 패널을 열거나 항목이 그려졌다는 이유로 자동으로 읽음 처리하지 않습니다. 배지는 0이면 숨깁니다.
- 계정이 바뀌면 이전 계정의 채팅/판매 알림은 제거하고 로컬 GTA 알림만 유지합니다.
- 메시지 삭제는 알림 전체를 제거합니다. Latest20 밖에 있는 메시지도 허용된 실시간 UPDATE/DELETE 수신 경로에서 정리합니다. 원문 변경 정보가 불완전하면 오래된 미리보기를 남기지 않고 제거합니다.
- 잠금 상태에서는 패널을 닫고 입력을 차단합니다. 기존 창 click-through가 최종 권한이며, 패널 높이는 채팅 영역 안으로 제한해 판매 상태를 가리지 않도록 했습니다.
- Windows Toast 및 신규 알림음은 **보류**했습니다. 설치 등록/상주 도우미/셸 우회는 추가하지 않았습니다. 기존 판매·타이머 알림음 설정은 변경하지 않았습니다.

## 4. Mention 강조 정책

`DirectSelfMention > ReplyToSelf > EveryoneHere > Normal`

- 직접 멘션: 실제 인증 사용자 ID와 Mentions의 ID 일치. 가장 강한 강조와 알림.
- 답장: 기존 Remote 메시지에 포함된 참조 메시지 작성자의 신뢰 가능한 ID 일치. 중간 강조와 알림.
- everyone/here: 기존 명시적 메타데이터로 약한 강조만 적용. 알림 없음.
- 다른 사용자 멘션: 올바른 표시 이름은 유지하지만 일반 본문과 같은 강도로 표시.
- 정체성을 확인할 수 없으면 강조하지 않습니다. 닉네임 비교나 렌더링 문자열 정규식으로 자신을 추측하지 않습니다.
- 기존 메시지 시각 트리에 스타일과 의미 상태만 적용합니다. 2.2에서 제거한 동시 3중 밀도 트리를 다시 만들지 않았습니다.

## 5. GTA Companion 번역

### 기존 구조와 변경

기존 구조는 외부 번역 API가 아니라 Backend가 사용하는 Core의 `GtaKoreanFormatter`와 하드코딩된 용어 목록입니다.
원본 의미 상태의 Last-Good 저장은 기존 Backend 경로가 담당합니다.

이번에는 용어를 임베디드 JSON 용어집으로 분리했습니다. canonicalId, 원어/별칭, 한국어, 분류, 출처, 메모를 유지합니다.
검수 용어가 공식 용어보다 우선하며, 모르는 고유명사는 원어를 유지합니다. 알려진 단어의 부분 일치로 차량 이름을 훼손하지 않습니다.
기존 검수 용어를 모두 공식 명칭이라고 재분류하지 않았습니다. 공식 확인은 출처가 확인된 항목만 표시했습니다.

- 알려진 문장 패턴 개선: 임무 N회 완료, 레이스 N회 승리, 주간 도전, 완료 보상, 배수/할인 등.
- 숫자 순서, 금액 수치, 퍼센트, 배수, GTA$/RP/HSW 보호 검사.
- 빈 문자열, 깨진 문자 또는 보호 정보가 바뀐 결과는 유효한 이전 결과, 없으면 원문으로 되돌립니다.
- 검사는 보수적입니다. 숫자 순서를 바꾸는 자연스러운 번역도 원문으로 돌아갈 수 있습니다.
- 버전: 규칙 `ko-2.3-1`, 용어집 `2.3-1` 및 내용 해시. 메모리 캐시는 최대 256건이며 용어집 변경 시 다른 키를 사용합니다.
- 새 영구 번역 캐시나 외부 번역 서비스를 추가하지 않았습니다. 기존 원본 Last-Good 저장/주간 분류/활성화 정책은 유지합니다.

공식 명칭 확인 근거: [Rockstar 한국어 Newswire](https://www.rockstargames.com/kr/newswire/article/ak32aka841114k/the-overflod-suzume-supercar-and-new-safeguard-deliveries-now-in-gta-o)의 대적 모드 표기.
나머지 계승 용어는 검수 용어로 구분하며, 전체 GTA 사전의 공식 검증을 완료했다고 주장하지 않습니다.

**운영 제한:** 번역 결과는 Backend에서 생성됩니다. 이번 작업에서는 Backend/Production/Staging 배포를 하지 않았으므로 새 WPF EXE만 실행해도 운영 서버의 번역은 바뀌지 않습니다. 번역 실데이터 검증은 별도 승인된 서버 반영 이후에 필요합니다.

## 6. Sales UI / 원문 렌더링

- 현재 판매 차례 영역의 상하 여백을 늘리고 원문 요약을 함께 배치했습니다.
- 파싱 성공 여부와 무관하게 수신 원문 Content를 DetailSource로 유지합니다. 제품/상태 파서와 canonical 상품 계산은 그대로입니다.
- 일반 판매 영역은 최대 두 줄의 기존 inline 렌더링을 사용합니다. 상세에서는 이전 80 DIP 잘림 제한을 제거해 전체 원문을 기존 스크롤 안에서 볼 수 있습니다.
- 커스텀 이모지는 기존 Chat 토큰 → Sales 프레젠테이션 → 기존 미디어 자산/캐시 경로를 재사용합니다.
- 이미지 로딩 실패 시 `:name:`으로 표시하며 원문과 여러 이모지의 순서를 유지합니다.
- 파싱된 상품 요약은 보조 정보입니다. 파싱 실패가 원문을 사라지게 하지 않습니다.
- UPDATE는 변경 원문으로 갱신하고 DELETE는 제거합니다. 새로운 판매 변경 버튼이나 별도 미디어 캐시는 추가하지 않았습니다.

## 7. GTA 클라이언트 감지

### 확인한 기존 연결 문제

기존 GameForegroundMonitor는 GameForegroundOnly의 전경 창 감지용이며, Always 모드에서는 감시를 중지합니다.
한편 생산 타이머의 RemoteOnlinePlaytimeStatusSource는 **선택한 세션 호스트의 Discord 상태**를 사용했습니다.
따라서 내 PC의 GTA 실행과 무관한 호스트 상태가 내 생산 타이머를 제어할 수 있는 연결 문제가 코드에서 확인됐습니다.

이는 확인된 기존 동작 및 실패 가능 경로입니다. 과거 사용자 PC에서 발생한 모든 미감지 현상의 원인이라고 단정하지 않습니다.

### 새 감지 경로

- 허용 이름: `GTA5`, `GTA5_Enhanced`만 사용합니다. 런처/창 제목 유사 문자열로 추측하지 않습니다.
- 시작 즉시 로컬 프로세스를 조회하여 GTA가 먼저 실행돼 있던 경우도 처리합니다.
- 별도의 1초 주기 reconciliation과 선택한 프로세스의 Exited 이벤트를 사용합니다.
- ProcessRunning(실행), Foreground(전경 PID), ValidTrackedProcess(추적 유효성)를 분리합니다.
- PID + 시작 시각 + generation으로 재시작을 구분하고 이전 세대의 종료 콜백을 무시합니다.
- 한 번의 조회 실패/미관측은 유지합니다. 2회 연속 미관측으로 상실을 확정합니다.
- Exited 이벤트는 즉시 반영하며, 이벤트를 사용할 수 없는 경우 정상 스케줄 기준 약 1~2초 내 폴링이 상실을 확인합니다. 이는 실시간 보장치가 아니며 Dispatcher가 막히면 UI 반영이 늦어질 수 있습니다.
- 생산 타이머 UI는 기존 1초 갱신 주기를 유지합니다.
- 조회용 Process는 매번 폐기하며 선택 프로세스의 종료 구독/핸들/타이머는 교체 및 앱 종료 시 해제합니다.
- 게임 메모리, 패킷, WMI 상시 감시, 주입은 사용하지 않습니다.

### 생산 타이머와 표시

- 사용자가 승인한 정책: **GTA 실행 중 누적, 스토리 모드 포함**.
- Alt+Tab은 누적을 멈추지 않습니다. 원격 세션 호스트의 온라인 여부가 로컬 생산 누적을 덮어쓰지 않습니다.
- 벙커는 기존 보급품 채움 조작으로 시작/재시작하며 기존 기간과 SharedTimerRegistry를 재사용합니다.
- 실행 종료/지속 미확인 시 일시 정지하고 재감지 시 다시 누적합니다. 기존 벽시계 기반 쿨다운/기간은 바꾸지 않았습니다.
- 컴패니언에 감지/미감지 표시와 설명 tooltip을 추가했습니다. 생산 타이머가 있을 때 미감지를 경고색으로 표시합니다.
- 실제 온라인/스토리 모드 판별은 하지 않습니다. Session HUD의 선택 호스트 인원 표시도 기존 원격 세션 정보 그대로입니다.
- GameForegroundOnly는 기존 전경 감지 경로를 유지합니다. Always 모드의 생산 타이머는 그 감시 여부에 의존하지 않습니다.

### 검증과 제한

가짜 프로세스/시계로 선실행·후실행·종료·재시작·이전 콜백·Alt+Tab·일시 실패·동일 관측·알림·벙커 누적/일시 정지/재개를 검증했습니다.
실제 GTA 프로세스, 권한 차이, 절전 복귀, 다중 모니터에서의 장시간 검증은 **미검증**입니다.
프로세스 이름만으로 실행 정체성을 확인하는 기존 허용 정책이며, 온라인 세션 상태를 증명하는 기능은 아닙니다.

## 8. Security

- Discord 사용자 토큰, self-bot, 클라이언트 Bot Token/OAuth Secret을 추가하지 않았습니다.
- 인증·권한·Remote wire protocol·OAuth·TLS·서버 설정은 변경하지 않았습니다.
- 기존 허용 채널 수신 데이터만 사용하며 추가 채널 구독을 하지 않습니다.
- GTA/Discord 메모리 읽기, DLL/게임 주입, 패킷 가로채기, 브라우저 자동화를 사용하지 않았습니다.
- 새 로그는 이력 저장 실패의 일반 상태만 기록하며 토큰/인증 정보/메시지 본문을 기록하지 않습니다.
- 알림 저장은 메타데이터 평문입니다. OS 계정의 로컬 프로필 보호를 이용하며 별도 암호화를 주장하지 않습니다.
- 삭제 알림 원문은 이력 및 임시 저장 경로에서 최소화합니다. 기존 미디어/자격 증명 저장 설계는 그대로입니다.

## 9. 성능 / 리소스 영향

- 알림 30건, 평문 미리보기 180자, 시작 관측 대기 64개, 읽기 128 KiB 상한.
- 용어집 최대 512개, 별칭 항목별 최대 16개, 번역 입력 최대 16,384자, 결과 캐시 최대 256개.
- 알림에는 Bitmap/GIF/frame/원본 메시지 모델을 보관하지 않습니다.
- Chat 밀도 렌더러, TextFormatter 재사용, 미디어 재생/자산 캐시 소유권을 유지합니다.
- 프로세스 조회는 1초 간격 백그라운드 작업이며 동일 상태에는 새 알림/파일 쓰기를 만들지 않습니다.
- 구조적 상한과 기존 회귀 테스트는 확인했습니다. **실제 로그인 상태의 2.2 대비 CPU/RAM A/B 수치나 장시간 핸들 누수 수치는 측정하지 않았습니다.** 성능 향상을 주장하지 않습니다.
- 기존 선택적 프로파일링 테스트의 성공을 실제 사용자 워크로드 측정으로 해석하지 않습니다.

## 10. 테스트

| 항목 | 결과 |
|---|---|
| Release Rebuild | PASS, 경고 0 / 오류 0 |
| 집중 테스트 | PASS, 115 / 115, 실패 0 / 건너뜀 0 |
| 전체 Release 테스트 | PASS, 1,810 / 1,810, 실패 0 / 건너뜀 0 |
| 서식 검사 | PASS |
| git diff --check | PASS |
| self-contained win-x64 publish | PASS |
| 기존 설정/상태 호환 | 스키마 변경 없음, 기존 migration/persistence 회귀 통과 |
| 실제 사용자 프로필로 새 앱 실행 | 미검증 |
| 실제 GTA/Discord/다중 모니터 조작 | 미검증 |

실행 명령:

```powershell
dotnet build GachaOverlay.sln -c Release -t:Rebuild --no-restore
dotnet test GachaOverlay.sln -c Release --no-build --filter 'FullyQualifiedName~Attention23|FullyQualifiedName~GtaDetection23|FullyQualifiedName~Translation23|FullyQualifiedName~SalesRendering23|FullyQualifiedName~M98SessionHudAndSalesNotificationTests' --logger 'trx;LogFileName=focused-23-final.trx' --results-directory artifacts/validation-2.3-attention/tests
dotnet test GachaOverlay.sln -c Release --no-build --logger 'trx;LogFileName=full-23-final.trx' --results-directory artifacts/validation-2.3-attention/tests
dotnet format GachaOverlay.sln --verify-no-changes --no-restore
git diff --check
dotnet publish src/GachaOverlay.App/GachaOverlay.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/validation-2.3-attention/win-x64
```

[최종 집중 TRX](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/tests/focused-23-final.trx)
· [최종 전체 TRX](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/tests/full-23-final.trx)

시각 검증:

- 실제 WPF 컨트롤 + 합성 데이터로 5개 테마 × 3개 밀도 × 3개 출력 배율 = 45개 이미지 생성.
- 실제 HUD의 배지 0/1/여러 개, 열린 패널, 잠금/투명도 0, 컴패니언 3상태 등 7개 추가 이미지. 총 52개.
- 대표 테마/밀도/배율 이미지를 직접 검토했습니다. 판매 영역과 알림 겹침, 잠금 입력 차단은 자동 assertion도 포함합니다.
- 100/125/150%는 RenderTargetBitmap 출력 배율 검증입니다. **실제 Per-monitor DPI 전환 검증은 아닙니다.**
- 기존 golden 이미지는 교체하지 않았습니다.
- [통합 HUD](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/visual/hud-notifications-open.png)
  · [GitHub Dark](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/visual/GitHubDark-Balanced-1.00.png)
  · [Nord](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/visual/Nord-Compact-1.25.png)
  · [GTA 미감지/타이머 주의](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/visual/companion-active-lost.png)

중간 검증에서 용어집 리소스의 .ko 문화권 처리와 WPF 합성 테스트 리소스 로딩, 시작 알림 순서 문제를 발견하고 수정했습니다.
이전 실패 TRX는 삭제하지 않고 보존했으며 최종 결과와 구분합니다.
기존 M911RemoteOnlyRetirementTests의 줄바꿈 3곳도 format 요구에 맞춰 정규화했습니다. 테스트 의미 변경은 없으며 Git LF 정규화 후의 실질 diff는 없습니다.

## 11. 변경 파일

소스/테스트 변경 40개와 이 보고서 1개입니다. 아래는 정확한 경로입니다.

- [docs/2.3/attention-translation-sales-gta-validation.md](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/docs/2.3/attention-translation-sales-gta-validation.md)
- [src/GachaOverlay.App/Lifecycle/ApplicationHost.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Lifecycle/ApplicationHost.cs)
- [src/GachaOverlay.App/Presentation/BusinessManagerViewModel.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/BusinessManagerViewModel.cs)
- [src/GachaOverlay.App/Presentation/ChatMessageView.xaml](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/ChatMessageView.xaml)
- [src/GachaOverlay.App/Presentation/ChatMessageViewModel.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/ChatMessageViewModel.cs)
- [src/GachaOverlay.App/Presentation/CrispOutlinedText.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/CrispOutlinedText.cs)
- [src/GachaOverlay.App/Presentation/FoundationWindow.xaml](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/FoundationWindow.xaml)
- [src/GachaOverlay.App/Presentation/GtaCompanionViewModel.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/GtaCompanionViewModel.cs)
- [src/GachaOverlay.App/Presentation/GtaCompanionWindow.xaml](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/GtaCompanionWindow.xaml)
- [src/GachaOverlay.App/Presentation/HudShellViewModel.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/HudShellViewModel.cs)
- [src/GachaOverlay.App/Presentation/HudWindow.xaml](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/HudWindow.xaml)
- [src/GachaOverlay.App/Presentation/HudWindow.xaml.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/HudWindow.xaml.cs)
- [src/GachaOverlay.App/Presentation/NotificationCenterView.xaml](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/NotificationCenterView.xaml)
- [src/GachaOverlay.App/Presentation/NotificationCenterView.xaml.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/NotificationCenterView.xaml.cs)
- [src/GachaOverlay.App/Presentation/NotificationCenterViewModel.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/NotificationCenterViewModel.cs)
- [src/GachaOverlay.App/Presentation/SalesQueueView.xaml](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/SalesQueueView.xaml)
- [src/GachaOverlay.App/Presentation/SalesQueueViewModel.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Presentation/SalesQueueViewModel.cs)
- [src/GachaOverlay.App/Services/GtaProcessMonitor.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Services/GtaProcessMonitor.cs)
- [src/GachaOverlay.App/Services/RemoteOnlinePlaytimeStatusSource.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Services/RemoteOnlinePlaytimeStatusSource.cs)
- [src/GachaOverlay.App/Services/SalesTurnNotification.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.App/Services/SalesTurnNotification.cs)
- [src/GachaOverlay.Core/Attention/NotificationHistory.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Attention/NotificationHistory.cs)
- [src/GachaOverlay.Core/Chat/ChatAttention.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Chat/ChatAttention.cs)
- [src/GachaOverlay.Core/Chat/ChatPresentation.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Chat/ChatPresentation.cs)
- [src/GachaOverlay.Core/Discord/Messages/DiscordMessagePipeline.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Discord/Messages/DiscordMessagePipeline.cs)
- [src/GachaOverlay.Core/Discord/Messages/DiscordRemoteMessageMetadata.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Discord/Messages/DiscordRemoteMessageMetadata.cs)
- [src/GachaOverlay.Core/GachaOverlay.Core.csproj](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/GachaOverlay.Core.csproj)
- [src/GachaOverlay.Core/Gta/GtaEventVocabulary.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Gta/GtaEventVocabulary.cs)
- [src/GachaOverlay.Core/Gta/GtaKoreanFormatter.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Gta/GtaKoreanFormatter.cs)
- [src/GachaOverlay.Core/Gta/GtaTranslationGlossary.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Gta/GtaTranslationGlossary.cs)
- [src/GachaOverlay.Core/Gta/TranslationGlossary.ko.json](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Gta/TranslationGlossary.ko.json)
- [src/GachaOverlay.Core/Hud/Game/GtaProcessTracker.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Hud/Game/GtaProcessTracker.cs)
- [src/GachaOverlay.Core/Sales/SalesStateEngine.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Core/Sales/SalesStateEngine.cs)
- [src/GachaOverlay.Infrastructure/Attention/JsonNotificationStore.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/GachaOverlay.Infrastructure/Attention/JsonNotificationStore.cs)
- [src/LSOverlay.RemoteClient/RemoteChatIngressAdapter.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/src/LSOverlay.RemoteClient/RemoteChatIngressAdapter.cs)
- [tests/GachaOverlay.Tests/Attention23Tests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/Attention23Tests.cs)
- [tests/GachaOverlay.Tests/GtaDetection23Tests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/GtaDetection23Tests.cs)
- [tests/GachaOverlay.Tests/Presentation/Attention23VisualTests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/Presentation/Attention23VisualTests.cs)
- [tests/GachaOverlay.Tests/Presentation/M98SessionHudAndSalesNotificationTests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/Presentation/M98SessionHudAndSalesNotificationTests.cs)
- [tests/GachaOverlay.Tests/Sales/M10SalesNormalizationTests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/Sales/M10SalesNormalizationTests.cs)
- [tests/GachaOverlay.Tests/SalesRendering23Tests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/SalesRendering23Tests.cs)
- [tests/GachaOverlay.Tests/Translation23Tests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/Translation23Tests.cs)

별도 로컬 줄바꿈 정규화만 수행한 파일:
- [tests/GachaOverlay.Tests/Architecture/M911RemoteOnlyRetirementTests.cs](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/tests/GachaOverlay.Tests/Architecture/M911RemoteOnlyRetirementTests.cs)

빌드/화면/TRX 산출물은 무시된 `artifacts/validation-2.3-attention` 아래에 있으며 Git에 추가하지 않았습니다.

## 12. 검증 EXE

[사용자 검증 EXE](E:/Codex/Worktrees/Gacha_Overlay-2.3-attention/artifacts/validation-2.3-attention/win-x64/GachaOverlay.App.exe)

```text
E:\Codex\Worktrees\Gacha_Overlay-2.3-attention\artifacts\validation-2.3-attention\win-x64\GachaOverlay.App.exe
```

기존 오버레이를 트레이에서 완전히 종료한 뒤 위 파일을 실행하세요.
**EXE만 떼어 옮기지 말고 win-x64 폴더 전체를 유지해야 합니다.**

.NET/Windows Desktop 런타임을 포함한 self-contained 폴더입니다. 공개 배포 ZIP은 만들지 않았습니다.
출시 버전 올리기는 범위 밖이므로 앱의 버전 표기는 기존 2.2.0을 유지할 수 있습니다. 위 경로로 후보를 구분하세요.
기존 기본 Remote endpoint와 사용자 프로필을 그대로 사용하며, 이번 작업 중에는 이 EXE를 실제 계정으로 실행하지 않았습니다.

- EXE SHA256: `E443630006211664D2576126311B5D5FF16274160B3B2F80811E04BE028C9355`
- App DLL SHA256: `5C6A8D0D7CE88DF2201ACACE9E3CA6D865A3836C66A1F1F173771683D16401D6`

## 13. 사용자 확인 항목

아래 모든 실제 사용자 시나리오는 현재 **미검증**입니다.

### 알림 / 멘션

1. 다른 사람 멘션에 강한 강조가 없는지.
2. 나를 직접 멘션하면 가장 강하게 표시되는지.
3. 직접 멘션 알림이 1건 생성되는지.
4. 내 메시지에 대한 답장이 중간 강도로 표시되는지.
5. 답장 알림이 생성되고 직접 멘션과 겹치면 1건만 남는지.
6. everyone/here가 약하게만 강조되는지.
7. everyone/here는 알림을 만들지 않는지.
8. 배지, 개별 읽음, 모두 읽음 및 앱 재실행 후 이력 복원이 정상인지.
9. 재연결/설정 재열기/F9 후 알림이 중복되지 않고, 원본 편집/삭제가 반영되는지.

### 판매

10. 판매할 차례 헤더의 위치와 여백.
11. 수신 판매 원문이 그대로 표시되는지.
12. 커스텀 이모지가 실제 이미지로 보이는지.
13. 여러 이모지와 텍스트 순서가 유지되는지.
14. 파싱 실패 상태에서도 원문이 유지되는지.
15. 이미지 로딩 실패 시 ID 대신 :name:이 보이는지.
16. 일반 영역 두 줄 요약과 상세의 전체 원문/스크롤이 정상인지.

### GTA 번역 — 별도 서버 반영 후

17. 실제 공지의 번역이 자연스러운지.
18. 차량/사업/모드 고유명사가 정확한지.
19. 배수/금액/퍼센트/날짜/조건이 원문과 일치하는지.

### GTA 감지 / 타이머

20. GTA 미실행 상태에서 미감지인지.
21. GTA 실행 후 감지되는지.
22. GTA를 먼저 실행한 뒤 오버레이를 켜도 감지되는지.
23. Alt+Tab 중 감지와 생산 시간 누적이 유지되는지.
24. GTA로 복귀해도 정상인지.
25. GTA 종료 후 미감지 및 생산 타이머 일시 정지인지.
26. GTA 재실행 후 재감지·누적 재개가 되는지.
27. 감지/상실 알림이 한 번씩만 나오는지.
28. 벙커 보급품 타이머가 **스토리 모드 포함** 정책대로 누적되는지.
29. F9 표시/숨기기, F10 잠금, 잠금 상태 click-through가 정상인지.
30. GameForegroundOnly가 전경 상태에 따라 동작하고 실제 모니터 이동/DPI 변경에도 UI가 정상인지.

## 14. Git 상태

**UNCOMMITTED / UNPUSHED**

git add / commit / push / merge / tag / GitHub Release를 수행하지 않았습니다.
Production / Staging / Railway / DNS / Discord Portal 설정과 배포를 변경하지 않았습니다.
main 및 기존 Stable 태그/자산은 그대로입니다.

## 15. 보류 / 제한사항

- **시험에서 실패 후 수정:** 임베디드 용어집 문화권 리소스, 합성 WPF 테스트 리소스 범위, 비동기 초기 알림 처리 순서. 최종 게이트 재통과.
- **의도적 보류:** Windows Toast/새 알림음, 출시 버전 올리기/ZIP/릴리즈/배포.
- **미검증:** 실제 GTA/Discord 사용자 조작, 장시간 실행/절전, 실모니터 DPI 변경, 실제 프로필 업그레이드 실행, CPU/RAM A/B 비교.
- **알려진 제한:** 로컬 실행 감지는 온라인/스토리를 구분하지 않으며, 사용자가 승인한 정책대로 둘 다 누적합니다.
- **알려진 제한:** 번역은 알려진 용어와 문장 패턴 중심입니다. 전체 영문 공지를 자유롭게 번역하는 새 엔진을 구현했다고 주장하지 않습니다. 미검증 고유명사는 원문을 유지합니다.
- **반영 대기:** 새 번역 규칙은 Backend가 사용하는 Core 코드에 있으므로, 별도 승인된 Backend 반영 전 운영 화면에는 기존 번역이 남습니다.
- **시각 검증 범위:** 합성 WPF 렌더링과 실제 사용자 게임 화면 검증은 구분합니다.
