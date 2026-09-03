# RC T-key: exclude Discord game-overlay windows

Status: **CANCELLED / HISTORICAL — T FEATURE REMOVED BY USER REQUEST**

The user subsequently reported that ordinary Alt+Tab had no camera jump, and that ESC followed by clicking the chat area stopped working with the experimental build. The latter was not root-caused. On 2026-09-03 the user requested returning to the release-preparation baseline and removing T, rather than pursuing more focus changes. The following validation describes only the cancelled experiment.

## User observation and evidence

The preceding native-hotkey fix changed `ForegroundRejected` into a successful
native return, but the user reported that GTA lost focus without displaying the
Discord chat window. F9/F10 still worked. This is a failed visual acceptance
check, regardless of the earlier `Activated` log.

Read-only window metadata showed two captioned, unowned Discord windows using
`Chrome_WidgetWin_1`: a full-screen overlay with extended style `0x2800A8`, and
the normal chat window with extended style `0x200100`. The previous selector
accepted the first visible window with that class without checking tool-window
or transparent styles. It could therefore activate Discord's game overlay.

## Change

The selector now excludes `WS_EX_TOOLWINDOW`, `WS_EX_TRANSPARENT`,
`WS_EX_NOACTIVATE`, and DWM-cloaked windows. Existing Discord process identity,
visibility, caption-presence and ownership checks are retained. Normal topmost
or layered windows are not rejected just for those properties. No window title
content is read or logged. The native result label is now `ActivationAccepted`,
which is not a claim of successful user-visible rendering.

The preceding Windows-native hotkey routing, repeat/release policy and Sales
Detail emoji correction remain included. No cursor movement, synthetic input,
window-position change, Discord setting change, permission change, server
deployment, commit, push or RC ZIP replacement was performed.

References: [Extended window styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles),
[DWM window attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute).

## Validation

Focused selection, routing, diagnostics, existing key policy and lifecycle:
59 PASS. The regression uses synthetic handles with the observed style flags;
no real window is activated by these tests. Full Debug and Release suites each
passed 1,578 tests with zero failures or skips. Debug and Release builds both
completed with zero warnings/errors. Full formatting verification and
`git diff --check` passed.

Local EXE: 78,483,669 bytes; product version `2.0.0-rc.1`.
SHA-256: `ABEB6C6C84BB135F56C65F6376E37674B36C15B0A3E7445AEDD15B9F19CDA549`.

## User check

Exit the previous overlay from the tray and start:
`artifacts/rc20-discord-main-window-fix/wpf-win-x64/LSOverlay.exe`.

With Discord Desktop running and the GTA5 Enhanced game window active, press T.
Confirm the actual Discord chat window becomes visible, not merely that GTA
loses focus. Then verify normal typing, holding/releasing T, and F9/F10.

## Subsequent user confirmation and unresolved issue

The user confirmed that the actual Discord window now opens and F9/F10 work.
The user also reported a sharp in-game camera rotation during the transition,
including when the physical mouse is completely stationary. The description
was approximately turning to face behind the character, not a measured angle.
Therefore this version is not considered fully accepted for the T shortcut.

No cursor movement or input injection call exists in the current transition
path. That does not rule out a side effect of the focus handoff on the game's
mouse processing. Cursor displacement, clipping/capture changes, and comparison
with ordinary Alt+Tab remain diagnostic questions; no cause is yet confirmed
and no cursor correction or input-blocking workaround has been added.
