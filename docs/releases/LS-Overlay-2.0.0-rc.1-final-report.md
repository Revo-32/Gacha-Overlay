# LS Overlay 2.0.0-rc.1 최종 릴리즈 준비 보고서

작성일: 2026-09-03

## 1. Summary

**LS OVERLAY 2.0.0-rc.1 RELEASE PREPARATION READY**

사용자가 승인한 **릴리즈 준비본 복원 + T 빠른 전환 기능 제거** 빌드로 최종 Windows 배포 ZIP, 한국어 안내서 PDF, 체크섬, 공개 manifest와 릴리즈 문안 준비를 완료했다.

이번 패키징에서는 앱을 다시 빌드하거나 다른 EXE로 교체하지 않았다. ZIP 안의 실행 파일은 사용자가 확인한 `rc20-baseline-no-t` EXE와 SHA-256이 동일하다.

**현재 상태: 로컬 준비 완료 / GitHub 미공개.**

## 2. 확정한 기능 범위

- 정상 확인했던 릴리즈 준비본을 기준으로 사용한다.
- 이후 추가했던 판매 상세 이모지 렌더링과 T 포커스 전환 실험은 취소했다.
- T 전환 기능, 입력 훅, Discord 창 활성화 코드와 설정 항목을 제거했다.
- 기존 설정 파일의 T 옵션은 폐기 항목으로 처리한다. 다른 설정과 로그인 저장소는 유지한다.
- F9/F10, HUD 잠금·이동·스크롤, 일반 채팅·미디어, 판매·세션 표시 등 나머지 기능은 준비본 상태를 유지한다.
- 판매 상세의 추가 이모지 렌더링 수정은 이번 후보에 포함하지 않는다. 원래의 상세 텍스트 표시를 사용한다.
- Discord 창 이동은 Windows의 Alt+Tab을 사용한다.

이번 단계에서 게임 입력 우회, 카메라 보정, 마우스 강제 이동, 신규 기능 또는 서버 변경을 추가하지 않았다.

## 3. 배포 대상과 식별 정보

- 제품: LS Overlay
- 버전: `2.0.0-rc.1`
- 공개 제목: `LS Overlay 2.0.0 RC1`
- 태그 예정: `v2.0.0-rc.1` — 아직 생성하지 않음
- 배포 종류: Pre-release
- Windows x64 / Release / self-contained / 압축 single-file
- 공개 실행 파일: `LSOverlay.exe`
- 내부 어셈블리: `GachaOverlay.App` 유지
- FileVersion / AssemblyVersion: `2.0.0.0`
- 코드 서명: NotSigned
- 자동 업데이트: 제공하지 않음

최종 파일 폴더:

`E:\Codex\Projects\Gacha_Overlay\artifacts\releases\2.0.0-rc.1-final`

이전 `artifacts/releases/2.0.0-rc.1`은 보존용 최초 준비본이며, 현재 공개 대상으로 선택한 폴더가 아니다.

## 4. 최종 산출물

| 파일 | 크기 | 용도 |
|---|---:|---|
| LS-Overlay-2.0.0-rc.1-win-x64.zip | 75,717,919 bytes | 일반 사용자용 배포 ZIP |
| LSOverlay.exe | 78,475,035 bytes | ZIP에 포함된 실행 파일과 동일 |
| LS-Overlay-2.0-RC-User-Guide-ko.pdf | 2,005,104 bytes | 한국어 안내서 9쪽, ZIP에도 포함 |
| LS-Overlay-2.0.0-rc.1-SHA256.txt | 텍스트 | ZIP·EXE·PDF 무결성 확인 |
| LS-Overlay-2.0.0-rc.1-manifest.json | JSON | 버전, 구성 파일, 테스트 결과와 해시 기록 |

SHA-256:

```text
ZIP: F7B4A76D7ABD44B1BFFF765D42CAFCFD2D4743BFE3ECC7A3A4C237AFB572C472
EXE: 9FE9EB430B2BFFD419C7A90401518C067D5ED057762C7196AAFB85A6977BAD59
PDF: 8AB9C52EB69D9082A17D1320FAB20A8F840D774D107BDA4DB5ABE12E8C3F3449
```

ZIP에는 실행 파일, 빠른 시작 README, 한국어 PDF, LICENSE 및 글꼴·런타임·테마 라이선스 등 허용된 14개 파일만 포함했다. Backend, 소스, 테스트, PDB, 사용자 설정·인증 파일·로그는 포함하지 않았다.

## 5. 검증 결과

