# Manual Performance Budget Verification

`docs/performance/budgets.md` states what DesktopShift promises about speed and
idle cost. Most of it is measured automatically against the real wiring. Two
things are not, because they are properties of the running process rather than of
the pipeline: what the application costs while nobody is touching it, and whether
that cost stays flat over a working day.

| Coverage | Where |
| --- | --- |
| Event-to-move latency measured from receipt through move completion | `tests/DesktopShift.Core.Tests/Performance/AssignmentLatencyInstrumentationTests.cs` |
| The follow grace period attributed as deferral, not as latency | `tests/DesktopShift.Core.Tests/Performance/AssignmentLatencyInstrumentationTests.cs` |
| Desktop creation and switching timed as separate series | `tests/DesktopShift.Core.Tests/Performance/DesktopOperationTimingTests.cs` |
| Percentiles, queue drops, and coalescing rate in one report | `tests/DesktopShift.Core.Tests/Performance/PerformanceReportTests.cs` |
| Percentile arithmetic, retention, and the release-blocker rule | `tests/DesktopShift.Core.Tests/Performance/LatencyDigestTests.cs`, `PerformanceBudgetTests.cs` |
| The normal-local workload against its thresholds | `tests/DesktopShift.Core.Tests/Performance/AssignmentLatencyBenchmarkTests.cs` |
| Configured desktops pre-created at startup, none on a window's path | `tests/DesktopShift.Core.Tests/Performance/StartupDesktopPrecreationTests.cs` |
| No recurring polling loop in the shipping source | `tests/DesktopShift.Windows.Tests/Performance/NoRecurringPollingScanTests.cs` |
| The report reaching an exported diagnostic bundle | `tests/DesktopShift.Core.Tests/Performance/PerformanceBundleTests.cs` |

This document covers what those cannot.

## Before each run

1. Install a **Release** build. A Debug build's timings and memory are not the
   ones being promised.
2. Complete first run, so the configured Managed Desktops exist and rules are
   active.
3. Enable **Record local activity** so `activity.json` can prove that the idle
   period produced no events. The preference is off by default.
4. Restart the machine and sign in cleanly. A session that has been up for days
   carries other applications' memory pressure into the working-set figure.
5. Leave the machine on mains power, and disable any battery-saver or thermal
   profile that throttles the processor. A throttled run measures the profile,
   not the application.
6. Note the Windows build, the machine's core count, and how many Managed
   Desktops are configured. None of the figures below is reproducible without
   them.

## Idle cost

| Case | Action | Expected result |
| --- | --- | --- |
| Idle processor use | Leave DesktopShift running with no windows opening or closing for **10 minutes**. Do not touch the machine. Then open the Activity page and export a diagnostic bundle. Read `performance.json`. | `idleCpuPercent` is under **1**. `measuredOverSeconds` is at least 600, so the figure is averaged over the whole quiet period rather than over a moment. |
| Nothing woke up on its own | In the same export, read `activity.json`. | No activity rows were produced during the quiet period. Nothing polled, nothing reconciled, and nothing enumerated. A row here means something is running on a schedule that should not be. |
| Task Manager agrees | During the quiet period, watch DesktopShift in Task Manager's Details tab. | CPU sits at 0% with occasional single readings, never a steady nonzero figure. This is a cross-check on the report, not a substitute for it. |
| Idle memory | Note working set and private memory from `performance.json` at the start of the quiet period and again after 10 minutes. | Both are flat. A private-memory figure that climbs steadily over a quiet period is a leak, and it is a release blocker whatever the absolute number is. |
| Over a working day | Leave DesktopShift running for a full session — at least six hours of ordinary use — and export a bundle at the end. | Private memory is within a few megabytes of where it started. Bounded stores keep the journal, the activity lists, and every latency series at fixed size, so a growing figure means something is not bounded. |

## Responsiveness with real windows

| Case | Action | Expected result |
| --- | --- | --- |
| A launch lands immediately | Stand on a desktop that is not the target. Launch an application a rule names. | The window arrives on its Managed Desktop and the desktop follows it. There is a brief, deliberate pause first — the grace period — and then it moves. It should not feel like waiting. |
| The numbers back up the feeling | Export a bundle after a dozen such launches. Read `assignmentLatency` in `performance.json`. | `p50Milliseconds` under 30, `p95Milliseconds` under 100, `p99Milliseconds` under 150. `budgetMet` is `true` and `releaseBlockers` is empty. |
| The wait is visible, not hidden | In the same file, compare `eventToMoveLatency` with `assignmentLatency`. | Event-to-move is larger, by roughly the grace period, on the launches that waited it out. Both series are present. If they are identical for every sample, the grace period is not being attributed and the comparison is not proving anything. |
| No desktop was created on the way | Read `desktopsCreated`. | **Zero.** Startup reconciliation created every configured destination before any window was assigned. Any other number means a desktop was made while a window was waiting, and that window's latency includes it. |
| Nothing was dropped | Read `queueDropped` and `queueSaturationEpisodes`. | Both zero for this workload. A drop means a window was never assigned at all, which no amount of good latency makes acceptable. |
| Switching is measured on its own | Read `desktopSwitch`. | Populated, with a sample count matching roughly the number of launches that took you with them. This is the expensive part of an assignment and it has to be readable separately from the move. |

## A missed budget is written down

| Case | Action | Expected result |
| --- | --- | --- |
| A blocker reads as a blocker | If any figure above is missed, read `releaseBlockers` in `performance.json`. | One entry per missed figure, each naming the metric, the budget, the measured value, and what the miss means for somebody using the application. `releaseBlocked` is `true`. |
| Too little data is not a pass | Export a bundle immediately after startup, before twenty windows have been assigned. | `budgetTested` is `false` and `releaseBlocked` is `false`. The report says the latency budget was not tested rather than claiming it was met. |

## Recording a run

Attach the exported bundle to the release checklist, and note the Windows build,
the core count, the Managed Desktop count, and whether the machine was on mains
power. A latency figure without the machine it came from cannot be compared to the
next one.
