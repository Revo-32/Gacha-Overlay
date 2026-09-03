# LS Overlay 2.0.0-rc.1 Release Preparation Report

> 이 문서는 최초 준비본의 기록이다. 이후 사용자가 승인한 T 제거 빌드를 담은 현재 공개 후보와 보고서는 `LS-Overlay-2.0.0-rc.1-final-report.md`를 기준으로 한다. 아래 원본 EXE/ZIP/PDF는 덮어쓰지 않고 보존했다.

## 1. Summary

**LS Overlay 2.0.0-rc.1 RELEASE PREPARATION READY**

2026-09-03. 현재 제품 기능을 변경하지 않고 버전·메타데이터, 안전한 공개 EXE 이름, Windows ZIP, 한국어 안내서·PDF, 릴리스 초안과 실사용 검증 계획을 준비했다.

## 2. Diagnostic Investigation Stop

M10.1.1 조사는 중단했다. 시작 시 작업 트리는 깨끗했고 조사 변경이 없었다. Diagnostic 생산 코드·테스트 변경, 계측, 재현 반복, 재시도·지연 추가는 전혀 하지 않았다. 간헐 실패는 RC 관찰 항목으로만 이월한다.

## 3. Version / Product Metadata

Product/Title/Description: LS Overlay. Version/InformationalVersion: **2.0.0-rc.1**. Assembly/FileVersion: 2.0.0.0.
Tag 제안: v2.0.0-rc.1. Release title: LS Overlay 2.0.0 RC1. Pre-release = ON.
Company는 빈 값이며 기존 MIT 저작권 문구만 사용했다. 코드 서명은 NotSigned다.

## 4. Executable Naming

**LSOverlay.exe**. publish된 GachaOverlay.App.exe의 바이트를 변경하지 않고 패키징에서 이름만 바꿨다. AssemblyName/프로젝트/namespace는 유지했다.

- LocalAppData: 기존 GachaOverlay 경로 유지, 스키마 18 유지
- DPAPI: 현재 Windows 사용자 범위·기존 entropy 그대로
- mutex: 기존 고정 이름 그대로, 신·구 패키지 사이 단일 인스턴스 호환
- 자동 시작: 등록 이름 그대로, 활성화된 경우 일반 앱 시작에서 현재 실행 경로로 갱신
- 자동 로그인: 저장소/로그인 코드 무변경, 기존 회귀 테스트 통과. 실제 사용자 프로필 로그인은 미실행
- 리소스: 기존 어셈블리 pack URI 유지. 고립 시작에서 App.xaml 리소스 로드 성공
- 재시작/업데이터: 새 기능 없음, 자동 업데이트 없음

## 5. Public Package

`artifacts/releases/2.0.0-rc.1/LS-Overlay-2.0.0-rc.1-win-x64.zip`

75,725,212 bytes. SHA-256:
`2784123788BC9B93B2604D0657CBF126B17F7543BBAD789F4715B40F06FE355F`

정확한 14개 파일:

```text
LICENSE
Licenses/Fonts/License-Cafe24-PRO-Slim-Fit.pdf
Licenses/Fonts/License-Cafe24-PRO-Slim-Max.pdf
Licenses/Fonts/NOTICE-Fonts.txt
Licenses/Fonts/OFL-1.1.txt
Licenses/Runtime/LICENSE-DOTNET-RUNTIME.txt
Licenses/Runtime/LICENSE-WINDOWS-DESKTOP-RUNTIME.txt
Licenses/Runtime/README.txt
Licenses/Runtime/THIRD-PARTY-NOTICES-DOTNET-RUNTIME.txt
Licenses/Themes/NOTICE-Color-Themes.txt
Licenses/THIRD-PARTY-NOTICES.txt
LS-Overlay-2.0-RC-User-Guide-ko.pdf
LSOverlay.exe
README.md
```

Backend, PDB, 소스, 테스트, 사용자 설정·인증·로그 파일을 넣지 않았다. 라이선스는 기존 고지를 보존했다.

## 6. Executable Artifact

LSOverlay.exe: **78,479,545 bytes**. SHA-256:
`1DFAD6587813D2EA0851B1AC394611004C1AFBD69400302B1872B11E90C3BF3F`

win-x64 / AMD64 0x8664 / self-contained / single-file / native self-extraction. 기존 압축 단일 파일 모델 유지.

## 7. User Documentation

- docs/user/QUICK-START-ko.md: 한 장 빠른 시작, ZIP README로 포함
- docs/user/LS-Overlay-2.0-RC-User-Guide-ko.md: 편집 가능한 안내서 원본
- artifacts/releases/2.0.0-rc.1/LS-Overlay-2.0-RC-User-Guide-ko.pdf

