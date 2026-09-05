# LS Overlay 2.1.0 - local Stable preparation

이 절차는 사용자 검토용 Stable 후보만 로컬에서 만듭니다. 커밋, 푸시, 태그, GitHub Release 게시, Railway 배포, DNS·OAuth·Discord 설정 변경을 수행하지 않습니다. 기존 2.0 manifest와 공개 산출물은 역사 자료로 유지합니다.

현재 Stable manifest는 `ls-2.1.0.json`이며, 패키징 도구는 `package-ls-stable.ps1`입니다.

## 1. 문서 준비

실제 2.1 UI 스크린샷을 `docs/2.1/assets/screenshots`의 캡처 목록대로 준비한 뒤 두 PDF를 생성합니다.

문서 도구 의존성: reportlab, Pillow, pypdf, pdfplumber, fonttools 및 Poppler. Wanted Sans Regular/Bold 인스턴스는 `tmp/pdfs`에서만 만들고 PDF에 서브셋 포함합니다. 새 글꼴 바이너리는 배포하지 않습니다.

```powershell
python tools/manual/build_21_guides.py --kind all --output-dir output/pdf/2.1
python tools/manual/validate_21_guides.py --repo . --pdf-dir output/pdf/2.1
```

누락된 실제 스크린샷이 있으면 빌더가 PDF를 만들지 않고 정확한 파일명을 보고합니다. 구형 또는 가짜 UI로 이 검사를 우회하지 않습니다.

## 2. 최소 Stable 검증

Pre-Stable 전체 회귀 1,688개가 이미 통과했으므로 Stable 준비에서는 메타데이터·문서·패키징에 집중합니다.

```powershell
dotnet build GachaOverlay.sln -c Release
dotnet test tests/GachaOverlay.Tests/GachaOverlay.Tests.csproj -c Release --no-build `
  --filter "FullyQualifiedName~GachaOverlay.Tests.Release.Stable210MetadataTests|FullyQualifiedName~GachaOverlay.Tests.M110FinalPolishTests.Protocol_|FullyQualifiedName~GachaOverlay.Tests.M21GtaCompanionTests.Protocol_" `
  --logger "trx;LogFileName=stable-210-focused-final.trx" `
  --results-directory artifacts/stable-preparation-2.1/tests

dotnet publish src/GachaOverlay.App/GachaOverlay.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugSymbols=false -p:DebugType=None `
  -o artifacts/stable-preparation-2.1/wpf-win-x64

git diff --check
```

WPF 출력은 2.0 Stable에서 검증된 .NET 8, win-x64, self-contained, 압축 single-file 정책을 유지합니다. 현재 publish 폴더에는 EXE와 SkiaSharp 고지 원본이 생길 수 있으며, 패키저는 이 정확한 두 경로만 허용하고 고지는 공개 ZIP의 `Licenses/Media`에 다시 배치합니다. Backend 산출물은 공개 ZIP에 넣지 않습니다.

## 3. 사용자 검토 후보 조립

먼저 WPF publish의 SHA-256을 계산합니다.

```powershell
$exeHash = (Get-FileHash artifacts/stable-preparation-2.1/wpf-win-x64/GachaOverlay.App.exe -Algorithm SHA256).Hash
```

두 PDF가 최종 시각 검증을 통과한 뒤, 존재하지 않는 새 출력 디렉터리를 지정해 한 번 조립합니다.

```powershell
pwsh -NoProfile -File tools/release/package-ls-stable.ps1 `
  -ManifestPath ls-2.1.0.json `
  -PublishRoot artifacts/stable-preparation-2.1/wpf-win-x64 `
  -QuickStartPath output/pdf/2.1/LS-Overlay-2.1-Quick-Start-ko.pdf `
  -GuidePath output/pdf/2.1/LS-Overlay-2.1-User-Guide-ko.pdf `
  -FocusedTrxPath artifacts/stable-preparation-2.1/tests/stable-210-focused-final.trx `
  -OutputRoot artifacts/releases/2.1.0-prep `
  -ExpectedExeSha256 $exeHash
```

도구는 다음을 확인합니다.

- EXE ProductVersion/FileVersion과 제품명
- 승인한 EXE SHA-256
- focused TRX PASS
- 두 PDF 존재
- 라이선스 상태와 `Runtime`, `Fonts`, `Themes`, `Media` 고지
- 명시적 공개 파일 allowlist
- 민감한 로컬 경로·토큰 패턴 부재
- clean-directory single-instance smoke
- ZIP의 파일 목록과 새 추출본의 byte hash
- EXE, ZIP, 두 PDF의 SHA-256

ZIP의 `README.md`는 `README-public-2.1.0.md`를 사용합니다. 같은 폴더의 두 PDF를 상대 링크로 열 수 있고 소스 저장소 전용 경로에 의존하지 않습니다. EXE smoke는 새 ZIP 추출본의 single-instance 조기 종료 경로만 확인합니다. 실제 HUD/OAuth 상호작용은 사용자 검토로 남깁니다.

## 4. 공개 전 제한

- 제안 태그: `v2.1.0`
- Release 제목: `LS Overlay 2.1.0`
- Pre-release: 아니요
- 현재 작업에서 게시: **금지**

사용자가 PDF, 실행 파일, README와 Release Notes를 승인하기 전에는 커밋·푸시·병합·태그·배포·게시하지 않습니다. 실제 공개 뒤 `v2.1.0` 태그와 산출물은 불변으로 취급합니다.

## 역사 도구

`ls-2.0.0*.json`, `package-ls-rc.ps1`, `build-release.ps1`, `verify-release.ps1`, `release-manifest.json`은 기존 1.0/2.0 기록과 재현을 위한 역사 도구입니다. 2.1 Stable 조립에는 사용하지 않습니다.
