# LS Overlay 2.1.0 Stable Preparation Report

Date: 2026-09-05 KST

## 1. Status

**READY FOR USER ARTIFACT REVIEW**

사용자 제공 실제 2.1.0 화면을 반영하여 중단된 PDF 제작과 로컬 후보 패키지 조립을 완료했습니다. 공개·게시·배포 완료를 의미하지 않습니다.

## 2. Git / Version

- Branch: develop/2.1
- HEAD / origin/develop/2.1: 3e2723c0af1a423a1af0066fc5a799eab167b083
- main / origin/main: b2f52fa34c8b816adbc58449df4e775bf0263309 (변경 없음)
- 작업 최초 시작 시 clean. 재개 시에는 앞서 만든 Stable Preparation 변경만 있었으며 이를 이어서 작업했습니다.
- Product: LS Overlay / public executable: LSOverlay.exe
- ProductVersion / informational version: 2.1.0
- FileVersion / AssemblyVersion: 2.1.0.0
- PE: x64 (0x8664), .NET 8, win-x64 self-contained compressed single-file
- Code signing: NotSigned
- 제품 동작 코드 변경 없음. 내부 네임스페이스·설정 경로·인증 보호 식별자 유지.

## 3. Quick Start PDF

- File: LS-Overlay-2.1-Quick-Start-ko.pdf
- Pages: 8
- Source: docs/2.1/quick-start/LS-Overlay-2.1-Quick-Start-ko.md
- 설치/압축 해제 → Windows 경고와 체크섬 → Web OAuth → 첫 HUD → F9/F10 → 보조 기능 → 문제 해결.
- 실제 세 HUD 개요, Discord 연결 완료, 번호 표식이 있는 Main HUD, 단축키, 컴패니언 설정, 진단 버튼 사용.
- 전체 페이지 렌더링·시각 확인·텍스트 범위·글꼴 포함 검사 PASS.
- 핵심 스크린샷 누락 없음. 연결 완료 캡처는 브라우저 승인 화면이 아니라고 명시.

## 4. Complete User Guide PDF

- File: LS-Overlay-2.1-User-Guide-ko.pdf
- Pages: 30
- Source: docs/2.1/user-guide/LS-Overlay-2.1-User-Guide-ko.md
- Chapters: 소개 / 설치·업그레이드 / Discord·보안 / HUD / Chat / 미디어 / Sales·히스토리 / GTA 컴패니언 / 사업장 관리자 / 단축키·화면 / 연결·진단 / 문제 해결 / 데이터·FAQ / 지원.
- 사업장 관리자의 수동 입력과 접속 기반 계산, 현실 시간과 확인된 온라인 플레이 시간, LSD·맨션 부스트, 하드 추적, 미리 알림과 일반 타이머를 구분했습니다.
- 일일 15:00 KST / 주간 목요일 18:00 KST, 작성자 간격 -2~48 DIP, ESC 매핑 해제, F9 공통 표시를 유지했습니다.
- 실제 잠금/해제 사업장 화면, 도전 준비 중, Chat·Sales, 히스토리, 미디어·테마·키 설정과 진단 화면 사용.
- 목차의 모든 장 시작 페이지와 실제 PDF 페이지 대조 PASS. PDF bookmarks 포함.
- 30쪽 모두 렌더링·시각 검수 PASS.
- 선택적 추가 화면: 본인 판매완료 버튼, 단축키 입력 대기 상태, 채워진 주간 데이터. 없는 상태를 합성하거나 해당 상태를 검증했다고 주장하지 않았습니다.

## 5. Documentation Design