한국어 초보자 중심이며 다운로드→로그인→HUD→채팅→판매→종료→문제 해결 순서다. 개인 App 생성, Secret/Token 설정, 서버 구축 안내는 없다. 실제 UI 버튼 이름과 공개 연락처·링크를 사용했다.

## 8. PDF

9쪽. 사용자 제공 LS Overlay 원본 로고와 기존 Pretendard/Wanted Sans 글꼴 사용. 원본 이미지·아이콘은 수정하지 않았다.
Korean glyph coverage, embedded fonts, 페이지 수, 4종 링크, 내부 경로/ID 금지, 텍스트 경계 검사 통과. Poppler로 모든 9쪽을 렌더링하여 잘림·겹침·한글 표시를 확인했다. Screenshots: **NO**. PDF 제작·검증은 pdf 스킬 절차를 적용했다.

## 9. Screenshot Requests

**NONE - DOCUMENTATION IS USABLE WITHOUT SCREENSHOTS**

실제 앱·Discord·GTA를 조작하거나 화면을 캡처하지 않았다. 향후 사용자 제공 이미지로 공개 전 개정할 수 있지만, 현재 안내서는 스크린샷 없이 완결된다.

## 10. Root README

LS Overlay 2.0 RC 기준으로 전면 정리했다. 오래된 Local RPC/개인 App 인증 설명과 예전 화면·성능 수치를 제거하고 현재 기능, 설치·단축키, 링크·문의, 라이선스를 간결하게 안내한다. 현재 RC는 공개 전 준비 단계임을 명시했다.

## 11. GitHub Release Draft

docs/releases/LS-Overlay-2.0.0-rc.1-github-release.md

LS Overlay 2.0.0 RC1 / v2.0.0-rc.1 / Pre-release = ON. **NOT PUBLISHED**. 별도 변경 안내 파일도 준비했다.

## 12. RC Test Plan

docs/releases/LS-Overlay-2.0.0-rc.1-test-plan.md

설치, 기존/신규 로그인, 채팅·미디어·전환·스크롤, F9/F10/T, 현재 판매완료와 읽기 전용 상세, 세션, 트레이·설정, 재연결, 진단 파일, 장시간 사용을 다룬다. 전 항목 사용자 실행 전이다.

## 13. Known RC Observation

Diagnostic Bundle automated-test flake: **INVESTIGATION DEFERRED**.
Production impact: **NOT CONFIRMED**. RC action: 실사용 중 진단 ZIP 생성 관찰.
이번 Debug/Release 실행에서는 해당 실패가 나타나지 않았다. 이 사실을 근본 해결 또는 장시간 안정성 입증으로 해석하지 않는다.

## 14. Status / Compliance Links

Privacy: https://overlay.revo32.cloud/privacy

Terms: https://overlay.revo32.cloud/terms

Status: https://status.revo32.cloud

Support: revo.32.39.41@gmail.com / mailto:revo.32.39.41@gmail.com

**PUBLIC CONTACT VERIFIED**. Status domain: **PENDING DNS/TLS**. 단발 직접 HTTPS 요청이 HttpRequestException으로 실패했다. DNS/TLS 중 어떤 원인인지는 조사하지 않았으며 전파 대기·반복 폴링하지 않았다. 운영 페이지의 게시 상태를 로컬 검증 결과와 혼동하지 않는다.

## 15. Tests

Pre-RC 1,534 → 신규 RC 메타데이터·호환성·문서 테스트 3개 → **1,537**.

| 검증 | 결과 |
|---|---|
| Restore | PASS |
| Debug build | PASS, warnings 0 / errors 0 |
| Debug full tests | 1,537 PASS / failed 0 / skipped 0, 35초 |
| Release build | PASS, warnings 0 / errors 0 |
| Release full tests | 1,537 PASS / failed 0 / skipped 0, 34초 |
| Format verify / git diff check | PASS / PASS |
| WPF win-x64 publish | PASS |
| Backend linux-x64 publish | PASS, 공유 버전 메타데이터 때문에 로컬 검증만 실행 |
| 고립 패키지 시작/정적 검증 | PASS, 아래 제한 참조 |
| PDF / ZIP / checksum | PASS / PASS / PASS |

새 임시 폴더에 EXE만 복사하고 기존 mutex를 잡은 상태에서 한 번 실행하여 exit 0을 확인했다. 번들 런타임·App.xaml 로드 후 secondary-instance 경로에서 종료되므로 ApplicationHost, 사용자 프로필, 네트워크, 실 HUD가 시작되지 않는다. **일반 로그인·전체 HUD 실행 검증은 사용자 대기**이며 이 결과로 대체하지 않는다. 스크린샷 자동화나 Diagnostic 실행용 helper는 사용하지 않았다.

