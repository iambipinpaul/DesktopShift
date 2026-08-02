# Manual Shell and Machine Recovery Matrix

Restarting Explorer, putting a machine to sleep, and unplugging a monitor are
three things no automated test in this repository is allowed to do. A test that
restarted the shell would take the developer's session down with it, and a test
that suspended the machine would never report its own result. So the recovery
paths are driven in tests by injected faults, and the real disruptions are
verified by hand. Everything that can be automated already is:

| Coverage | Where |
| --- | --- |
| All three signals end to end, with call counts and ordering | `tests/DesktopShift.Core.Tests/Recovery/ShellRecoveryServiceTests.cs` |
| Stale registrations are dropped **before** anything is validated | same file |
| Hooks are re-registered **only** after capability validation succeeds | same file |
| Every fault-injection point failing produces a reported failure, not a crash | same file |
| Overlapping duplicate signals produce exactly one reconciliation | same file |
| Message → signal mapping, including suspend and the later duplicate resume notification that must not map | `tests/DesktopShift.Windows.Tests/Recovery/WindowsShellLifecycleMessageMappingTests.cs` |
| The Win32 message and power-event constants | `tests/DesktopShift.Windows.Tests/Recovery/WindowsShellLifecycleMessageContractTests.cs` |
| Hooks can be dropped and taken again without disposing the source | `tests/DesktopShift.Windows.Tests/Recovery/WindowsWinEventSourceRestartTests.cs` |
| The targeted validity check, including a dead bridge and a throwing probe | `tests/DesktopShift.Windows.Tests/Recovery/WindowsNativeRegistrationSetTests.cs` |
| Signals reach exactly one recovery pass, and stop reaching it after shutdown | `tests/DesktopShift.Core.Tests/Hosting/ShellRecoveryHostingTests.cs` |

This matrix covers what those cannot: a real shell, a real sleep, and a real
monitor.

## Before each run

1. At least one Managed Desktop exists and at least one Application Rule targets
   it, so "assignment still works afterwards" is something you can actually see.
2. At least two desktops exist that no definition names. They are the control
   group: no row below may change them.
3. The Activity page is open, **Record local activity** is enabled, and the page
   is filtered to nothing, so recovery events can be read as they arrive.
4. Note whether DesktopShift is in Full Mode or Limited Mode before you start.
   Several rows read differently in each, and the difference is the point.

Recovery is entirely event-driven. There is no timer, no settle delay, and no
polling anywhere in it, so "wait and see if it fixes itself" is never the
expected behaviour — if a row does not recover promptly after the disruption, it
is not going to.

## Matrix

### 1. Explorer restarts

Restart Explorer from Task Manager (**Details** → `explorer.exe` → Restart), or
end the process and let Windows bring it back.

| Step | Expected |
| --- | --- |
| Watch Activity as the taskbar returns | One recovery thread appears, every row tagged with signal `explorer_restarted` |
| Read the rows in order | Registrations invalidated, then capability validated, then registrations restored, then one reconciliation. That order, every time |
| Open a window a rule claims | It is assigned as it was before the restart. Window observation is working again |
| Watch the unmanaged desktops | Unchanged — none created, deleted, renamed, or reordered |
| Watch the notification-area icon | It comes back on its own |

The failure worth an issue is **automatic assignment silently not working after
the taskbar returns**: hooks registered against the previous shell are dead, and
a run where Activity shows no recovery thread at all means the restart was never
noticed.

### 2. Explorer restarts while DesktopShift is in Limited Mode

| Step | Expected |
| --- | --- |
| Force Limited Mode, then restart Explorer | Recovery still runs. It reports Limited rather than Healthy |
| Read the capability-validation row | It states plainly that window placement is available and that desktop creation, switching, and notifications are not |
| Open a window a rule claims | It is still placed. Limited Mode degrades recovery; it does not disable assignment |

### 3. Explorer restarts repeatedly

| Step | Expected |
| --- | --- |
| Restart Explorer three times in fairly quick succession | Three recovery threads, each complete. No pile-up, no growing queue, no duplicated reconciliation within one thread |
| Watch the desktop count throughout | Constant. Recovery re-registers; it does not recreate |

