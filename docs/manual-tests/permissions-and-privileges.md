# Manual Permissions and Privileges Matrix

A test cannot elevate itself, and a test that opened handles into other users'
processes to prove a denial would be doing the very thing this ticket forbids.
So the privilege boundary is verified by hand against real elevated
applications. Everything that can be automated already is:

| Coverage | Where |
| --- | --- |
| Every `OpenProcess` call site passes only the limited query right | `tests/DesktopShift.Windows.Tests/Security/LeastPrivilegeContractTests.cs` |
| A denial never provokes a retry with a wider right | `LeastPrivilegeContractTests.DeniedIdentity_NeverWidensTheRequestedRightOnRetry` |
| No command-line reader is even constructed by default | `LeastPrivilegeContractTests.IdentityResolver_ByDefaultBuildsNoCommandLineReaderAtAll` |
| Shipping source contains no prohibited technique | `tests/DesktopShift.Windows.Tests/Security/ProhibitedTechniqueScanTests.cs` |
| The scanner itself detects a violation, ignores comments, reads literals | same file |
| Manifests request no elevation, no uiAccess, no extra capability | `tests/DesktopShift.Windows.Tests/Security/ElevationBoundaryTests.cs` |
| Denied identity and denied move reach Activity with the HRESULT intact | `tests/DesktopShift.Windows.Tests/Security/AccessDeniedActivityTests.cs` |
| The elevated-companion boundary is carried into exported diagnostics | `ElevationBoundaryTests.ExportedDiagnostics_CarryTheElevatedCompanionBoundary` |

This matrix covers what those cannot: DesktopShift unelevated, looking at a
window it is genuinely not allowed to touch.

## Before each run

1. DesktopShift is running **unelevated**, as a normal sign-in would start it.
2. At least one application is running **elevated** — an administrator Terminal
   or Registry Editor is the easiest to arrange.
3. A rule exists that would claim the elevated application's window, so the
   denial happens on a window DesktopShift actually wanted.
4. The Activity page is open.

## Matrix

### 1. Identity of an elevated window

| Step | Expected |
| --- | --- |
| Bring the elevated application's window to the foreground | Activity records skip reason `Identity access denied`, carrying `Windows error 5` |
| Read the row | No process name, no package identity, no path is shown. Nothing is guessed from the window title |
| Repeat several times | Same result each time. The right requested never widens, and no elevation prompt ever appears |

The row that would be a defect is one that names the elevated application
anyway — that would mean an identity was inferred from something other than a
handle Windows granted.

### 2. Moving an elevated window

| Step | Expected |
| --- | --- |
| Arrange for a rule to claim a window owned by an elevated application | Outcome **Move failed**, code `window_placement.move_access_denied`, HRESULT `0x80070005` |
| Read the explanation | It names E_ACCESSDENIED and states that DesktopShift runs with the signed-in user's own privileges and does not request elevation |
| Confirm what did not happen | No elevation prompt, no retry, no UAC dialog, and the window stays where it is |

### 3. The companion that does not exist

| Step | Expected |
| --- | --- |
| Read the compatibility explanation after a denial | It states that reaching those windows would need a separate companion with different rights, and that none is implemented, installed, or started |
| Export a diagnostic bundle and open `manifest.json` | `securityNotice` carries the same boundary, whether or not anything was denied during the run |

The purpose of row 3 is that a maintainer reading an exported archive can tell a
window DesktopShift was *refused* apart from a feature that failed to install.

### 4. No elevation is ever requested

| Step | Expected |
| --- | --- |
| Start DesktopShift from a normal sign-in | No UAC prompt, at any point |
| Use every feature: rules, desktops, reassignment, diagnostics export | No UAC prompt, and no feature reports that it needs administrator rights |
| Check Task Manager's **Elevated** column | DesktopShift reads **No** |

### 5. DesktopShift running elevated anyway

| Step | Expected |
| --- | --- |
| Start DesktopShift elevated deliberately | It runs normally. The compatibility surface states that administrator rights are neither requested nor needed |
| Observe the previously denied elevated window | It can now be classified and moved. Nothing else about behaviour changes |

This row exists to confirm elevation is *tolerated but pointless*, not required.
If any feature only works elevated, that is a defect.

### 6. Nothing invasive

| Step | Expected |
| --- | --- |
| Attach Process Explorer to DesktopShift and inspect its open handles | Process handles appear only transiently, and only with the limited query right. No thread handles into other processes |
| Watch for injected modules in other processes | None. DesktopShift's modules appear only in its own process |
| Watch Explorer across a full session | Never patched, never restarted by DesktopShift, and its windows are never scraped for the taskbar |

The static scanner already fails the build if any of these techniques appear in
source. Row 6 is the runtime confirmation that the shipping binary behaves the
way the source says it does.

## Recording a run

For each row, note the outcome, the skip or failure code, and the HRESULT when
one appears. Two results are worth an issue immediately: **any UAC prompt
DesktopShift causes on its own**, and **any identity attributed to a window
whose handle was denied**.
