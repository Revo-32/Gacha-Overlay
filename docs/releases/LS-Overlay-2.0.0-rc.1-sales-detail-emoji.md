# RC1 review corrective: Sales Detail emoji

**CANCELLED by user request on 2026-09-03.** This post-RC change was reverted when returning to the release-preparation baseline. The following is historical validation only; it does not describe the current no-T build.

2026-09-03. User-reported raw `<:SELL_SP:...>` in the read-only sale detail text.

## Cause and scope

The detail template bound canonical `DetailSource` directly to a plain TextBlock. Unlike chat it never tokenized custom emoji or loaded their images.

The detail now uses the existing chat tokenizer, CrispOutlinedText inline layout and the same bounded DiscordMediaAssetService emoji cache. No second HTTP client/cache is added. Valid custom markup renders as an inline image; disabled/unavailable images retain `:name:` rather than raw IDs. Animated markup uses the existing chat static PNG policy.

Only loaded, visible detail surfaces request images. Repeated IDs in a row share a request; replacement, hide and unload cancel the old wait and reject late results. The detail stays read-only and non-hit-testable. The existing global custom-emoji setting is respected.

Canonical sales content/parser, order, Sold confirmation, human reactions, protocol, Backend and Diagnostic are unchanged. No server deployment is required.

## Validation

- Debug and Release: **1,540 / 1,540 PASS**, failed 0 / skipped 0; final build warnings 0 / errors 0.
- Format and git diff check: PASS.
- Three new tokenizer cases plus four UI scenarios within the existing WPF Application test scope: image rendering/deduplication, fallback/disabled, late-result cancellation, actual Sales template/lock policy.
- Initial standalone UI cases passed alone but failed after the suite's sole WPF Application had shut down. They now run before that existing Application scope closes; all UI assertions remain. No global parallelization changes, skips, retries or sleeps were introduced.
- Preserved intermediate TRX files document the test-harness failures. Final evidence: `sales-detail-emoji-debug-final.trx`, `sales-detail-emoji-release-final.trx` in the test project's TestResults.
- The screenshot's emoji CDN returned HTTP 200 / image/png. Actual user HUD verification remains pending.

## Artifact / publication

Corrective EXE: `artifacts/rc20-sales-detail-emoji/wpf-win-x64/LSOverlay.exe` (Release, self-contained win-x64 single-file, internal assembly unchanged).

Existing `artifacts/releases/2.0.0-rc.1/` ZIP/PDF/checksums are untouched and **do not include this corrective**. Their earlier validation report describes that earlier artifact set, not the subsequently edited source. This is a separate local review binary; prepare a fresh approved release artifact set before publishing. Version remains 2.0.0-rc.1 because this is unpublished RC review work, not a newly published release.

No commit, push, tag, upload, GitHub Release, Railway, DNS or Discord Portal mutation.

## User check

1. Fully exit the current app from its tray menu and run the corrective EXE.
2. Unlock HUD and expand the same sale detail; verify the SELL_SP image appears inline.
3. Collapse/expand, then lock/unlock: no raw emoji code, stale image, layout overlap or blocked click-through.
