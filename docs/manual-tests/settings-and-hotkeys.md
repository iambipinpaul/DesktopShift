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
| Seven sections | Open Settings and scroll from top to bottom. | Startup, Assignment, Compatibility, Notifications, Appearance, Diagnostics, and Global shortcuts are present. |
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
