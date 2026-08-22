# BSP virtual desktop flicker research

## Decision

Use a reason-aware desktop-switch transaction in the tiling coordinator.

DesktopShift already receives two different native topology signals:

- `CurrentChanged` when the current desktop changes.
- `Switched` when the shell reports that the switch finished.

The coordinator currently treats both signals as a generic topology change and
can apply a layout between them. It must instead freeze BSP placement from
`CurrentChanged` through `Switched`, preserve the existing BSP tree, and apply
one validated layout after the switch completes.

For Limited Mode, where the private completion signal is not available, use a
bounded quiet-period fallback. End the transition only after cloak state and
documented current-desktop membership agree in two consecutive snapshots.

This is better than a global delay. It changes timing only during a desktop
switch and keeps normal window opening, restore, minimize, and close handling
fast.

## Confirmed behavior

The live repro used ChatGPT and DesktopShift in two half-screen tiles.

- The issue reproduced in 10 of 10 desktop-switch cycles.
- ChatGPT stayed at `965,5,950,1022`.
- DesktopShift changed from `5,5,950,1022` to `5,5,1910,1022`.
- The full-width state started about 118–133 ms after returning.
- It lasted about 350–400 ms.
- DesktopShift then returned to the correct half-screen tile.
- The final BSP order stayed correct.
- Both windows were visible, normal, on the same desktop, and reported as being
  on the current desktop before the unwanted resize.
- WinEvent capture showed shell cloak, shell uncloak, and then two
  DesktopShift location changes. It did not show hide, destroy, minimize,
  maximize, or other state-change events.

The earlier fix preserved tree tokens during a shell cloak. That fixed the
permanent left/right reversal, but it did not stop an intermediate layout from
being committed during the wider switch transaction.

## Microsoft API findings

### Cloak is a per-window state, not a completed-switch barrier

`DWMWA_CLOAKED` reports why a window is cloaked. The value `0x2` means the
Windows shell supplied the cloak. The API does not say that one uncloak event
means every window on the desktop is ready. [Microsoft DWM window attribute
documentation](https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)

DesktopShift's captured WinEvents confirmed this. ChatGPT and DesktopShift
received separate cloak and uncloak events.

### WinEvents are asynchronous

DesktopShift uses out-of-context WinEvent hooks. Microsoft says these events
are queued and asynchronous. They remain in sequential delivery order, but a
callback can be re-entered. The correct pattern is to enqueue quickly and use a
serialized worker to read fresh state. [Microsoft `SetWinEventHook`
documentation](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwineventhook)

This means per-window uncloak events are good triggers, but they are not a safe
point for an immediate destructive BSP rebuild.

### The public virtual desktop API has no completion event

The supported `IVirtualDesktopManager` API can:

- Test whether a window is on the current desktop.
- Get the desktop ID for a window.
- Move a window to a desktop.

It does not expose desktop enumeration or an atomic switch-completed event.
[Microsoft `IVirtualDesktopManager`
documentation](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager)

`IsWindowOnCurrentVirtualDesktop` is still useful for the Limited Mode fallback
because it provides a supported current-membership check for each top-level
window. [Microsoft membership-query
documentation](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ivirtualdesktopmanager-iswindowoncurrentvirtualdesktop)

