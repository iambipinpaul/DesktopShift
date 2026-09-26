# Manual Show on all desktops (per-app pin) Verification

An Application Rule can keep its application on **every** virtual desktop. The
windows it claims are *pinned* — Windows Task View's "Show windows from this app
on all desktops" — instead of being moved, so the app is at hand wherever the
user is, and DesktopShift never relocates it while the rule holds.

Almost all of that is automated. What is not is the part that only exists inside
a running shell: whether the private pinned-apps surface is really laid out where
DesktopShift believes it is on this build, whether Task View shows the window as
pinned, and whether a pin survives the things a user actually does — switching
desktops, restarting, disabling the rule, pressing **Reassign all**.

| Coverage | Where |
| --- | --- |
| A pin rule projects to the pinned destination, and answers its own event set whatever its stored triggers say | `tests/DesktopShift.Windows.Tests/Observation/WindowRuleMatcherTests.cs`, `tests/DesktopShift.Windows.Tests/Observation/WindowObservationProcessorTests.cs` |
| A pin never resolves a desktop, never moves, never switches, and reads as pinned / already pinned / unavailable / failed | `tests/DesktopShift.Core.Tests/Assignments/WindowAssignmentServiceTests.cs` |
| Releasing a pin before a move, and leaving the window alone when the release is refused | `tests/DesktopShift.Core.Tests/Assignments/WindowAssignmentServiceTests.cs` |
| A held pin is never released by a foreground activation or a lifecycle event, and a stale pin is released on the placement-repairing events even when no rule answers them or the destination cannot be resolved | `tests/DesktopShift.Core.Tests/Assignments/WindowAssignmentServiceTests.cs`, `tests/DesktopShift.Windows.Tests/Observation/WindowObservationProcessorTests.cs` |
| Releasing a pin an Anywhere rule no longer holds, on the events that repair placement only, and reporting a release that is refused | `tests/DesktopShift.Windows.Tests/Observation/WindowObservationProcessorTests.cs` |
| Pinning keeps the window in place for tiling rather than reporting a move | `tests/DesktopShift.Windows.Tests/Observation/WindowObservationProcessorTests.cs` |
| The capability is advertised only after its own read-only probe, and a failed probe costs pinning only | `tests/DesktopShift.Windows.Tests/VirtualDesktops/ValidatedVirtualDesktopTopologyProviderTests.cs` |
| The pin surface is never reached on a build the adapter has not admitted | `tests/DesktopShift.Windows.Tests/VirtualDesktops/ValidatedVirtualDesktopTopologyProviderTests.cs`, `tests/DesktopShift.Windows.Tests/VirtualDesktops/NativeBridgeOptInContractTests.cs` |
| The editor's third destination, its hidden placement controls, and the stored key surviving a round trip | `tests/DesktopShift.App.Presentation.Tests/Rules/ShowOnAllDesktopsDestinationTests.cs` |
| Pin rows reading as pins in Activity and never as move failures | `tests/DesktopShift.App.Presentation.Tests/ViewModels/AssignmentActivityPresentationTests.cs`, `tests/DesktopShift.Core.Tests/Diagnostics/ActivityRecordFactoryTests.cs` |
| A rule whose target key no longer resolves still validates while it pins | `tests/DesktopShift.Core.Tests/Configuration/ApplicationRuleEditingTests.cs` |

This document covers what those cannot.

## Before each run

1. Have **at least three** virtual desktops.
2. Confirm this machine's build is one whose pin surface was admitted. Today that
   is build 26100 and 26200. On any other build — or after a probe that failed —
   pinning is deliberately *unavailable*, and the Limited-Mode cases below are
   the expected result instead of the normal ones.
3. Complete first run so the shipped desktops and rules are active.
4. Pick an application **no** shipped rule names — Music or Paint will do — and
   open the Activity page with **Record local activity** enabled, because that is
   where each result is read.
5. Add the rule under test: **Rules → New application rule**, name the app by its
   process name (and its package family name if it ships as a package), and
   choose **Show on all desktops — pin this app everywhere**.

## Pin matrix

