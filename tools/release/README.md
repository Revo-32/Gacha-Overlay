# LS Overlay 2.0.0-rc.1 - local release engineering

No commit, push, tag, GitHub release/upload, Railway, DNS or Discord Portal changes are performed by this workflow.
The current manifest is `ls-2.0.0-rc.1.json`. The older `release-manifest.json`, `build-release.ps1` and `verify-release.ps1` are historical 1.0 tooling; do not use them for 2.0.

## Validation and publishing (local only)

Run from the repository root on Windows x64 with .NET 8 SDK:

```powershell
dotnet restore GachaOverlay.sln
dotnet build GachaOverlay.sln -c Debug
dotnet test GachaOverlay.sln -c Debug --no-build --logger "trx;LogFileName=rc20-debug.trx" --results-directory artifacts/rc-preparation/tests/debug
dotnet build GachaOverlay.sln -c Release
dotnet test GachaOverlay.sln -c Release --no-build --logger "trx;LogFileName=rc20-release.trx" --results-directory artifacts/rc-preparation/tests/release
dotnet format GachaOverlay.sln --verify-no-changes
git diff --check
dotnet publish src/GachaOverlay.App/GachaOverlay.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/rc-preparation/wpf-win-x64
```

The shared version props also affect Backend metadata; for this RC the linux-x64 Backend publish was verified locally, not deployed.
The original preparation changed only release metadata and packaging. The later T-removal correction is described below; profile storage, credential entropy, protocol and branding assets remain unchanged.

The final candidate is the prepared RC baseline **with the T feature and setting removed**. Later Sales Detail emoji and focus experiments were reverted.
For the currently approved candidate, do not rebuild or substitute another EXE while packaging:
use `artifacts/rc20-baseline-no-t/publish/GachaOverlay.App.exe`, SHA-256
`9FE9EB430B2BFFD419C7A90401518C067D5ED057762C7196AAFB85A6977BAD59`.
Its full Debug/Release suites each passed 1,539 tests. The generic commands above are for future validation, not permission to overwrite earlier artifacts.

## Guide

Use Python with reportlab and pypdf installed:

```powershell
python tools/manual/build_rc_guide.py --output output/pdf/rc20-final/LS-Overlay-2.0-RC-User-Guide-ko.pdf
```

This reads the editable Korean Markdown and existing licensed fonts/logo. It checks 9 pages, glyph coverage and the public links.
Render with Poppler and inspect every page before packaging. Do not collect app screenshots automatically.
The current guide is complete without screenshots.

## Assemble once, after validation

```powershell
pwsh -NoProfile -File tools/release/package-ls-rc.ps1 `
  -PublishRoot artifacts/rc20-baseline-no-t/publish `
  -GuidePath output/pdf/rc20-final/LS-Overlay-2.0-RC-User-Guide-ko.pdf `
  -DebugTrxPath tests/GachaOverlay.Tests/TestResults/rc-baseline-no-t-debug.trx `
  -ReleaseTrxPath tests/GachaOverlay.Tests/TestResults/rc-baseline-no-t-release.trx `
  -OutputRoot artifacts/releases/2.0.0-rc.1-final `
  -ExpectedExeSha256 9FE9EB430B2BFFD419C7A90401518C067D5ED057762C7196AAFB85A6977BAD59
```

The helper requires full Debug/Release TRX results, checks metadata, copies only approved public files plus existing license notices,
creates and hashes the final ZIP, extracts it into a new temporary directory and compares every file.
It requires explicit current test evidence and the approved executable hash, and refuses to overwrite any existing output directory.
The final no-T set is `artifacts/releases/2.0.0-rc.1-final`; `artifacts/releases/2.0.0-rc.1` remains an immutable historical preparation, not the selected release set.

The public EXE is renamed at packaging time only. Its managed assembly remains `GachaOverlay.App`; all pack URIs remain valid.
One isolated launch holds the existing single-instance mutex so startup loads the bundled runtime and App.xaml resources,
then exits **before** profile/network/HUD startup. This is not a full interactive launch, screenshot capture or Diagnostic ZIP test.
Actual auto-login, HUD and extended use remain manual RC checks.

The public manifest records the base Git commit **and uncommitted RC changes**, not a claim that the base commit alone reproduces the bytes.
Release assembly/file version is 2.0.0.0; ProductVersion/InformationalVersion is exactly 2.0.0-rc.1.
The EXE is not code-signed. LocalAppData, DPAPI, single-instance name and auto-start registration name are unchanged.
Auto-start updates its executable path on normal startup when enabled; users should update old shortcuts.

## Review and publication

See [release body](../../docs/releases/LS-Overlay-2.0.0-rc.1-github-release.md) and [test plan](../../docs/releases/LS-Overlay-2.0.0-rc.1-test-plan.md).
Title: **LS Overlay 2.0.0 RC1**. Suggested tag: **v2.0.0-rc.1**. **Pre-release = ON**.
Upload only the final ZIP, guide PDF, checksum file and optionally public manifest after explicit user approval.
Once published, do not replace rc.1 assets; use rc.2 for fixes.

Diagnostic Bundle automated-test flake: **INVESTIGATION DEFERRED**, production impact **NOT CONFIRMED**.
No retries, instrumentation, stress loops or Diagnostic corrective belong to this RC preparation.
