# Manual Settings and Desktop Switching Verification

A desktop-switching hotkey cannot be exercised by a test. `RegisterHotKey` needs a real
message loop and takes its key combination away from every other application on
the machine for as long as it is held, so an automated test that called it would
break the machine it ran on. A file picker, a notification-area balloon, and a
theme applied to a live shell are in the same position: they exist only inside a
running application.

Everything else is automated:

| Coverage | Where |
| --- | --- |
| Every settings section is present, named, and ordered | `tests/DesktopShift.App.Presentation.Tests/Settings/SettingsPresentationTests.cs` |
| Settings persistence and legacy documents | `tests/DesktopShift.Core.Tests/Configuration/BehaviorSettingsPersistenceTests.cs` |
| Theme persistence | `tests/DesktopShift.Core.Tests/Appearance/` |
| Ordered schema migrations | `tests/DesktopShift.Core.Tests/Configuration/ConfigurationMigrationPipelineTests.cs` |
| Import recovery and the candidate/active split | `tests/DesktopShift.Core.Tests/Configuration/ConfigurationImportTests.cs` |
| Export portability and privacy | `tests/DesktopShift.Core.Tests/Configuration/ConfigurationExportTests.cs` |
| Win32 modifier and virtual-key translation | `tests/DesktopShift.Windows.Tests/Hotkeys/Win32HotkeyTranslationTests.cs` |
| Desktop switching profiles, digit mapping, and validation | `tests/DesktopShift.Core.Tests/Hotkeys/DesktopSwitchShortcutTests.cs` |
| Desktop switching registration ids and `MOD_NOREPEAT` | `tests/DesktopShift.Windows.Tests/Hotkeys/WindowsDesktopSwitchHotkeyRegistrarTests.cs` |
| Switching to a desktop by position, and the missing-desktop path | `tests/DesktopShift.Core.Tests/Hotkeys/DesktopSwitchShortcutServiceTests.cs` |

This document covers what those cannot.

## Before each run

1. Sign in normally. DesktopShift is **not** running.
2. Note the contents of `%LOCALAPPDATA%\DesktopShift` so a change is observable.
3. Have a second application open that you can type into, so a stolen key
   combination is noticeable immediately.
4. Know how to reach Task Manager without a keyboard shortcut, in case a chord
   you assign turns out to conflict with something you rely on.

## Settings and configuration matrix

| Case | Action | Expected result |
| --- | --- | --- |
| Eight sections | Open Settings and scroll from top to bottom. | Startup, Assignment, Desktops, Compatibility, Notifications, Appearance, Diagnostics, and Desktop switching shortcut are present, in that order. |
| Persisted behavior | Change start-minimized, close-to-tray, next-launch pause, local activity recording, both notification switches, and theme. Exit from the tray, then relaunch. | Every control keeps its value. The chosen theme is applied, activity recording resumes in the requested state, and automatic assignment starts in the requested pause state. |
| Rapid edits | Quickly change theme and two toggles without waiting between clicks. Leave and return to Settings. | The last value of every edited control remains; a later completion does not revert another control. |
| Portable export | Export configuration, then open the JSON in a text editor. | JSON is indented and readable. It contains semantic desktops, rules, behavior, and the desktop-switching profile, but no runtime desktop GUID bindings or reconciliation metadata. Paths below the profile use `%USERPROFILE%`; the account name is absent. |
| Valid import | Export, change a harmless setting in the JSON, and import it. | The candidate is validated, becomes active, and its live theme, notification, startup, and desktop-switching behavior is applied. |
| Invalid import recovery | Select the custom desktop-switching profile with no modifier or duplicate a rule ID, then import. | Every imported entry remains available for correction, the issue identifies its JSON path, and the previous valid configuration continues running. Export still exports the previous active snapshot. |
| Unreadable import | Import malformed JSON and then a JSON array. | Each is rejected as an unreadable configuration; the current candidate and active snapshot are unchanged. |
| Diagnostics | Confirm **Record local activity** starts off on a fresh configuration. Enable it, create an assignment, open the log location, export a diagnostic bundle, disable it, create another assignment, then clear local activity. | The first assignment is retained in Activity and the rotating log; the assignment made while off is not. Explorer opens the local log folder, the chosen ZIP is written, disabling did not delete prior history, and Clear removes DesktopShift-created activity/log content without uploading anything. |
| Notification switches | Disable assignment-failure notifications and produce a controlled failed assignment; repeat while enabled. Do the same with a Limited Mode/failed compatibility result. | Disabled categories remain silent. Enabled failures/warnings notify. Successful assignments never notify. |

## Desktop switching matrix

Have at least three virtual desktops open before starting, and no more than four,
so both the "desktop exists" and "desktop does not exist" cases are reachable.

There is no `Win + Alt + Number` profile. It was offered once and removed: the
shell holds those ten combinations from boot, so `RegisterHotKey` refuses every
one of them and the profile could never register anything. Nothing but a
low-level keyboard hook can take them, and DesktopShift deliberately does not
install one, so there is nothing to test here. A custom profile can still
include the Windows key alongside other modifiers.

| Case | Action | Expected result |
| --- | --- | --- |
| Disabled by default | Leave the desktop switching switch off and press `Ctrl + Alt + 2` in the second application. | The second application receives the keys; the desktop does not change. |
| Recommended profile | Enable switching with `Ctrl + Alt + Number` and apply. Press `Ctrl + Alt + 1`, then `2`, then `3`. | The foreground moves to Desktops 1, 2, and 3. The status reports 10 of 10 registered. |
| Desktop ten on zero | With ten desktops open, press the profile's `0`. | The foreground moves to Desktop 10, not Desktop 1. |
| Number row only | Press the profile's modifiers with the **numeric keypad** digits. | Nothing happens. Only the top row switches desktops. |
| Held key | Press and hold `Ctrl + Alt + 3` for several seconds. | Exactly one switch occurs. The desktop does not cycle or flicker while the key is held. |
| Missing desktop | With four desktops open, press the profile's `7`. | A notification says there is no Desktop 7 and names how many desktops exist. The foreground desktop does not change. |
| Already current | Press the shortcut for the desktop already in front. | Nothing happens and nothing is reported. |
| Claimed combination | Select Custom, tick only Win and Alt, and apply. | Settings reports the desktops Windows refused and how many are active. Silence is a failure. The taskbar Jump Lists keep working. |
| Custom profile | Select Custom, tick Ctrl, Alt, and Shift, and apply. | The summary line updates before applying. `Ctrl + Alt + Shift + 2` switches to Desktop 2; the old `Ctrl + Alt + 2` no longer does. |
| Custom with no modifier | Select Custom and untick every box. | Applying is refused with a message about the number keys, and no bare digit is ever claimed. Typing numbers still works everywhere. |
| Profile exchange | Switch from Custom back to `Ctrl + Alt + Number` and then to Custom again. | The custom ticks are still as they were left. |
| Restored at startup | Enable a profile, exit from the tray, and relaunch. | The same profile is registered again without opening Settings. |
| Shutdown disposal | With a profile enabled, exit DesktopShift and press its combinations. | Every combination is free immediately and can be claimed by another application. |
| Limited Mode | Force the provider into Limited Mode and press a switching shortcut. | A notification says switching is unavailable on this machine. Nothing is left half-switched. |