TRX: artifacts/rc-preparation/tests/debug/rc20-debug.trx, artifacts/rc-preparation/tests/release/rc20-release.trx.
자세한 결과: LS-Overlay-2.0.0-rc.1-validation.json.

## 16. Files Added

- docs/user/QUICK-START-ko.md: 짧은 설치·사용 안내
- docs/user/LS-Overlay-2.0-RC-User-Guide-ko.md: 안내서 원본
- docs/releases/LS-Overlay-2.0.0-rc.1.md: 사용자 변경 안내
- docs/releases/LS-Overlay-2.0.0-rc.1-github-release.md: 게시용 본문
- docs/releases/LS-Overlay-2.0.0-rc.1-test-plan.md: 체크리스트·Soak·관찰 항목
- docs/releases/LS-Overlay-2.0.0-rc.1-validation-report.md: 본 보고서
- docs/releases/LS-Overlay-2.0.0-rc.1-validation.json: 검증 요약
- tests/GachaOverlay.Tests/Release/Rc20MetadataTests.cs: RC 테스트 3개
- tools/manual/build_rc_guide.py: Markdown→PDF 제작·구조 검사
- tools/release/ls-2.0.0-rc.1.json: 공개 패키지 사양
- tools/release/package-ls-rc.ps1: 안전한 로컬 패키징·검증·해시 생성

artifacts/releases/2.0.0-rc.1에는 EXE, ZIP, PDF, checksum, 공개 manifest와 README/LICENSE/고지가 있다. 생성물은 Git ignore 대상이다.

## 17. Files Changed

- Directory.Build.props: 중앙 버전, Assembly/File/InformationalVersion
- src/GachaOverlay.App/GachaOverlay.App.csproj: 설명·기존 저작권·회사명 생략
- README.md: 현재 일반 사용자 안내
- tools/release/README.md: 현재 RC 빌드·패키징 방법 및 구버전 도구 구분

다른 기존 파일은 변경하지 않았다. Sales, Chat, OAuth, protocol, Diagnostic, Backend 로직, 설정 저장소, 아이콘·로고 모두 그대로다.

## 18. Git State

Branch main. Base/current HEAD **5a4c10044efcd4cdff1f128261c00f82b3a17c5e**.
시작 시 clean, 기존 미커밋 작업 없음. 완료 시 위 4개 수정 + 11개 신규 파일, staged 0.
공개 manifest는 이 base commit **및 미커밋 RC 변경 포함**을 명시한다. 커밋/푸시는 하지 않았다.

## 19. User Manual RC Validation

1. ZIP을 새 폴더에 풀고 기존 앱을 트레이에서 종료한 뒤 LSOverlay.exe 실행
2. 기존 자동 로그인/새 Discord 로그인, 최근 채팅·채널 전환·미디어 확인
3. F9/F10, 이동·스크롤·↓ N, T 전환, 테마·설정·트레이 확인
4. 자기 판매글 일반 영역의 판매완료, 상세 목록 유지와 잠금 입력 통과 확인
5. 세션 표시, 네트워크 복구, 진단 ZIP 생성 확인
6. 테스트 계획에 결과와 불편한 점 기록

## 20. Real-World RC Soak Recommendation

4~5시간 연속 사용, 가능하면 더 긴 하루/야간 관찰과 여러 PC/사용자 결과를 권장한다. 앱 메모리·핸들·스레드와 Backend 메모리의 상승/안정화 추세, 단절·재시작·진단 생성 결과를 함께 기록한다. 지금 자동 실행하지 않았다.

## 21. Deferred Work

Diagnostic ZIP 원인 조사, rc.2가 필요한 수정, namespace/project 및 LocalAppData 이름 변경, 동적 Bot 상태, 장애/가동 이력, 대규모 설정 재설계, 신규 기능을 이월했다. 정기 Backend 재시작을 추가하지 않았다.

## 22. Publication - NOT PERFORMED

커밋, 푸시, 태그, GitHub Release, 업로드, Railway, DNS, Discord Portal 변경 모두 미실행.

## 23. Recommended Next Step

**USER REVIEW OF RC PACKAGE** → 별도 승인 후 COMMIT / PUSH → 명시적 게시 승인 후 **CREATE v2.0.0-rc.1 PRE-RELEASE**.
공개한 ZIP/PDF/checksum은 덮어쓰지 않는다. 이후 수정 배포는 rc.2로 구분한다.

## 24. Final Status

**LS OVERLAY 2.0.0-rc.1 READY FOR USER REVIEW**