| 항목 | 결과 |
|---|---|
| T 제거 후보 Debug 빌드 | 경고 0 / 오류 0 |
| T 제거 후보 Debug 전체 테스트 | 1,539 통과 / 실패 0 / 건너뜀 0 |
| T 제거 후보 Release 빌드 | 경고 0 / 오류 0 |
| T 제거 후보 Release 전체 테스트 | 1,539 통과 / 실패 0 / 건너뜀 0 |
| 패키징 후 문서·메타데이터·T 제거 계약 재검증 | 12 통과 / 실패 0 / 건너뜀 0 |
| 이번 준비 단계 format 검증 | PASS |
| 이번 준비 단계 git diff --check | PASS |
| 패키지 EXE와 승인된 후보 EXE의 해시 비교 | 일치 |
| ZIP 허용 목록 및 새 폴더 압축 해제 후 모든 파일 비교 | 14개 파일 모두 일치 |
| ZIP·EXE·PDF와 최종 manifest/체크섬 비교 | PASS |
| 고립된 실행 파일 시작 검사 | PASS, 종료 코드 0 |
| 기존 릴리즈 폴더 덮어쓰기 방지 | 거절 확인 |
| 잘못된 EXE 해시 입력 | 거절 확인 |
| 실패 또는 불완전한 테스트 결과 입력 | 거절 확인 |

전체 빌드·테스트 결과는 직전 T 제거 후보를 검증한 TRX를 사용했다. 이번 단계에서 재빌드하지 않은 동일 EXE를 패키징했으며, manifest에 해당 TRX의 파일명·해시·완료 시각을 기록했다. 이전 실험용 빌드나 최초 1,537개 테스트 결과를 이번 후보의 근거로 사용하지 않았다.

고립 시작 검사는 단일 인스턴스 보호를 이용해 런타임과 App.xaml 로드 후 종료하는 제한된 검사다. 실제 프로필·로그인·네트워크·HUD를 시작하지 않았으며, 이를 전체 실사용 검증으로 표현하지 않는다.

## 6. 사용자 안내서

PDF 스킬의 생성·렌더링·시각 검증 절차를 적용했다. 기존 Markdown, 로고와 글꼴을 재사용하고 기능과 맞지 않던 T 안내를 정리했다.

- 9쪽 유지, 모든 페이지를 PNG로 렌더링해 확인
- T 빠른 전환 설정 안내 삭제, Alt+Tab 사용 안내 반영
- F9/F10, 잠금 해제 후 HUD 클릭, 트레이에서 설정 열기 안내 유지
- 한글 글리프, 페이지 경계, 페이지 번호와 배치 확인
- 개인정보처리방침·이용약관·상태 페이지·mailto 링크 포함 확인
- 내부 경로, 내부 ID 및 폐기된 T 사용 설명이 없는지 검사
- 실제 앱·게임·Discord 화면 자동 캡처 없음

이메일은 공개 문의처 `revo.32.39.41@gmail.com`을 그대로 사용한다. 공개 문서에 포함되는 것이 의도된 값이다.

**PUBLIC CONTACT VERIFIED**

## 7. 원본과 기존 작업 보존

최초 릴리즈 EXE·ZIP·PDF의 원래 해시가 유지됨을 다시 확인했다. 취소한 수정 코드와 실험용 실행 파일도 기존 백업 위치에 보존되어 있다.

기존 사용자 프로필·로그인·DPAPI 식별자·단일 인스턴스 이름을 바꾸지 않았다. 사용자 실행 프로세스를 강제 종료하지 않았다. 패키징 도구가 실행한 고립 검사 프로세스만 정상 종료했다.

기준 커밋은 `5a4c10044efcd4cdff1f128261c00f82b3a17c5e`이며 RC 준비 및 T 제거 변경은 아직 미커밋 상태다. 이 커밋만으로 최종 EXE가 재현된다고 주장하지 않도록 manifest에 미커밋 변경 포함 여부를 기록했다.

## 8. 남은 확인과 한계

- 사용자는 이 후보를 기준으로 릴리즈 준비를 승인했다. 개별 실사용 체크리스트가 모두 완료된 것으로 임의 표시하지 않았다.
- 4~5시간 이상 장시간 안정성 검증은 완료했다고 주장하지 않는다.
- 기존 Diagnostic Bundle 테스트 간헐 실패 조사는 재개하지 않았다. 생산 영향 미확정이라는 기존 관찰 상태를 유지한다.
- 상태 도메인의 DNS/TLS 및 운영 서비스 가용성은 이번 로컬 패키징에서 재검증하지 않았다.
- 코드 서명과 자동 업데이트를 추가하지 않았다. 안내서에 현재 제약을 명시했다.

## 9. Production Changes

없음.

- commit / push / tag: 미실행
- GitHub Release 생성·업로드·공개: 미실행
- Railway / Backend 배포: 미실행
- DNS / Discord Developer Portal / 권한·Intents: 변경 없음
- OAuth / Remote protocol / WPF 기능 / 채팅·판매 알고리즘: 이번 패키징 단계에서 추가 변경 없음

공개 단계에서는 별도 승인 후 현재 소스 변경을 포함할 커밋·태그를 확정하고, 최종 폴더의 ZIP·PDF·SHA256 파일과 필요 시 공개 manifest를 업로드한다. 최초 준비본이나 취소된 실험 EXE를 업로드하지 않는다.

## 10. Final Status

**LS OVERLAY 2.0.0-rc.1 RELEASE PREPARATION READY**

**APPROVED NO-T CANDIDATE PACKAGED — NOT PUBLISHED**

**PUBLIC CONTACT VERIFIED**
