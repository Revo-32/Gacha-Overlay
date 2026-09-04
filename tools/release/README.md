# LS Overlay 2.0.0 - local stable release engineering

This workflow prepares Stable locally. It never commits, pushes, tags, uploads, deploys, or changes Railway, DNS, or the Discord Developer Portal. Published RC1 and RC2 tags, assets, and checksums remain immutable.

The current manifest is `ls-2.0.0.json`. The older `release-manifest.json`, `build-release.ps1`, and `verify-release.ps1` are historical 1.0 tooling.

## Minimal Stable validation

Run from the repository root on Windows x64 with the .NET 8 SDK:

```powershell
dotnet build GachaOverlay.sln -c Release
dotnet test tests/GachaOverlay.Tests/GachaOverlay.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~GachaOverlay.Tests.Release.Rc20MetadataTests|FullyQualifiedName~GachaOverlay.Tests.M110FinalPolishTests.Protocol_" --logger "trx;LogFileName=stable-focused.trx" --results-directory artifacts/stable-preparation/tests
dotnet publish src/GachaOverlay.App/GachaOverlay.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/stable-preparation/wpf-win-x64
dotnet publish src/LSOverlay.Backend/LSOverlay.Backend.csproj -c Release -r linux-x64 --self-contained false -o artifacts/stable-preparation/backend-linux-x64
git diff --check
```

The WPF publish uses the same self-contained, compressed, single-file semantics proven by RC2. The public package contains only the renamed WPF executable, user documents, and approved license notices. Backend output is validation-only and never enters the public ZIP.

## Guide

```powershell
python tools/manual/build_rc_guide.py --output output/pdf/stable/LS-Overlay-2.0-User-Guide-ko.pdf
```

The builder checks the nine-page Korean text-first guide, embedded glyph coverage, and public links. Rendered pages must be checked for clipping before packaging.

## Assemble once

After calculating the final WPF executable SHA-256:

```powershell
pwsh -NoProfile -File tools/release/package-ls-rc.ps1 `
  -ManifestPath ls-2.0.0.json `
  -PublishRoot artifacts/stable-preparation/wpf-win-x64 `
  -GuidePath output/pdf/stable/LS-Overlay-2.0-User-Guide-ko.pdf `
  -FocusedTrxPath artifacts/stable-preparation/tests/stable-focused.trx `
  -OutputRoot artifacts/releases/2.0.0 `
  -ExpectedExeSha256 <FINAL-WPF-EXE-SHA256>
```

The helper refuses to overwrite an existing release directory, verifies version and product metadata, requires passing test evidence, allows only approved files, checks the fresh ZIP extraction byte-for-byte, and writes final SHA-256 values. Its isolated launch uses the existing single-instance exit path and does not start profile, network, or interactive HUD behavior.

- Release title: **LS Overlay 2.0.0**
- Suggested tag: **v2.0.0**
- Pre-release: **NO**

Do not publish until the user has reviewed the local artifacts. Once published, Stable assets and checksums are immutable; later fixes require a new version.

After Stable is published, create `develop/2.1` from the exact commit tagged `v2.0.0`. Do not create that branch during release preparation.

Developer note: Diagnostic memory metric UX work remains deferred and is not a 2.0 release blocker.
