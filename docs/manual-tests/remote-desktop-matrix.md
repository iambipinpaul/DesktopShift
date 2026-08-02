# Manual Remote Desktop Matrix

A Remote Desktop session cannot be created inside a test, so the behavior that
depends on a live connection is verified by hand. Everything that can be
automated already is:

| Coverage | Where |
| --- | --- |
| Rule matching from the real defaults | `tests/DesktopShift.Windows.Tests/Observation/RemoteDesktopWindowMatchingTests.cs` |
| Ownership against real Win32 windows | `tests/DesktopShift.Windows.Tests/Assignments/RemoteDesktopControlledWindowTests.cs` |
| Refused and denied moves | `tests/DesktopShift.Windows.Tests/Assignments/WindowsWindowDesktopPlacementServiceTests.cs` |
| Full observation-to-assignment pipeline | `tests/DesktopShift.Core.Tests/Assignments/RemoteDesktopAssignmentAcceptanceTests.cs` |

This matrix covers what those cannot: a real `mstsc.exe` talking to a real host.

## What the default rule claims

The `remote-desktop` rule targets the **Remote** Managed Desktop and is
identified by the process name `mstsc.exe` alone.

- **No window class is declared.** Every surface `mstsc.exe` puts on screen —
  the connection dialog before a session exists, the `TscShellContainerClass`
  session frame, the connection bar — belongs on Remote. Naming one class would
  leave the others unmanaged.
- **`msrdc.exe` is not claimed.** The same file name is both the Azure Virtual
  Desktop client and the WSL client that hosts every WSLg Linux window. Claiming
  it would drag Linux application windows onto Remote.
- **No packaged client is claimed.** The Store "Remote Desktop" app and "Windows
  App" ship packaged identities that have not been confirmed against an
  installed package, and an unverified identity is dead configuration.

A user who wants only *connected* sessions on Remote can narrow the rule in
`configuration.json`:

```json
{
  "id": "remote-desktop",
  "processNames": [ "mstsc.exe" ],
  "windowClasses": [ "TscShellContainerClass" ]
}
```

`windowClasses` is a refinement: the rule then matches only windows carrying
that class, and the connection dialog stays wherever it opens.

## Before each run

1. Managed Desktops are reconciled and **Remote** is bound (Desktops page shows
   a runtime desktop for it).
2. The Activity page is open and **Record local activity** is enabled, so each
   row can be read as it arrives.
3. Start from a desktop that is **not** Remote, so a move is observable.
4. The `remote-desktop` rule is enabled with the default switch policy,
   `OnForegroundActivation`.

Every row below records one assignment Activity entry per window event unless
stated otherwise. "Correlation ID" differs per row; two windows never share one.

## Matrix

### 1. Windowed single session

| Step | Expected |
| --- | --- |
| Launch `mstsc.exe`; the connection dialog appears | Rule `remote-desktop`, matched on **process name**, moved to Remote |
| Connect; the session frame opens windowed | Rule `remote-desktop`, moved to Remote, outcome **Moved** |
| Click the session window from another desktop | Outcome **Moved + switched** or **Already correct + switched**; Desktop navigation reads "Switched to remote" |

Activity: `Window movement` says `Moved to remote` on first placement and
`Already on remote` on repeats. `Matched on` reads `Process name`.

### 2. Multiple simultaneous sessions

| Step | Expected |
| --- | --- |
| Open two sessions to different hosts | Two Activity rows, two distinct correlation IDs, two distinct window handles |
| Open a third | A third independent row |
| Close one | The others are unaffected; no row references the closed window's handle |

Activity: each row names its own window handle. One session never stands in for
another, and no row shows a window moving twice for a single event.

### 3. Credential prompt