- 기존 Wanted Sans Variable에서 Regular 400 / Bold 700 인스턴스를 문서 빌드 임시 폴더에 만들고 PDF에 서브셋 포함. 새 폰트 파일 배포 없음.
- A4 세로, 20mm 좌우 여백, 본문 10.2pt / 줄간격 15.8pt, 절제된 녹색과 실제 LS Overlay 로고 사용.
- PNG 무손실 크롭, 라운드 프레임, 캡션과 Main HUD 번호 표식.
- 실제 제공 화면에서 24개 컷 준비, 두 PDF에서 총 22개 고유 컷 사용.
- 타인 이름·대화·판매 원문은 픽셀 단계에서 불투명하게 가림. 원본을 PDF에 숨겨 넣지 않음. PNG EXIF 없음.
- 스크린샷 수치/키는 사용자 설정 예시임을 명시.
- 검색/선택 가능한 한국어 본문, 외부 링크, 페이지 번호, 제목·저자 metadata, PDF outline 유지.
- 첫 렌더에서 확인한 별도 페이지로 밀린 그림, 코드 상자 간격, 화면 가장자리의 잘린 행을 수정하고 재검수했습니다.

## 6. README / Release Notes

- README.md: 2.1 기능, 플랫폼, 다운로드, F9/F10, OAuth, 업그레이드와 지원.
- docs/releases/LS-Overlay-2.1.0-release-notes.md: 공개용 기능·개선·제약·업그레이드 요약.
- tools/release/README-public-2.1.0.md: ZIP 전용 README 원본. 같은 폴더의 PDF와 LICENSE 링크를 사용하여 저장소 전용 상대 경로가 깨지지 않도록 분리.

## 7. License Audit

- .NET: self-contained runtimeconfig에서 Microsoft.NETCore.App / Microsoft.WindowsDesktop.App 8.0.30 확인. 보존된 라이선스 두 개와 Runtime third-party notice SHA-256이 기록과 일치.
- Fonts: KIMM, Cafe24, Pretendard, Wanted Sans, 조선굴림체의 저장소 고지 유지. OFL 및 Cafe24 원본 라이선스 PDF 두 개 포함.
- Themes: GitHub Dark, One Dark Pro, Nord, Tokyo Night, Monokai 관련 기존 고지 유지.
- SkiaSharp: 3.119.1 MIT 고지를 Licenses/Media/SkiaSharp.txt에 포함.
- LICENSE 및 notice index 포함. 라이선스 manifest의 모든 그룹 확인 PASS.
- 이 검사는 현재 저장소·런타임 패키지 증거와 패키지 포함 여부를 확인한 것이며 별도 법률 의견이나 포괄적 외부 법률 감사가 아닙니다.

## 8. Release Candidate Artifacts

Review directory: artifacts/releases/2.1.0-prep

### LSOverlay.exe

- Bytes: 85599587
- SHA256: DB5E9A80FEE55D6028C50AB3A3A681F48AD42441AEF481415649D1FA79B338AE

### LS-Overlay-2.1.0-win-x64.zip

- Bytes: 84870101
- SHA256: A8A468CF2F4B2B71B9BCDF82FADB96E34A24F4C26924DBB5244CDB13D7718FF4

### LS-Overlay-2.1-Quick-Start-ko.pdf

- Bytes: 2168391
- SHA256: EB0F55CC88F08F31190ACBBACA05DA806C04B3927D26F4FF77B33DCDB238A6BF

### LS-Overlay-2.1-User-Guide-ko.pdf

- Bytes: 2378491
- SHA256: EEACDC197F72D4599E787F69420B29E9C1502D3EFC4ECF89AF331BAF99F6BF9E

Checksum: LS-Overlay-2.1.0-SHA256.txt
Manifest: LS-Overlay-2.1.0-manifest.json

## 9. ZIP Manifest

Exact count: **16 files**. 중복 경로·불필요한 최상위 디렉터리 없음.

```text
LICENSE
Licenses/Fonts/License-Cafe24-PRO-Slim-Fit.pdf
Licenses/Fonts/License-Cafe24-PRO-Slim-Max.pdf
Licenses/Fonts/NOTICE-Fonts.txt
Licenses/Fonts/OFL-1.1.txt
Licenses/Media/SkiaSharp.txt
Licenses/Runtime/LICENSE-DOTNET-RUNTIME.txt
Licenses/Runtime/LICENSE-WINDOWS-DESKTOP-RUNTIME.txt
Licenses/Runtime/README.txt
Licenses/Runtime/THIRD-PARTY-NOTICES-DOTNET-RUNTIME.txt
Licenses/Themes/NOTICE-Color-Themes.txt
Licenses/THIRD-PARTY-NOTICES.txt
LS-Overlay-2.1-Quick-Start-ko.pdf
LS-Overlay-2.1-User-Guide-ko.pdf
LSOverlay.exe
README.md
```