### 4. Sleep and resume

Sleep the machine (**Start → Power → Sleep**), wait long enough to be sure it
really suspended, and wake it.

| Step | Expected |
| --- | --- |
| Read Activity after unlocking | One recovery thread tagged `session_resumed`, and **one** reconciliation — not two, even though Windows broadcasts more than one resume notification |
| Read the validity row | It says the registrations were checked and found intact, in the normal case |
| Confirm what did not happen | No capability re-validation and no re-registration when the check found everything intact |
| Open a window a rule claims | It is assigned normally |

Row 4's real subject is the notification count. Windows broadcasts
`PBT_APMRESUMEAUTOMATIC` for every wake and may later broadcast
`PBT_APMRESUMESUSPEND` for the same wake after user interaction. The signal
source deliberately maps only the automatic notification, so one physical wake
produces one recovery signal without a timer or settle delay.

### 5. Desktops changed while the machine was asleep

This row needs two machines' worth of patience or a hybrid-sleep machine that
restores a different desktop layout.

| Step | Expected |
| --- | --- |
| Arrange for the Managed Desktop to be missing at wake | The resume reconciliation notices and restores the mapping, subject to the same recreation cooldown as any other topology recovery |
| Read the rows | The reconciliation is the same one topology recovery uses. It is not a second, separate mechanism |

### 6. Hibernate

| Step | Expected |
| --- | --- |
| Hibernate and resume | Identical to row 4. Hibernation is not a distinct path and must not behave like one |

### 7. Going to sleep

| Step | Expected |
| --- | --- |
| Sleep the machine and read Activity **after** waking, looking at the events from before the suspend | Nothing was recovered on the way down. Only coming back is a recovery trigger |

A recovery pass triggered by `PBT_APMSUSPEND` would be work done on a machine
that is about to stop executing, and would race the suspend itself.

### 8. Display changes

| Step | Expected |
| --- | --- |
| Attach or detach an external monitor | One recovery thread tagged `display_changed`, reporting **no action needed** |
| Confirm what did not happen | No reconciliation, no re-validation, no re-registration, no window moved, no desktop touched |
| Change resolution, scaling, or refresh rate | Same: noticed, checked, nothing done |
| Rotate a display, or change the primary display | Same |

Virtual desktops are not per-monitor. A display change that reconciled desktops
would be moving the user's windows for a reason that does not exist, so row 8 is
mostly a list of things that must **not** happen.

### 9. Docking and undocking

| Step | Expected |
| --- | --- |
| Undock a laptop and redock it | Several display notifications arrive; **one** recovery pass answers them. Activity says how many notifications it folded |
| Watch the windows Windows itself moves between monitors | DesktopShift does not fight it. It moves nothing of its own |

### 10. Recovery that cannot succeed

| Step | Expected |
| --- | --- |
| Arrange for capability validation to fail after an Explorer restart (an unsupported build, or the native bridge unavailable) | Recovery reports an understandable failure and the app keeps running |
| Read the rows | The failure names the stage that failed. **No re-registration was attempted** after the failed validation |
| Confirm the app | Still running, still openable, still able to export diagnostics. Explorer is untouched — DesktopShift never restarts it and never retries it |

### 11. Nothing on a timer

| Step | Expected |
| --- | --- |
| Leave the machine completely idle for an hour with DesktopShift running | Activity gains no recovery events at all |
| Check DesktopShift's CPU time over that hour | Effectively none |

Row 11 is the one that fails quietly if someone later adds a "just in case"
periodic check. Recovery is event-driven, and an idle machine produces no
events.

## Recording a run

For each row, note the signal tag, the ordered result codes, the number of
notifications folded, and whether a reconciliation ran. Export the diagnostic
bundle once and inspect the JSON-lines log too: every recovery row must have
`source: "recovery"` and a `recoverySignal` of `explorer_restarted`,
`session_resumed`, or `display_changed` matching the disruption.

Three results are worth an issue immediately: **assignment not working after a
disruption with no recovery thread in Activity**, **more than one reconciliation
for a single physical event**, and **a desktop or window changing that no row
above predicts**.
