# Manual Settings and Hotkeys Verification

A global hotkey cannot be exercised by a test. `RegisterHotKey` needs a real
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
| Hotkey conflicts and unregistered-until-enabled | `tests/DesktopShift.Core.Tests/Hotkeys/` |
| Win32 modifier and virtual-key translation | `tests/DesktopShift.Windows.Tests/Hotkeys/Win32HotkeyTranslationTests.cs` |
| Desktop switching profiles, digit mapping, and validation | `tests/DesktopShift.Core.Tests/Hotkeys/DesktopSwitchShortcutTests.cs` |
| Desktop switching registration ids and `MOD_NOREPEAT` | `tests/DesktopShift.Windows.Tests/Hotkeys/WindowsDesktopSwitchHotkeyRegistrarTests.cs` |
| Switching to a desktop by position, and the missing-desktop path | `tests/DesktopShift.Core.Tests/Hotkeys/DesktopSwitchShortcutServiceTests.cs` |
| A key picker keeping its key while the list is rebuilt | `tests/DesktopShift.App.Presentation.Tests/Settings/SettingsPresentationTests.cs` |

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
| Nine sections | Open Settings and scroll from top to bottom. | Startup, Assignment, Desktops, Compatibility, Notifications, Appearance, Diagnostics, Global shortcuts, and Desktop switching shortcut are present, in that order. |
| Persisted behavior | Change start-minimized, close-to-tray, next-launch pause, both notification switches, and theme. Exit from the tray, then relaunch. | Every control keeps its value. The chosen theme is applied and automatic assignment starts in the requested pause state. |
| Rapid edits | Quickly change theme and two toggles without waiting between clicks. Leave and return to Settings. | The last value of every edited control remains; a later completion does not revert another control. |
| Portable export | Export configuration, then open the JSON in a text editor. | JSON is indented and readable. It contains semantic desktops, rules, behavior, and shortcuts, but no runtime desktop GUID bindings or reconciliation metadata. Paths below the profile use `%USERPROFILE%`; the account name is absent. |
| Valid import | Export, change a harmless setting in the JSON, and import it. | The candidate is validated, becomes active, and its live theme, notification, startup, and hotkey behavior is applied. |
| Invalid import recovery | Add two enabled shortcuts with the same chord or duplicate a rule ID, then import. | Every imported entry remains available for correction, the issue identifies its JSON path, and the previous valid configuration continues running. Export still exports the previous active snapshot. |
| Unreadable import | Import malformed JSON and then a JSON array. | Each is rejected as an unreadable configuration; the current candidate and active snapshot are unchanged. |
| Diagnostics | Open the log location, export a diagnostic bundle, then clear local activity. | Explorer opens the local log folder, the chosen ZIP is written, and DesktopShift-created activity/log content is cleared without uploading anything. |
| Notification switches | Disable assignment-failure notifications and produce a controlled failed assignment; repeat while enabled. Do the same with a Limited Mode/failed compatibility result. | Disabled categories remain silent. Enabled failures/warnings notify. Successful assignments never notify. |

## Global shortcut matrix

Use chords that do not overlap Windows or the second application.