PDB, source, logs, settings, credentials, diagnostics, screenshot originals, PDF source/build scripts는 ZIP에 포함되지 않습니다. 체크섬·검토 manifest는 ZIP 바깥에 둡니다.

## 10. Validation

- Release build: 앞선 동일 Stable Preparation 단계에서 PASS, warning 0 / error 0. 재개 후 제품 코드/버전 변경이 없으므로 재빌드하지 않고 동일 EXE hash를 확인했습니다.
- WPF self-contained publish: 앞선 단계 PASS. 현재 후보 EXE hash와 정확히 일치.
- Final focused tests: **9/9 PASS, 0 failed, 0 skipped**. Release metadata 5 + 기존 protocol 호환성 3 + GTA capability/additive compatibility 1.
- 전체 1,688개 회귀: 이전 Pre-Stable 완료 기록. 이번 작업에서는 재실행하지 않음.
- PDF: 8 + 30 = 38쪽 전체 Poppler 렌더 및 시각 확인 PASS. 텍스트 경계·페이지 경계·목차·글꼴 포함·metadata 확인 PASS.
- PDF stale version: 2.0 언급은 업그레이드 제목/목차 등 역사 비교만 해당. 현재 버전 표기는 2.1/2.1.0.
- ZIP extraction: 16개 전체 원본과 새 추출 파일 hash 일치, 중복 없음.
- ZIP 내 두 설명서 및 Cafe24 라이선스 PDF를 실제 parser로 열기 PASS. README UTF-8 및 상대 대상 파일 존재 PASS.
- EXE smoke: 새 ZIP 추출본을 실행하고 정상 종료 확인. 기존 single-instance 조기 종료 경로를 사용한 app host/runtime 수준 검사이며 HUD·OAuth 상호작용 전체 검증은 아님.
- EXE: ProductVersion 2.1.0 / FileVersion 2.1.0.0 / x64 / NotSigned.
- 4개 최종 파일 SHA256 재검산 PASS.
- dotnet format --verify-no-changes PASS.
- git diff --check PASS.
- 공개 문서의 개인 경로·토큰 패턴·Discord Snowflake scan PASS.
- 생성 PDF/EXE/ZIP, 임시 폰트·렌더·TRX는 소스 변경 목록에서 제외. 실제 문서용 처리 PNG와 editable source만 소스에 남음.
- Backend capability는 gta_companion_v1을 광고한 client에만 제공하는 기존 조건 유지. 설정 schema 22와 DPAPI identity 유지.

## 11. Backend Production Release Note

docs/releases/LS-Overlay-2.1.0-production-deployment-note.md에 승인 후 실행 순서를 기록했습니다.

사용자 승인 → Stable Preparation commit/push → 저장소 정책에 따른 main 통합 → Production Backend 배포 → healthz / 구형·신형 Remote 호환 / GTA snapshot·Last-Good 확인 → 태그·공개.

Production Volume과 gta-companion-events.json, DNS/OAuth/환경 변수는 변경·초기화하지 않습니다. 이 단계는 **실행하지 않았습니다**.

## 12. Changed Files

아래는 이번 Stable Preparation 전체의 소스·문서 변경 목록입니다. Rc20MetadataTests.cs는 현재 버전 테스트인 Stable210MetadataTests.cs로 교체했습니다. 모든 변경은 커밋되지 않은 상태입니다.