| Case | Action | Expected result |
| --- | --- | --- |
| Pinned on open | Stand on desktop 2 and launch the app. | The window appears on desktop 2 and **does not move**. Activity shows a matched row for your rule and a pin that succeeded. The desktop does not switch. |
| Everywhere at once | Switch to desktop 3, then back to desktop 2, with the app's window open. | The window is visible on both. Task View shows it as pinned — the same state you would get by pinning it there by hand. |
| Never on a click | With the pinned window visible on desktop 3, click into it there. | Nothing moves. The row, if one is recorded, re-asserts the pin and reports no move and no switch. |
| Still pinned after the rule changed | Change the rule's destination to a Managed Desktop, then click the still-pinned window — or hide, minimize, or restore it — without pressing anything else. | Nothing moves and the pin stays: none of those events repairs placement. Activity says the window stays pinned and its move waits for the next placement event — pressing **Reassign all** is the obvious way to supply one. |
| A window with no pin still moves on a click | With the same move rule and a window of the app that was never pinned, click that window on the wrong desktop. | It moves as it always did. Only a window that actually holds a pin is left alone. |
| Second window follows | While the app is pinned, make it open another window. | The new window is pinned too, on its own observed event. It never appears on one desktop only. |
| Pinning is not a move | Read the newest Activity rows. | The pin row says the window was pinned to all desktops, shows no target desktop, and reports the switch as not requested. It must not read as a move. |
| Tiling still applies | With tiling enabled, press the tiling reconciliation key (or drag the window) while it is pinned. | The window is laid out **within the current desktop** like any other window, and is never moved to another desktop by tiling. |
| Unnamed app still swept | From desktop 3, launch an unrelated app no rule names. | It is still swept to the first desktop. Pinning one app does not exempt anything else. |

## Unpin matrix — the pin follows the rule

Disabling or deleting a pin rule is deliberately **lazy**: DesktopShift does not
sweep the desktop unpinning windows. The pin is released on the window's next
placement-repairing event — created, shown, startup reconciliation, or manual
reassignment — together with the placement that event decides. A click or a
lifecycle event (hide, minimize, restore, state change) is not a repair, so it
leaves the pin, and the move it defers, alone.

| Case | Action | Expected result |
| --- | --- | --- |
| Disable the rule | Switch the rule **off**, then make the app open or show a window — or press **Reassign all**. | On that event the window's pin is released and it stops appearing on other desktops. Until then, the windows that were already open stay pinned: that is the documented behaviour, not a stuck pin. |
| Delete the rule | Delete it, then relaunch the app from desktop 3. | The relaunched window is unnamed, so it is swept to the first desktop like any other unmanaged window. |
| Switch to a Managed Desktop | Change the rule's destination to a Managed Desktop, then press **Reassign all**. | Each window has its pin released **before** it is moved, and ends on the managed desktop. If a release is refused, the window is left where it is and Activity reports the refusal instead of moving a still-pinned window. |
| Released even when the new rule answers nothing | Change the rule to a Managed Desktop and turn off every trigger except **Foreground activated**, then press **Reassign all**. | The pin is released on that event even though no rule answered it: the window stops appearing on every desktop, and Activity reports that no rule matched. Clicking the window afterwards moves it per the rule. |
| Released even when the destination no longer resolves | Point the rule at a Managed Desktop, delete that desktop so the rule's key stops resolving, then press **Reassign all**. | The pin is released on that event even though the move cannot place the window: the window stops appearing on every desktop, and Activity reports the unresolved destination instead of a still-pinned window. |
| Switch to Anywhere | Change the destination to Anywhere, then show the window again. | The pin is released on that event and the window stays where it is. Activity says an Anywhere rule names this application — or, if the release was refused, that the window may still appear on every desktop, which is worth reporting rather than hiding. |
| The stored key round-trips | Select Show on all desktops, then select a Managed Desktop again, and save both times. | The editor does not clear the desktop, trigger, or switch-policy values while the rule pins, so switching back restores a working rule. |
| Windows pinned it by hand | Pin an application in Task View yourself, then give it a rule that pins it, and later a rule that moves it. | While the move rule is in force, DesktopShift releases the pin before moving the window. This is deliberate: the app is named by a rule, so its placement belongs to DesktopShift. |

## Restart and repair

| Case | Action | Expected result |
| --- | --- | --- |
| Survives sign-out | Sign out and back in with the app open and its pin rule enabled. | The windows are pinned again during startup reconciliation, without being moved. Activity shows the pin succeeding on a startup event. |
| Reassign all converges | Turn the rule off, press **Reassign all**, then turn it back on and press it again. | The first batch releases the pins and places the windows per the remaining rules; the second re-pins them. Nothing moves between desktops while the pin rule holds. |

## When pinning is unavailable

| Case | Action | Expected result |
| --- | --- | --- |
| Limited Mode is honest | On a build whose pin surface was not admitted, or with the compatibility test forced to Limited Mode, make the rule pin an app. | The window stays where it opened. Activity reports the pin as unavailable — a deliberate skip with a reason, **not** a failed move and not silence. |
| Nothing else degrades | In the same state, use a move rule, the sweep, desktop creation, and desktop switching. | All of them keep working exactly as they did before this feature: only pinning is unavailable. |
| Editor still offers it | Open the rule editor in that state. | The destination is offered; the review step and Activity are what say the host cannot pin, so the user is told rather than quietly ignored. |

## What this run cannot prove

The exact vtable layout and the "not pinned" HRESULT of the private pinned-apps
surface are proven by behaviour on a real build, never by the declaration. If the
probe passes but a pin does nothing visible in Task View, the declaration is
wrong for this build family: report the build number, and the correct posture is
to remove that build from the adapter's admitted list rather than to adjust the
declaration until something appears to work.
