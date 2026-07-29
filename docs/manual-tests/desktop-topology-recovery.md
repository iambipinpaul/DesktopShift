# Manual Desktop Topology Recovery

Deleting, adding, renaming, and dragging a Windows Virtual Desktop cannot be done
from a test. No automated test in this repository creates, deletes, reorders, or
switches a real desktop, and none ever should — a test that rearranged the
machine it runs on would be indistinguishable from the bug it was written to
catch. Everything that can be automated already is:

| Coverage | Where |
| --- | --- |
| Notification → one reconciliation → at most one window pass | `tests/DesktopShift.Core.Tests/ManagedDesktops/ManagedDesktopTopologyRecoveryTests.cs` |
| Recreation policy, capabilities, Limited Mode, ambiguity | same file |
| Bounded cooldown, and that it is driven by events and never by time | `tests/DesktopShift.Core.Tests/ManagedDesktops/ManagedDesktopRecreationCooldownTests.cs` |
| Reorder updates runtime mapping and changes no rule target | `ManagedDesktopTopologyRecoveryTests.ReorderingDesktops_UpdatesTheRuntimeMappingAndChangesNoRule` |
| Unrelated desktops are never touched | `ManagedDesktopTopologyRecoveryTests.RecoveringOneDesktop_TouchesNoUnrelatedDesktop` |
| Native notification ABI: exports, struct layout, callback marshalling | `tests/DesktopShift.Windows.Tests/VirtualDesktops/DesktopTopologyNotificationContractTests.cs` |

This matrix covers what those cannot: a real user rearranging real desktops.

## Before each run

1. At least four desktops exist, and **only one** of them is a Managed Desktop.
   The others are the control group — the whole point of most rows below is that
   they are never touched.
2. Note each desktop's name and position before starting. Several rows are only
   meaningful against a recorded starting state.
3. The Activity page is open, so each decision can be read as it arrives.
4. Diagnostics export is available, because the suppression rows are easiest to
   confirm in the exported activity.

The shipped cooldown bounds are **3 recreations**, counted within a window of
**16 topology events**, after which recreation is suppressed for **32 topology
events**. The cooldown counts *events*, not seconds: waiting without touching
the desktops will never lift it.

## Matrix

### 1. Deleting a Managed Desktop

| Step | Expected |
| --- | --- |
| Delete the Managed Desktop from Task View | It is recreated once. Activity records one reconciliation naming the semantic key |
| Watch the other desktops | Unchanged — same names, same relative order, none created or removed |
| Watch the windows on unrelated desktops | None move. A window moves only when a rule claims it |

The recreated desktop is a **new** runtime desktop with a new identifier. The
semantic key is what rules target, so no rule needs editing.

### 2. Repeated deletion reaches the cooldown

| Step | Expected |
| --- | --- |
| Delete the Managed Desktop three times in a row | Each of the first three is recreated |
| Delete it a fourth time | It is **not** recreated. Activity records a suppression with code `managed_desktops.recreation_suppressed`, naming the key and its remaining budget |
| Keep deleting | Still suppressed. No recreation loop, no flicker, no runaway desktop count |

This row is the one that matters most. A user deleting a desktop repeatedly is
telling DesktopShift something, and the correct response is to stop, not to win.

### 3. The cooldown is bounded, not permanent

| Step | Expected |
| --- | --- |
| After reaching suppression, create/delete/switch unrelated desktops to generate topology events | After the configured number of events, suppression lifts |
| Delete the Managed Desktop again | It is recreated normally |
| Instead of generating events, simply wait several minutes | Suppression does **not** lift. It is event-driven by design |

### 4. Explicit recreation is never held back

| Step | Expected |
| --- | --- |
| While suppressed, use the Desktops page to recreate the Managed Desktop by hand | It is recreated immediately |

The cooldown exists to stop DesktopShift from fighting the user. It must never
stop the user from asking directly.

### 5. Reordering

| Step | Expected |
| --- | --- |
| Drag the Managed Desktop to a new position in Task View | The Desktops page shows its new position. No window moves, and no desktop is created or deleted |
| Inspect the rules targeting it | Unchanged. Rules target the semantic key, which a reorder does not affect |
| Drag an unrelated desktop past it | Same: position updates, nothing else changes |

A reorder is explicitly **not** a reason to touch windows — the windows are
already where they belong and only the index beneath them moved.

### 6. Renaming

| Step | Expected |
| --- | --- |
| Rename the Managed Desktop's runtime desktop in Task View | The binding survives; the desktop is not recreated and no duplicate appears |
| Rename an unrelated desktop to exactly the Managed Desktop's expected name | **Two desktops now match one definition.** Nothing is recreated and nothing is rebound — Activity records the ambiguity instead of guessing |

Row 6's second step is the ambiguity case. Picking either desktop would be a
coin flip, so DesktopShift reports and stops.

### 7. Limited Mode

| Step | Expected |
| --- | --- |
| Force Limited Mode (compatibility test fails, or the native bridge is unavailable) and delete the Managed Desktop | It is **not** recreated. Activity explains that recreation needs a validated capability that is not present |
| Confirm the rest of the app | Still runs. Limited Mode degrades recovery; it does not crash or disable the app |

### 8. Adding desktops

| Step | Expected |
| --- | --- |
| Add a new desktop that no definition names | It is left entirely alone — no binding claims it, no window is moved to it |
| Add a desktop whose name matches an unbound definition | It may be bound to that definition. No desktop is created, because the one that was needed now exists |

## Recording a run

For each row, note the reconciliation outcome, whether a window pass ran, and the
suppression code when one appears. The two failures worth an issue immediately
are **a desktop or window changing that no row above predicts**, and **a
recreation loop that does not converge**.