```text
.gitignore
Directory.Build.props
README.md
docs/2.1/assets/screenshots/01-main-hud.png
docs/2.1/assets/screenshots/02-discord-connected.png
docs/2.1/assets/screenshots/03-settings-hud-hotkeys.png
docs/2.1/assets/screenshots/04-three-huds-overview.png
docs/2.1/assets/screenshots/05-main-chat.png
docs/2.1/assets/screenshots/06-sales.png
docs/2.1/assets/screenshots/07-gta-companion.png
docs/2.1/assets/screenshots/08-business-cargo.png
docs/2.1/assets/screenshots/08-business-unlocked.png
docs/2.1/assets/screenshots/09-business-compact.png
docs/2.1/assets/screenshots/09-business-locked.png
docs/2.1/assets/screenshots/09-general-timer.png
docs/2.1/assets/screenshots/10-settings-hotkey-capture.png
docs/2.1/assets/screenshots/11-settings-media.png
docs/2.1/assets/screenshots/11-settings-themes.png
docs/2.1/assets/screenshots/11-settings-visual-media.png
docs/2.1/assets/screenshots/12-diagnostics-button.png
docs/2.1/assets/screenshots/12-settings-diagnostics.png
docs/2.1/assets/screenshots/13-settings-sales.png
docs/2.1/assets/screenshots/14-sales-history.png
docs/2.1/assets/screenshots/15-settings-companion.png
docs/2.1/assets/screenshots/16-settings-business.png
docs/2.1/assets/screenshots/17-settings-business-options.png
docs/2.1/assets/screenshots/18-settings-timer-sound.png
docs/2.1/assets/screenshots/README.md
docs/2.1/assets/screenshots/provenance.json
docs/2.1/quick-start/LS-Overlay-2.1-Quick-Start-ko.md
docs/2.1/user-guide/LS-Overlay-2.1-User-Guide-ko.md
docs/releases/LS-Overlay-2.1.0-production-deployment-note.md
docs/releases/LS-Overlay-2.1.0-release-notes.md
docs/releases/LS-Overlay-2.1.0-stable-preparation-report.md
src/GachaOverlay.App/Assets/Fonts/ThirdPartyNotices/NOTICE-Fonts.txt
src/GachaOverlay.App/Assets/Themes/ThirdPartyNotices/NOTICE-Color-Themes.txt
tests/GachaOverlay.Tests/Release/Rc20MetadataTests.cs
tests/GachaOverlay.Tests/Release/Stable210MetadataTests.cs
tools/manual/build_21_guides.py
tools/manual/prepare_21_screenshots.py
tools/manual/qa_21_guides.py
tools/manual/validate_21_guides.py
tools/release/README-public-2.1.0.md
tools/release/README.md
tools/release/licenses/THIRD-PARTY-NOTICES.txt
tools/release/ls-2.1.0.json
tools/release/package-ls-stable.ps1
```

생성 검토 파일:
- artifacts/releases/2.1.0-prep: EXE, ZIP, 두 PDF, checksum, manifest, README/LICENSE/Licenses
- output/pdf/2.1: PDF 빌드 출력 복사본 (git ignored)
- tmp/pdfs/2.1/qa: 전체 페이지 PNG, contact sheets, structural-qa.json (git ignored)
- artifacts/stable-preparation-2.1/tests/stable-210-focused-final.trx
- artifacts/stable-preparation-2.1/wpf-win-x64: 기존 publish 원본

## 13. Remaining User Review

1. 두 PDF의 실제 크기에서 글자와 캡처 가독성.
2. 개인정보 가림 범위와 필요한 경우 본인 테스트 대화 캡처로 교체 여부.
3. 타이머·부스트·하드 추적 설명의 사용자 관점 정확성.
4. 새 ZIP을 풀고 LSOverlay.exe 실제 HUD 실행 확인.
5. README와 공개 Release Notes 문구.
6. 실제 Stable 공개·Production 배포를 진행할 최종 승인.

## 14. Next Step

**사용자 산출물 검토와 승인 대기.**

커밋, push, main 변경, merge, 태그, GitHub Release, Production/Railway 배포, DNS/OAuth 변경은 하지 않았습니다.