| Step | Expected |
| --- | --- |
| Connect to a host that asks for credentials | The prompt is **owned** by the session frame. Activity records the **frame's** handle, `mstsc.exe`, and window class `TscShellContainerClass` — not the prompt's |
| The prompt is hosted by `CredentialUIBroker.exe` | Same as above. Ownership decides, so the broker's own identity never reaches matching |
| A credential window appears with **no** owner | Activity identifies it as a Windows-managed window and leaves it where Windows opened it. It is never guessed onto Remote |

Without an owner there is nothing tying the prompt to a session, so it is never
guessed onto Remote. The read-only Windows-managed list applies instead, so
DesktopShift does not attempt a move Windows is expected to reject.

### 4. Reconnect dialog

| Step | Expected |
| --- | --- |
| Interrupt the network so the session starts reconnecting | The reconnection dialog is owned by the frame; Activity records the **frame's** handle |
| Reconnect succeeds | No extra move — the frame is already on Remote, `Window movement` reads `Already on remote` |
| The dialog closes itself mid-move (rare race) | If a failure is recorded at all it reads `window_placement.stale_window_handle` — "The window closed before it could be moved" — not a refused move |

### 5. Full-screen entry

| Step | Expected |
| --- | --- |
| Put a session full screen **while it is already on Remote** | `Already on remote`; nothing is attempted |
| Put a session full screen **while it is on another desktop**, then raise an event for it | Either a normal move, or outcome **Move failed** with code `window_placement.move_failed` |

When Windows refuses, the Activity diagnostic reads:

> Windows refused to move the window to the requested virtual desktop. A window
> can be refused while it is in a state Windows manages itself, such as a
> full-screen remote session. (`window_placement.move_failed`) • HRESULT 0x…

Confirm the failure is **reported once per event**: the row count must match the
number of events, with no burst of repeated failures for one event. DesktopShift
never retries a refused move — Windows owns that decision and repeating the same
call cannot change it.

### 6. Full-screen exit

| Step | Expected |
| --- | --- |
| Leave full screen on a session whose move was refused | The next event moves it normally, outcome **Moved** |

Nothing is remembered between attempts, so no manual reset is needed. If the
session was already on Remote, the row reads `Already on remote`.

### 7. Multi-monitor full screen

| Step | Expected |
| --- | --- |
| Connect with **Use all my monitors** and enter full screen | Same as row 5: either a normal move or a single structured `Move failed` |
| Exit full screen | Same as row 6 |
| Use the connection bar while full screen across monitors | No separate Activity row for the bar, or a row naming the **frame's** handle. The bar must never move on its own identity |

### 8. Access denial

| Step | Expected |
| --- | --- |
| Run `mstsc.exe` elevated while DesktopShift is not | Observation skip reason `Identity access denied`, with the Windows error code. No window is moved |
| A move is denied by Windows | Outcome **Move failed** with code `window_placement.move_access_denied` — "Windows denied access to the window" |

An access denial is reported as its own failure, distinct from a refused move,
so Activity says which one happened.

### 9. Focus behavior

| Step | Expected |
| --- | --- |
| Activate a windowed session from another desktop | Exactly **one** desktop switch. The follow-on foreground event DesktopShift's own switch produces is recorded as **Suppressed** with skip reason `SelfGeneratedForegroundSuppressed`, carrying the original row's correlation ID as its related correlation |
| Activate it again a second later | A second genuine switch. Suppression is one-shot and never hides a real activation |
| Set the rule's switch policy to `Never` and activate | The window still moves; `Desktop navigation` reads "Move only — the rule's switch policy is Never". No switch occurs |
| Set the policy to `OnNewWindowActivation` | Only the first activation after the window is created or shown switches; later ones read "this was not the first eligible activation of a new window" |

The failure this row guards against is a focus loop: two or more switches for a
single user action, or the desktop flipping back and forth. Either is a defect.

## Recording a run

For each row, note the outcome, the skip or failure reason, and the HRESULT when
one appears. A row that neither matches the expectation nor has a documented
reason is a defect worth an issue.
