# RC T-key Discord focus correction

Status: **CANCELLED / HISTORICAL — T FEATURE REMOVED BY USER REQUEST**

All post-RC focus experiments were cancelled on 2026-09-03. Their binaries are retained for recovery, not recommended for use. The evidence below is historical.

## Evidence

The separate diagnostic executable recorded an accepted shortcut followed by
`ForegroundRejected` during the user's GTA5 Enhanced test. The user confirmed
no visible response. This is not an absent Discord process or a disabled option:
the old low-level hook consumed the input and requested activation without a
native hotkey event, and the request was rejected.

## Correction

The input thread registers modifier-free T only while the feature is enabled
and GTA5 Enhanced is foreground. It listens for foreground changes independently
of HUD visibility mode. The first eligible physical key-down reaches the real
Windows `RegisterHotKey` / `WM_HOTKEY` path. The existing key policy continues to
consume repeat and release events so T does not leak into Discord after focus
changes. The activation is coalesced and rechecks the game foreground condition.

Outside-game, modifier and injected input unregister the hotkey before passing
through; held keys cannot turn into new activation requests. Registration
conflicts leave the input usable. Shutdown removes the foreground hook, keyboard
hook, message handler and owned registration. No synthetic key/mouse input,
`AttachThreadInput`, privilege change, foreground-lock setting change, Discord
launch, channel selection, protocol change or server deployment was added.

References: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[WM_HOTKEY](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey),
[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).

## Validation

Focused routing, diagnostics, existing input policy and lifecycle tests: 47 PASS.
Debug and Release full regression: 1,566 PASS each, zero failures/skips.
Debug and Release builds: zero warnings/errors. Formatting verification and
`git diff --check` passed. Tests use fake native registration/activation actions;
they do not inject keyboard input into the user's desktop.

Local EXE: 78,482,888 bytes; product version `2.0.0-rc.1`.
SHA-256: `841EEC1E6097EC6F43914AFE523EA0A8A48B840D0C220FB35EE818D1CE6AA907`.

## Local executable and manual checks

`artifacts/rc20-discord-focus-fix/wpf-win-x64/LSOverlay.exe`

This includes the preceding Sales Detail emoji correction. The prepared public
RC ZIP and its checksums remain unchanged. No commit, push or release was made.

Exit the preceding overlay from its tray menu before starting this executable.
Confirm in the actual game that T activates the already-running Discord window,
holding T does not type repeated characters, and normal T/Shift+T typing still
works in Discord and other applications. Also confirm F9/F10 remain functional.
Real foreground activation is not established by mocked routing tests alone.

## Live result

The corrected executable's user-session log recorded `request accepted` at
17:26:57.399 +09:00 on 2026-09-03, followed by `result=Activated` at 17:26:57.402.
The actual `SetForegroundWindow` call returned success, contrasting with the
diagnostic executable's earlier `ForegroundRejected`. Visual confirmation,
hold/release leakage and normal typing/F9/F10 checks remain user-confirmation
items; they are not inferred from this single success log.

## Subsequent user feedback

The user subsequently confirmed that the game lost focus and the cursor moved
to the center, but the Discord chat window did not appear. F9 and F10 worked.
Therefore this version did not pass the visual acceptance check. Window
inspection found a Discord game-overlay tool window sharing the same Electron
class as the normal Discord window. The old selector did not inspect extended
window styles. A separate main-window selection correction supersedes this EXE;
the `Activated` native return must not be treated as proof that the intended
chat window was selected or displayed. No cursor-movement API was added by
this version.
