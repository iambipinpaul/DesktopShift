# Manual Unmanaged Window Sweep Verification

Window placement is total: every window gets one of three answers. A rule that
names the application and targets a Managed Desktop moves its windows, a rule
that names it and says **Anywhere** leaves them where they opened, and a window
no enabled rule names is swept to the **first desktop** — the one Task View calls
"Desktop 1".

Almost all of that is automated. What is not is the part that only exists inside
a running shell: whether a real virtual desktop switch actually follows a window
you just launched, whether Task View still shows the first desktop under the name
*you* gave it, and whether the rule editor visibly hides the controls an Anywhere
rule cannot use.

| Coverage | Where |
| --- | --- |
| The three-tier decision, on every trigger | `tests/DesktopShift.Core.Tests/Observation/UnmanagedWindowSweepTests.cs` |
| The identity-only predicate ignoring triggers, switch policy, and destination | `tests/DesktopShift.Core.Tests/Observation/UnmanagedWindowSweepTests.cs` |
| The follow-on-open switch, and the batch triggers that must not switch | `tests/DesktopShift.Core.Tests/Observation/UnmanagedWindowSweepTests.cs` |
| Position 0 resolved by position rather than by name | `tests/DesktopShift.Core.Tests/Observation/UnmanagedWindowSweepTests.cs` |
| Degrading when the first desktop cannot be located | `tests/DesktopShift.Core.Tests/Observation/UnmanagedWindowSweepTests.cs`, `tests/DesktopShift.Core.Tests/Assignments/TerminalAssignmentAcceptanceTests.cs` |
| A swept window never guessed onto a managed desktop | `tests/DesktopShift.Core.Tests/Assignments/RemoteDesktopAssignmentAcceptanceTests.cs` |
| Pausing automatic assignment stopping the sweep | `tests/DesktopShift.Core.Tests/Hosting/AutomaticAssignmentPauseTests.cs` |
| The shipped desktops, move rules, and two Anywhere rules | `tests/DesktopShift.Core.Tests/Configuration/ConfigurationAcceptanceTests.cs` |
| A version 1 document with no `action`, and the export/import round trip | `tests/DesktopShift.Core.Tests/Configuration/` |
| Anywhere through the editor, the list, validation, and persistence | `tests/DesktopShift.App.Presentation.Tests/Rules/AnywhereDestinationTests.cs` |
| Swept and Anywhere rows reading distinctly in Activity | `tests/DesktopShift.Core.Tests/Diagnostics/SweptWindowActivityTests.cs` |

This document covers what those cannot.

## Before each run

1. Have **at least three** virtual desktops. Rename the first one in Task View to
   something you would notice losing — `Mine`, say. DesktopShift must never
   rename it.
2. Complete first run so the shipped desktops and rules are active.
3. Have an application installed that **no** shipped rule names. Discord, Slack,
   Spotify, or Paint all work. Nothing below should be attempted with Edge,
   Windows Terminal, Visual Studio Code, Visual Studio, or Remote Desktop, all of
   which are named by a shipped rule.
4. Open the Activity page in a second DesktopShift window position, or be ready
   to switch to it, because it is where each result is read.

## Sweep matrix

| Case | Action | Expected result |
| --- | --- | --- |
| Swept on open, with the user | Stand on desktop 3. Launch the unnamed application. | Its window opens on the first desktop and **the current desktop becomes the first desktop**, so you are looking at the window you just launched. It never appears to vanish. |
| Recorded distinctly | Read the newest Activity rows. | The decision row says no rule names this app and that the window is moved to the first desktop. Rule reads "No rule names this app" and target reads "First desktop" — not a raw key in parentheses. |
| One-click rule | On that decision row, press **Create a rule for this app**. | The rule editor opens pre-filled with the application's process name and, for a packaged app, its package family name and app ID. The name and identifier are filled in. No window title, address, or command line appears anywhere in the dialog. |
| The rule takes over | Save that rule against a Managed Desktop, then close and relaunch the application. | The window now goes to the Managed Desktop, and the Activity row names your rule instead of the sweep. |
| Anywhere exempts | Edit the rule and change its destination to **Anywhere**. Relaunch the application from desktop 3. | The window stays on desktop 3. No move, no switch. Activity says an Anywhere rule names this application. |
| Anywhere hides what it cannot use | While Anywhere is selected, open **Advanced**. | The trigger check boxes and the desktop switch policy are not shown. Choosing a Managed Desktop again brings both back with the values they had. |
| Disabling is not exemption | Set the destination back to a Managed Desktop, then switch the rule **off**. Relaunch the application from desktop 3. | The window is swept to the first desktop, because a disabled rule no longer names the application. The editor's help text says exactly this, and points at Anywhere instead. |
| Never on a click | Leave a swept window on the first desktop, stand on desktop 3, and use Alt+Tab or the taskbar to click into it. | The window is not moved and the current desktop is not changed by DesktopShift beyond following your own activation. Nothing teleports mid-interaction. |
| Startup tidies without bouncing | Leave the unnamed application open on desktop 3, exit DesktopShift from the tray, then relaunch it. | Its window is moved to the first desktop, and **the current desktop does not change**. You stay where you signed in. |
| A batch does not thrash | Open the unnamed application on two different desktops, then press **Reassign all**. | Both windows end up on the first desktop and the current desktop never changes during the batch. |
| Already correct | With a swept window already on the first desktop, press **Reassign all** again. | Activity reports the window was already on the first desktop. No move is attempted and no switch happens. |
| Paused means paused | Pause automatic assignment. Launch the unnamed application from desktop 3. | It stays on desktop 3 and produces no decision row. Resume, relaunch, and it is swept again. |
| Shipped exemptions | From desktop 3, open File Explorer and Notepad. | Both open and stay on desktop 3. Neither is swept, and neither is moved to a Managed Desktop. |
| Nothing else is exempt | From desktop 3, open Calculator, Task Manager, Settings, Paint, and Photos. | Every one is swept to the first desktop. None ships as an Anywhere rule — exempting them is the user's decision, and the **Create a rule for this app** button on each swept row is how they make it. |
| Adding an exemption | Use that button on the Calculator row, set the destination to Anywhere, and save. Relaunch Calculator from desktop 3. | It now stays on desktop 3, proving a user-added exemption behaves exactly like a shipped one. |

## The first desktop is borrowed, never owned

| Case | Action | Expected result |
| --- | --- | --- |
| Never renamed | After every case above, open Task View. | The first desktop is still called `Mine`. DesktopShift names only the desktops bound to a Managed Desktop. |
| Not in the catalog | Open the Desktops page. | The first desktop is not listed as a Managed Desktop, has no semantic key, and is not something the page offers to recreate. |
| Found by position | In Task View, drag your desktops so a different one is first. Launch the unnamed application. | The window goes to whichever desktop is now first. The sweep follows the position, not the name and not the previous identity. |
| Overlapping a Managed Desktop | Reorder desktops so a Managed Desktop sits first. Launch the unnamed application. | Its window lands inside that Managed Desktop. This is acceptable and must not loop: the window is simply already correct on the next pass. |

## Limited Mode

| Case | Action | Expected result |
| --- | --- | --- |
| Degrades like any assignment | Force Limited Mode (see `permissions-and-privileges.md`) and launch the unnamed application. | The sweep reports the same kind of unresolved-destination result a rule-driven assignment reports. It does not crash, does not retry in a loop, and does not invent a new failure message. |

## Recording a run

Note the Windows build, how many desktops existed, which desktop was first, and
the application used for the unnamed cases. A sweep result that depends on
desktop order is only reproducible if the order was written down.
