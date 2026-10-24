# Show on all desktops (per-app pin)

## Problem Statement

In Windows 11 Task View, a window or app can be set to show on all virtual
desktops ("Show this window on all desktops" / "Show windows from this app on
all desktops"). DesktopShift has no equivalent: an Application Rule can move an
app to one Managed Desktop, or leave it anywhere it opened (`Anywhere`), but it
cannot keep an app visible everywhere. Users want this for the apps they always
want at hand (music, chat, scratch notes, task manager) without moving those
windows themselves after every desktop switch or sign-in.

## Solution

A new per-app Application Rule destination, "Show on all desktops", that pins
every window the rule claims so it appears on all Windows virtual desktops,
like Task View app pinning. The user names the application once with the same
identities rules already use (process name, package family, AppUserModelId,
executable path, window class refinement). DesktopShift pins matching windows
instead of moving them, re-pins them silently after restart, and never moves
them between desktops while the rule is in force.

## User Stories

1. As a user, I want to mark an app as "Show on all desktops", so that its windows follow me to every desktop.
2. As a user, I want to pick the app with the same identities I already use in rules, so that I do not learn a second matching model.
3. As a user, I want a pinned app's new windows to pin automatically, so that I do not pin each window by hand.
4. As a user, I want pinned windows to stay where they are across desktop switches, so that switching desktops never strands them.
5. As a user, I want pinned windows to survive sign-out/sign-in without action, so that my always-visible apps are still everywhere after restart.
6. As a user, I want pressing Reassign All to re-pin anything that lost its pin, so that there is one obvious repair.
7. As a user, I want disabling or deleting a pin rule to stop pinning, so that the app returns to normal placement on its next observed event.
8. As a user, I want a disabled pin rule to behave as if the app were unnamed until I re-enable it, so that disable means disable.
9. As a user, I want pinned windows to never trigger a desktop switch, so that a background pinned window does not pull my screen away.
10. As a user, I want pinned windows to still tile within the current desktop, so that pinning does not break layout.
11. As a user, I want pinned windows to be exempt from the unmanaged sweep, so that a pinned app is never swept to the first desktop.
12. As a user, I want Activity to show which rule pinned a window, so that pinning stays attributable like any other assignment.
13. As a user, I want the rule editor to offer pinning as a third destination next to Managed Desktop and Anywhere, so that there is one picker and the existing tie-break still applies.
14. As a user, I want a pin rule to carry no target desktop and no switch policy, so that unusable settings are hidden like they are for Anywhere.
15. As a user, I want pinning to be unavailable-but-honest in Limited Mode, so that a host that cannot pin says so instead of silently doing nothing.

## Implementation Decisions

Agreed scope from discussion (2026-09-26): per-app only, no per-window manual
pin; unpin lazily on next observed event; never move pinned windows across
desktops; re-pin silently at startup.

- New `ApplicationRuleAction` value (e.g. `ShowOnAllDesktops`) as a trailing
  member, so documents written before it existed keep reading as
  `MoveToDesktop` with no schema-version bump — the same pattern `AllowAnywhere`
  used. `TargetDesktopKey`, `Triggers`, and `SwitchPolicy` are stored but not
  read while the action is pin, and the editor hides the placement controls.
- New `WindowRuleDestination` value (e.g. `PinnedToAllDesktops`) projected by
  the existing configuration-to-observation projection. `IsNamedByAnyRule`
  counts a pin rule as naming its app, so narrowing triggers never banishes
  pinned windows to the sweep.
- Assignment pins instead of moving: when the observation matches a pin rule,
  the pipeline calls pin rather than resolving a target desktop. No desktop
  resolution, no switch-policy evaluation, no move. A pinned window is never
  passed to the mover while its pin rule is in force.
- Unpin is lazy, not immediate. Disabling or deleting a pin rule does not
  enumerate windows. The next observed event for the window (created, shown,
  startup reconciliation, manual reassignment) unpins it first, then follows
  the rule that now matches (move, Anywhere, or sweep). Open windows stay
  pinned until that next event; restart and Reassign All converge everything.
- Answered events mirror the sweep-plus-foreground set needed for repair:
  `WindowCreated`, `WindowShown`, `StartupReconciliation`,
  `ManualReassignment`, plus `ForegroundActivated` only to re-assert a lost pin
  without moving. Foreground handling must never move a pinned window out from
  under a click.
- New placement seam at the highest point possible: extend the existing window
  placement abstraction with pin/unpin operations (preferred) rather than a
  second parallel service, so assignment, tests, and Limited Mode share one
  capability check. One seam for the decision, one for the transport.
- Native transport is new private Shell ABI surface (the `IVirtualDesktopPinnedApps`
  family: view-pin query, pin, unpin), declared build-scoped like the existing
  manager ABI, gated per Windows build family, and validated by behaviour
  before use — the same posture as the extended-layout probe for naming and
  reordering. Failure to prove the layout costs pinning only; enumeration,
  creation, switching, and moves stay in Full Mode. Limited Mode reports pin
  as unavailable.
- Tiling still applies within the current desktop; pinning only exempts the
  window from cross-desktop moves. Tiling reconciliation must not move a pinned
  window to another desktop.
- Editor: third entry in the existing destination picker ("Show on all
  desktops — pin this app everywhere"). Selecting it preserves the stored
  target/trigger/switch values underneath, so toggling back restores a working
  rule — the same pattern Anywhere uses today.

## Testing Decisions

- Test external behaviour, not implementation details: given a rule and a
  window event, assert pin-called vs move-called vs left-alone, and assert the
  recorded activity attributes the decision to the pin rule.
- Modules under test:
  - Rule projection (configured rule to observation rule destination mapping),
    following the existing matcher/projection tests.
  - Observation dispatch (pin rule wins over sweep, exempt from sweep,
    foreground does not move), following the observation processor tests.
  - Assignment service (pin path records activity, never calls move, never
    requests a switch; unpin-then-move when the pin rule is gone), following
    the existing assignment-service tests.
  - Native bridge gating (unsupported build / failed probe reports pin
    unavailable; Full Mode operations unaffected), following the provider
    validation tests.
- Prior art: `WindowAssignmentServiceTests`,
  `WindowAssignmentHostingAcceptanceTests`, `WindowRuleMatcherTests`, and the
  unmanaged-sweep manual test. Add a pin/unpin manual matrix (pin, switch
  desktop, restart, disable rule, Reassign All) alongside the sweep notes.
- Manual verification boundary: pin an app, switch across at least three
  desktops, restart, disable the rule and confirm lazy unpin on next event,
  confirm no desktop switch is ever triggered by a pinned background window,
  confirm Limited Mode reports pin unavailable.

## Acceptance Criteria

- A rule with the pin destination pins every matching window: the assignment
  path records a pin outcome and never resolves a target desktop, never calls
  move, and never requests a switch.
- A window claimed by an enabled pin rule is never swept to the first desktop,
  even when the rule's triggers are narrowed.
- A pinned app's new windows pin automatically on their answering events
  (window created, window shown, startup reconciliation, manual reassignment);
  foreground activation re-asserts a lost pin without moving the window.
- Disabling or deleting a pin rule does not enumerate windows; the next
  observed event unpins the window first, then applies the rule that now
  matches (move, anywhere, or sweep).
- Restart or sign-in re-pins silently during startup reconciliation, and
  Reassign All re-pins anything that lost its pin.
- A pinned window never triggers a desktop switch, and tiling reconciliation
  never moves a pinned window to another desktop.
- A document written before the pin action existed still loads as a move rule
  with no schema-version bump; a pin rule round-trips through configuration
  and appears as the third destination in the editor.
- On a build where the pin ABI cannot be proven, pin is reported unavailable
  and Full Mode enumeration, creation, switching, and moves continue to work;
  Limited Mode reports pin as unavailable.

## Out of Scope

- Per-window manual pin ("pin just this window") outside a rule.
- Immediate background unpin sweep on rule disable/delete (no enumeration pass).
- Pinning a window class that narrows to one surface is supported by the
  existing refinement model, but no new per-surface UX is added.
- Removing or reordering Windows desktops; pinning does not change desktop
  topology.
- Any change to the unmanaged sweep destination (still position 0) or to the
  Anywhere behaviour (still never move).

## Further Notes

- Open Windows questions to confirm during implementation spike: exact
  `IVirtualDesktopPinnedApps` slot layout per supported build family, whether
  view-pin (`PinView`) alone suffices or app-id pin (`PinApp`) is also needed
  for processes without an AppUserModelId, and the HRESULT for "not pinned".
  Keep the declaration prefix-only and probe by behaviour; do not claim slots
  the probe has not proven.
- Activity record should carry a pin outcome alongside the existing move
  outcome vocabulary, so pinned vs already-pinned vs pin-failed reads like
  moved vs already-correct vs failed.
- If the spike shows per-build pin slots are unstable, fall back to scoping
  this spec down to view-pin only on proven builds and keep the rest of the
  decisions unchanged.
