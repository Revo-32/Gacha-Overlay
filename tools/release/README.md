# LS Overlay 2.0.0-rc.2 - local release engineering

This workflow prepares RC2 locally. It never commits, pushes, tags, uploads, deploys, or changes Railway, DNS, or the Discord Developer Portal. The published RC1 tag, assets, and checksums remain immutable.

The current manifest is `ls-2.0.0-rc.2.json`. The older `release-manifest.json`, `build-release.ps1`, and `verify-release.ps1` are historical 1.0 tooling.

## Minimal RC2 validation

Run from the repository root on Windows x64 with the .NET 8 SDK:

```powershell
dotnet build GachaOverlay.sln -c Release
dotnet test tests/GachaOverlay.Tests/GachaOverlay.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~GachaOverlay.Tests.Release.Rc20MetadataTests|FullyQualifiedName~GachaOverlay.Tests.M110FinalPolishTests.Protocol_" --logger "trx;LogFileName=rc2-focused.trx" --results-directory artifacts/rc2-preparation/tests
dotnet publish src/GachaOverlay.App/GachaOverlay.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/rc2-preparation/wpf-win-x64
dotnet publish src/LSOverlay.Backend/LSOverlay.Backend.csproj -c Release -r linux-x64 --self-contained false -o artifacts/rc2-preparation/backend-linux-x64
git diff --check
```

The WPF publish flags are the same self-contained, compressed, single-file semantics used for RC1. The public package contains only the renamed WPF executable, user documents, and approved license notices. Backend output is validation-only and never enters the public ZIP.

## Guide

```powershell
python tools/manual/build_rc_guide.py --output output/pdf/rc20-final/LS-Overlay-2.0-RC2-User-Guide-ko.pdf
```

The builder checks the nine-page Korean text-first guide, embedded glyph coverage, and public links. Rendered pages must be checked for clipping before packaging.

## Assemble once

After calculating the new WPF executable SHA-256:

```powershell
pwsh -NoProfile -File tools/release/package-ls-rc.ps1 `
  -ManifestPath ls-2.0.0-rc.2.json `
  -PublishRoot artifacts/rc2-preparation/wpf-win-x64 `
  -GuidePath output/pdf/rc20-final/LS-Overlay-2.0-RC2-User-Guide-ko.pdf `
  -FocusedTrxPath artifacts/rc2-preparation/tests/rc2-focused.trx `
  -OutputRoot artifacts/releases/2.0.0-rc.2 `
  -ExpectedExeSha256 <CURRENT-WPF-EXE-SHA256>
```

The helper refuses to overwrite an existing RC directory, verifies version and product metadata, requires passing test evidence, allows only approved files, checks the fresh ZIP extraction byte-for-byte, and writes final SHA-256 values. Its isolated launch uses the existing single-instance exit path and does not start profile, network, or interactive HUD behavior.

- Release title: **LS Overlay 2.0.0 RC2**
- Suggested tag: **v2.0.0-rc.2**
- Pre-release: **ON**

Do not publish until the user has reviewed the local artifacts. Once published, RC2 assets and checksums are immutable; later fixes require RC3 or 2.0.0.

Developer note: Diagnostic Bundle automated-test flake investigation remains deferred; no production failure has been confirmed.