Microsoft also describes virtual desktops as an end-user window-management
feature and recommends using the public API only for its limited supported
scenarios. [Microsoft Old New Thing
article](https://devblogs.microsoft.com/oldnewthing/20201123-00/?p=104476)

DesktopShift already contains a build-validated native bridge for its Full
Mode. The switch transaction can use its more precise `CurrentChanged` and
`Switched` reasons, while keeping a public-API fallback.

### Keep the final placement atomic

Microsoft documents the deferred-position APIs as the way to change multiple
window positions together. `EndDeferWindowPos` applies the collected changes.
[Microsoft `DeferWindowPos`
documentation](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-deferwindowpos)

DesktopShift already uses one deferred-position batch with no activation and no
Z-order change. Keep this design for the one final post-switch placement.

### DWM flush is not a solution

`DwmFlush` waits for drawing changes queued by the calling application. It does
not flush the full desktop session. It cannot be used as a shell-switch
completion barrier. [Microsoft `DwmFlush`
documentation](https://learn.microsoft.com/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)

### Snap or arranged state is not the cause in this repro

Windows has a separate arranged state for snapped windows.
[Microsoft `IsWindowArranged`
documentation](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-iswindowarranged)

Live testing ruled it out here:

- `IsWindowArranged` stayed false.
- `WINDOWPLACEMENT.showCmd` stayed `SW_SHOWNORMAL`.
- `rcNormalPosition` changed at the same time as the unwanted resize instead of
  containing an older full-width restore rectangle.

No arranged-state normalization is needed for this bug.

## Options considered

### 1. Reason-aware switch transaction — recommended

On `CurrentChanged`:

1. Start a new switch generation.
2. Preserve every tracked BSP token, tree node, and last planned rectangle.
3. Continue reading events and state, but suppress placement batches.
4. Do not compact a workspace because a window is temporarily cloaked,
   unreadable, or absent from an incomplete switch snapshot.

On `Switched`:

1. Read one complete snapshot for the new current desktop.
2. Confirm that the snapshot is internally consistent.
3. Apply one deferred-position batch.
4. End the switch generation.

Benefits:

- Uses the precise signals already available in Full Mode.
- Prevents the bad intermediate full-width layout instead of repairing it
  after the user sees it.
- Does not slow normal tiling events.
- Preserves BSP order.

Risk:

- The `CurrentChanged` and `Switched` signals come from DesktopShift's validated
  native bridge, not the public virtual desktop API. The existing compatibility
  validation and Limited Mode fallback must remain.

### 2. Stable-snapshot fallback — recommended for Limited Mode

Start a transition when a tracked window receives shell cloak or when a
desktop-switch signal is observed. After all shell cloaks clear:

1. Wait for a short quiet period, such as 50–100 ms.
2. Read cloak, desktop ID, current-desktop membership, monitor, and rectangle
   for every existing token owner.
3. Read the same state once more after another short bounded interval.
4. Commit only if both snapshots agree.
5. Stop after a small retry limit. Keep the old layout if state never settles.

This is bounded event-driven validation, not continuous polling.

### 3. Global 400–500 ms debounce — not recommended

This can hide the current repro, but it delays every window open, restore,
minimize, close, and topology update. It also depends on one machine's animation
timing and can fail on a slower system.

### 4. Repair on `EVENT_OBJECT_LOCATIONCHANGE` — useful only as a safety net

DesktopShift could observe location-change events and restore a cached target
when a managed window moves unexpectedly. This would shorten the flash, but it
still repairs the window after the incorrect size becomes visible. It also
needs careful suppression to avoid feedback loops.

Use it only as an optional invariant check after the switch transaction is in
place.

### 5. Intercept `WM_WINDOWPOSCHANGING` — not recommended as the main fix

An application can change or reject its own incoming window position in this
message. [Microsoft `WM_WINDOWPOSCHANGING`
documentation](https://learn.microsoft.com/windows/win32/winmsg/wm-windowposchanging)

This could protect DesktopShift's own HWND, but it cannot safely protect other
applications. It would hide the architecture bug only for one window.

### 6. Disable redraw or DWM transitions — not recommended

`SWP_NOREDRAW` makes the caller responsible for later repainting all affected
areas. DesktopShift does not own the foreign windows it tiles. Disabling DWM
transitions also changes presentation behavior instead of fixing the wrong
intermediate layout. [Microsoft `SetWindowPos`
documentation](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos)

## Proposed implementation shape

Add a small coordinator-owned state machine:

```text
Stable
  -> CurrentChanged or first shell cloak
Switching(generation, preserved tokens and targets)
  -> Switched or stable-snapshot fallback
Settling(generation)
  -> one validated deferred-position batch
Stable
```

Important rules:

- A newer generation cancels an older pending settle operation.
- Destroy and minimize events remain authoritative, but their space-closing
  placement waits until the switch ends.
- Application cloak remains normal hidden-window behavior outside a switch.
- Shell cloak never removes a token.
- An incomplete or unreadable snapshot never expands surviving windows during
  a switch.
- Provider reconnect or fallback ends the native transaction safely and uses
  the bounded stable-snapshot path.
- Store topology reasons as a typed value rather than comparing free-form
  strings inside the coordinator. Keep the raw reason only for diagnostics.

Before changing behavior, add temporary diagnostic fields at two boundaries:

- Topology reason and generation received by `TilingCoordinator`.
- Every placement batch with handle and target rectangle.

The expected proof is that the current bad full-width request occurs after
`CurrentChanged` and before `Switched`. Remove the temporary fields after the
regression test captures the sequence.

## Test plan

### Regression tests

Add an integration-level test that includes the topology reasons and the
coordinator, not only direct cloak calls:

1. Start with two stable half-screen tokens.
2. Raise `CurrentChanged`.
3. Deliver separate shell-cloak events.
4. Deliver separate uncloak events.
5. Present an incomplete intermediate snapshot that would normally produce one
   full-width tile.
6. Assert that no placement batch is committed.
7. Raise `Switched` and provide a complete stable snapshot.
8. Assert that one final batch preserves both original sides.

Also test:

- A second switch starts before the first settles.
- The `Switched` signal never arrives and the bounded fallback succeeds.
- The fallback reaches its retry limit and preserves the old layout.
- A window is destroyed or minimized during a switch.
- Provider fallback or reconnect occurs during a switch.
- Full Mode and Limited Mode produce the same final layout.

### Live acceptance test

Run at least 20 real desktop-switch cycles with 1 ms rectangle sampling.

Pass conditions:

- Each target window has one unique visible rectangle during return.
- No half-screen window expands to full width.
- No side changes.
- Exactly zero placement batches are committed between switch start and switch
  completion.
- The final post-switch batch is either empty or contains the complete layout.

## Final recommendation

Implement the reason-aware switch transaction first. Add the bounded
stable-snapshot fallback at the same time so Limited Mode remains correct. Keep
the existing atomic `EndDeferWindowPos` executor. Do not use a global delay,
redraw suppression, DWM flush, or an own-window-only message interception as
the primary fix.