| Case | Action | Expected result |
| --- | --- | --- |
| Disabled by default | Leave the master switch off, enable individual rows, and press their displayed chords in the second application. | The second application receives the keys; DesktopShift performs no command. |
| Reassign all | Enable shortcuts and press the Reassign all chord. | One manual reassignment batch runs, including while automatic assignment is paused. Repeated keydown does not queue duplicate batches. |
| Reassign foreground | Focus a window with a matching rule and press the foreground chord. | Only that foreground HWND is sent through the ordinary rule/assignment pipeline. |
| Pause/resume | Press the pause chord twice. | The tray state changes to Paused, then Running. Explicit reassignment remains available while paused. |
| Open | Hide DesktopShift to the notification area and press the open chord. | The existing DesktopShift window is restored and activated; no second window is created. |
| Candidate conflict | Give two enabled rows the same chord and apply. | The candidate is retained, the conflicting row/path is shown, and the previous registrations remain active. |
| Windows registration conflict | Have another application claim a chord, assign it in DesktopShift, and apply. Restart DesktopShift with that chord still claimed. | Settings immediately reports that the specific chord could not be registered, including the Windows conflict, without requiring another Apply click. Other valid chords remain available. |
| Disable and edit | Turn the master switch off, then change chords and apply. | Every DesktopShift registration is released before validation; no edited chord is claimed while the master switch is off. |
| Shutdown disposal | Enable shortcuts, exit DesktopShift from the tray, then claim the same chords in the second application or relaunch DesktopShift. | Every chord is free immediately after exit and registers normally on relaunch. No hidden hotkey window or registration survives shutdown. |
| Rebuilt list survives | Scroll until the shortcut rows are on screen, then apply any settings change twice in a row. | DesktopShift stays running and each row still shows the key it was saved with. The list is rebuilt from scratch on every save, and the key pickers are reused across those rebuilds. |

## Desktop switching matrix

Have at least three virtual desktops open before starting, and no more than four,
so both the "desktop exists" and "desktop does not exist" cases are reachable.

Whether `Win + Alt + a number` can be registered at all is decided by the shell
on the machine under test: if Explorer claimed the Jump List shortcuts first,
Windows refuses DesktopShift's request. Both outcomes are correct behaviour, and
the point of the case below is that the user is told which one they got. Record
the result — it is the only way to find out how this behaves in practice.

| Case | Action | Expected result |
| --- | --- | --- |
| Disabled by default | Leave the desktop switching switch off and press `Ctrl + Alt + 2` in the second application. | The second application receives the keys; the desktop does not change. |
| Recommended profile | Enable switching with `Ctrl + Alt + Number` and apply. Press `Ctrl + Alt + 1`, then `2`, then `3`. | The foreground moves to Desktops 1, 2, and 3. The status reports 10 of 10 registered. |
| Desktop ten on zero | With ten desktops open, press the profile's `0`. | The foreground moves to Desktop 10, not Desktop 1. |
| Number row only | Press the profile's modifiers with the **numeric keypad** digits. | Nothing happens. Only the top row switches desktops. |
| Held key | Press and hold `Ctrl + Alt + 3` for several seconds. | Exactly one switch occurs. The desktop does not cycle or flicker while the key is held. |
| Missing desktop | With four desktops open, press the profile's `7`. | A notification says there is no Desktop 7 and names how many desktops exist. The foreground desktop does not change. |
| Already current | Press the shortcut for the desktop already in front. | Nothing happens and nothing is reported. |
| Windows override warning | Select `Win + Alt + Number` **without** applying. | A warning appears naming the taskbar Jump List shortcut before anything is claimed. |
| Windows override outcome | Apply the `Win + Alt + Number` profile, then press `Win + Alt + 2`. | Either the desktop switches and the taskbar Jump List no longer opens, **or** Settings reports the specific desktops Windows refused and names the taskbar as the cause. Silence is a failure. |
| Custom profile | Select Custom, tick Ctrl, Alt, and Shift, and apply. | The summary line updates before applying. `Ctrl + Alt + Shift + 2` switches to Desktop 2; the old `Ctrl + Alt + 2` no longer does. |
| Custom with no modifier | Select Custom and untick every box. | Applying is refused with a message about the number keys, and no bare digit is ever claimed. Typing numbers still works everywhere. |
| Profile exchange | Switch from Custom back to `Ctrl + Alt + Number` and then to Custom again. | The custom ticks are still as they were left. |
| Restored at startup | Enable a profile, exit from the tray, and relaunch. | The same profile is registered again without opening Settings. |
| Shutdown disposal | With a profile enabled, exit DesktopShift and press its combinations. | Every combination is free immediately. With `Win + Alt + Number`, the taskbar Jump Lists work again. |
| Limited Mode | Force the provider into Limited Mode and press a switching shortcut. | A notification says switching is unavailable on this machine. Nothing is left half-switched. |
