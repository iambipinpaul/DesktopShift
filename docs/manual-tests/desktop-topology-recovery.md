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
| Task View reorder updates preferred app order and changes no rule target | `ManagedDesktopTopologyRecoveryTests.ReorderingDesktops_UpdatesTheRuntimeMappingAndChangesNoRule` |
| App reorder updates Task View order | `ManagedDesktopMaintenanceTests.Move_RenumbersPreferredOrderAndReordersWindowsDesktops` |
| Unrelated desktops are never touched | `ManagedDesktopTopologyRecoveryTests.RecoveringOneDesktop_TouchesNoUnrelatedDesktop` |
| Native notification ABI: exports, struct layout, callback marshalling | `tests/DesktopShift.Windows.Tests/VirtualDesktops/DesktopTopologyNotificationContractTests.cs` |
| Naming: convergence, concede bound, readback guard, capability and setting gates | `tests/DesktopShift.Core.Tests/ManagedDesktops/ManagedDesktopNamingTests.cs` |
| Naming, reordering, and the harmless probe are exported; removal is not | `DesktopTopologyNotificationContractTests` and `NativeBridgeOptInContractTests` |

This matrix covers what those cannot: a real user rearranging real desktops.

Naming and reordering are the capabilities here that no automated test can fully
prove. The managed tests drive a fake, and the native tests inspect exported
symbols without calling them — neither can establish that the private shell
slots match the machine in front of you. Section 6a exists for that gap.

## Before each run

1. At least four desktops exist, and **only one** of them is a Managed Desktop.
   The others are the control group — the whole point of most rows below is that
   they are never touched.
2. Note each desktop's name and position before starting. Several rows are only
   meaningful against a recorded starting state.
3. The Activity page is open and **Record local activity** is enabled, so each
   decision can be read as it arrives.
4. Diagnostics export is available, because the suppression rows are easiest to
   confirm in the exported activity.

The shipped cooldown bounds are **3 recreations**, counted within a window of
**16 topology events**, after which recreation is suppressed for **32 topology
events**. The cooldown counts *events*, not seconds: waiting without touching
the desktops will never lift it.

Naming has its own separate bound: **3 corrections** per Managed Desktop, after
which DesktopShift concedes and leaves the user's name alone. Unlike the
recreation cooldown it does not decay, and it resets only when the application
restarts.

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
| Move a Managed Desktop earlier or later on the Desktops page | Task View shows the same relative order. The page reports a partial result instead if the validated reorder capability is unavailable |
| Inspect the rules targeting it | Unchanged. Rules target the semantic key, which a reorder does not affect |
| Drag an unrelated desktop past it | Same: position updates, nothing else changes |

A reorder is explicitly **not** a reason to touch windows — the windows are
already where they belong and only the index beneath them moved.

### 6. Renaming

These rows assume **Settings → Desktops → Name Windows desktops** is on, which is
the shipped default. With it off, DesktopShift never writes a name and the first
two rows reduce to "the binding survives and nothing else happens".

| Step | Expected |
| --- | --- |
| Rename the Managed Desktop's runtime desktop in Task View | The binding survives — the desktop is not recreated and no duplicate appears. DesktopShift then puts its own name back, and Activity records `naming.applied` |
| Rename it back three more times | It is corrected the first three times, then DesktopShift concedes and **your** name stays. Activity records `naming.conceded` |
| Rename an unrelated desktop to exactly the Managed Desktop's expected name | **Two desktops now match one definition.** Nothing is recreated and nothing is rebound — Activity records the ambiguity instead of guessing |

The third step is the ambiguity case. Picking either desktop would be a coin
flip, so DesktopShift reports and stops. Note that naming can now *produce* this
state itself: it applies the configured name without first checking whether
another desktop already carries it, so a manually named spare and a managed
desktop can end up sharing a name. The binding is by identity and is unaffected;
the ambiguity only bites if the binding metadata is later lost.

### 6a. Naming (needs a machine that supports it)

Naming and reordering reach shell manager slots past the read-only prefix, so
they are gated twice: the build family must be marked `ExtendedLayoutValidated`
in the native adapter profile table, **and** a read-only lookup probe must pass
at activation. Confirm which applies before reading a failure as a bug.

| Step | Expected |
| --- | --- |
| Open Settings → Windows compatibility | The capability list includes **Name desktops** and **Reorder desktops** on a supported build, and omits both otherwise. Everything else in the list is identical either way |
| On a build where it is omitted, check the Desktops page and Activity | Bindings, creation, switching and window moves all still work. Activity records `naming.unavailable` once per pass, not once per desktop |
| Let DesktopShift create a missing Managed Desktop | The new desktop appears in Task View already carrying its configured name rather than "Desktop 5" |
| Give a Managed Desktop a very long display name, or one with unusual characters, and reconcile | Either Task View shows it exactly, or Activity records `naming.not_stored` and DesktopShift stops trying for that destination. It must **never** rewrite the name repeatedly |
| Watch Task View for a few minutes after any naming | No flicker, no repeated renaming. One write per change, then silence |

The fourth row is the one worth being slow about. Windows does not document a
length or character limit for this call, so the readback guard is the only thing
standing between an unstorable name and a rename on every topology notification
for as long as the app runs.

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
suppression code when one appears. Record the Windows build, and whether **Name
desktops** and **Reorder desktops** appeared in the capability list, since every
row in 6a depends on it.

The failures worth an issue immediately are **a desktop or window changing that
no row above predicts**, **a recreation loop that does not converge**, and **a
naming loop that does not converge**. Treat any desktop being deleted by
DesktopShift as urgent: the application has no supported deletion path and the
native bridge exports no entry point for it.
