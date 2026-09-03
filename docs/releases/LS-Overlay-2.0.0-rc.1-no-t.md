# RC baseline restoration and T removal

Follow-up: the user approved this exact executable for release preparation. The packaged candidate, refreshed PDF and final report are now described in `LS-Overlay-2.0.0-rc.1-final-report.md`. No new application build was substituted during packaging.

2026-09-03. Local-only correction; no commit, push, publication or production deployment.

## Scope

Per the user's final direction, return to the release-preparation code baseline and remove the T quick-Discord-focus feature and settings option. Do not continue the subsequent camera/focus experiments or Sales Detail emoji enhancement.

- Baseline: `5a4c10044efcd4cdff1f128261c00f82b3a17c5e` plus the existing RC version/packaging/documentation changes.
- Original prepared EXE SHA-256: `1DFAD6587813D2EA0851B1AC394611004C1AFBD69400302B1872B11E90C3BF3F`. Its manifest confirms the same source base and RC changes.
- Sales detail, chat tokenization, media cache ownership and application composition restored to that baseline, including their original tests.
- T keyboard hook, foreground-window activation service, routing and policy removed. No replacement activation method, synthetic input, cursor movement, delay or retry added.
- T settings UI, view-model property, defaults, translations and exclusive key reservation removed. The existing deprecated-settings filter drops the old flag case-insensitively without resetting other settings or changing the schema.
- F9/F10 and user-configured channel shortcuts remain; no default T binding. Existing HUD, settings-window, modifier-drag, chat, sales, login and backend behavior is not modified.

## Preservation

Original release ZIP, EXE, PDF and hashes remain untouched. Post-RC experimental binaries are retained but superseded. Removed source files were copied to `artifacts/rc20-discord-focus-removal-backup` before removal. User profile, credentials, logs and running processes were not changed by this task.

Editable user instructions and release drafts now describe Alt+Tab instead of T. The archived original PDF/ZIP still describe the old build and were not regenerated or republished in this task.

## Validation

- Debug build: PASS, 0 warnings / 0 errors.
- Debug full suite: 1,539 passed / 0 failed / 0 skipped (`rc-baseline-no-t-debug.trx`).
- Release build: PASS, 0 warnings / 0 errors.
- Release full suite: 1,539 passed / 0 failed / 0 skipped (`rc-baseline-no-t-release.trx`).
- `dotnet format GachaOverlay.sln --verify-no-changes --no-restore`: PASS.
- `git diff --check`: PASS.
- Production-source allowlist audit: PASS. Only existing RC project metadata and T-removal paths differ from the original source base; no untracked production source remains.
- Original RC EXE, ZIP and PDF hashes: unchanged.
- New removal regressions cover legacy true/false flags in schemas 17/18, preserved settings, removed compiled services/properties, unchanged default F9/F10, no implicit T registration, and all three localization resources.
- Obsolete T-feature tests were removed with the feature, not skipped. Other baseline tests are retained. An initial new-test failure exposed the existing unknown-field preservation, so the old flag was added to the existing deprecated-field filter. A test's record-reference comparison was corrected to compare serialized setting values; final full runs pass without retries in production code.

## Executable

`artifacts/rc20-baseline-no-t/wpf-win-x64/LSOverlay.exe`

- Release / win-x64 / self-contained / compressed single-file, using the prepared RC publishing model.
- Version: `2.0.0-rc.1`; file version: `2.0.0.0`.
- Size: 78,475,035 bytes.
- SHA-256: `9FE9EB430B2BFFD419C7A90401518C067D5ED057762C7196AAFB85A6977BAD59`.
- Public filename is a byte-identical copy of the published `GachaOverlay.App.exe`; license files and updated quick-start README are included alongside it. No archived PDF is copied into this correction folder.
- This is a separate local correction build. The original release archive has not been overwritten or uploaded.

## Manual acceptance

The user confirmed the original prepared release worked apart from T. The rebuilt no-T executable still needs an in-game check: no T setting or forced Discord activation, normal Alt+Tab, F9/F10, and HUD click/scroll after unlocking and opening ESC. Removal is not a claim that the independently unconfirmed ESC click issue has been root-caused.
