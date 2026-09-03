# RC T-key Discord focus: diagnostic follow-up

Status: **CANCELLED / HISTORICAL — T FEATURE REMOVED BY USER REQUEST**

All post-RC focus experiments were cancelled on 2026-09-03. Their binaries are retained for recovery, not recommended for use. The evidence below is historical.

The user reports no visible response to T with GTA5 Enhanced active. Read-only
checks found the shortcut enabled, the expected game process, a visible eligible
Discord window, and matching elevation for game, Discord and overlay. Those
checks do not establish which stage failed at the time of the key press.

The previous implementation ignored the result of `SetForegroundWindow` and
silently ignored expected window-API exceptions. This follow-up reports hook
installation and accepted shortcut outcomes: `Activated`, `WindowNotFound`,
`ForegroundRejected`, `ForegroundChanged`, or `WindowApiFailure`.

The diagnostic records fixed action/result labels only, not typed text, window
titles, paths, Discord message content or exception messages. Logging runs
outside the low-level keyboard callback. No synthetic input, foreground-policy
bypass, Discord launch, channel selection or new retry loop was added. Existing
modifier, injected-input, repeat and key-up rules remain unchanged.

Microsoft documents that foreground requests may be rejected; this is a
possible cause when this diagnostic was prepared:
[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).

The separate local executable includes the preceding Sales Detail emoji fix:
`artifacts/rc20-discord-focus-diagnostic/wpf-win-x64/LSOverlay.exe`.
The prepared RC ZIP, its manifest and checksums are not replaced. No commit,
push, public release or server/configuration change is part of this diagnostic.

Manual check: exit the previous overlay from its tray menu, launch the diagnostic
EXE, activate the GTA game window, press T once and report the visible result.
Inspect only the new `Quick Discord focus` log lines from this launch.

Validation: focused tests 32 passed; Debug and Release full suites each passed
1,551 tests with no failures or skips. Both builds completed with zero warnings
and errors. Formatting verification and `git diff --check` passed.

Diagnostic EXE: 78,481,903 bytes, product version `2.0.0-rc.1`.
SHA-256: `8D971D4EB1E180D8B8BE4EB9A5CA3A86A1D93C326727C5311792E93BED123165`.

## User reproduction, 2026-09-03

The diagnostic log recorded `request accepted` followed by `ForegroundRejected`
at 17:16:40 +09:00. The user confirmed no visible response. Keyboard detection,
the GTA eligibility check and Discord window discovery succeeded; the native
foreground request returned false. The previous code consumed T without
receiving a Windows `WM_HOTKEY` input event, then ignored that failed request.
The subsequent native-hotkey correction is documented separately.
