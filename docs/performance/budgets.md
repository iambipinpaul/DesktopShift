# Performance budgets

What DesktopShift promises about speed and idle cost, how the numbers are
measured, and what happens when one is missed.

## The workload the budget describes

Every figure below is against one workload, called **normal local**. It is
deliberately ordinary, because responsiveness should be judged on what a person
actually does rather than on a worst case:

- A signed-in local session on a physical machine. Not a Remote Desktop session,
  and not a session being restored.
- Between two and six Managed Desktops, **already created** — startup
  reconciliation has run and bound every configured destination.
- Rules naming a handful of applications, plus the sweep for everything else.
- Windows opening, being activated, and closing one at a time, at the rate a
  person opens them. A few events per second, not hundreds.
- Nothing else contending for the machine.

Bursts are a different workload and are not judged here. A session restore or an
Explorer restart produces hundreds of events at once; what DesktopShift owes
under those conditions is that it loses only what it says it lost, which is what
`WindowEventBurstTests` covers.

## The figures

| Metric | Budget |
| --- | --- |
| Assignment latency P50 | under 30 ms |
| Assignment latency P95 | under 100 ms |
| Assignment latency P99 | under 150 ms |
| Idle processor use | under 1% of the machine |

The upper end is set by what a person notices. A window that lands inside about a
tenth of a second reads as instant; one that takes longer than about a seventh of
a second reads as the application thinking about it.

Idle memory is reported but not budgeted with a fixed ceiling. Working set on
Windows moves with system memory pressure, so a hard number would fail for
reasons that have nothing to do with DesktopShift. What matters is that it is
flat over a session, which the report's private-memory figure is there to show.

## What "assignment latency" measures

Two series are recorded, and they are not the same thing.

**Event-to-move latency** starts at the instant the WinEvent callback received
the event from Windows and ends when the move — and any desktop switch that
followed it — has returned. It includes the time the event waited in the bounded
queue. This is the wall clock a user would feel.

**Assignment latency** is the same measurement with the open-window follow grace
period taken out. That grace period is a deliberate 150 ms wait: a newly opened
window is held briefly so a foreground activation can overtake it, which is the
whole mechanism by which the desktop follows a window the user launched. See
`OpenWindowFollowGrace` for why.

The budget is applied to **assignment latency**, not to event-to-move latency.
Counting the wait would make this a budget on the follow policy, and the only way
to pass it would be to stop following windows. Both series are in every report, so
the wait is visible rather than hidden.

## What else the report carries

- **Queue drops** and saturation episodes. Any drop above zero means a window was
  never assigned. It is reported rather than budgeted because it already has its
  own alarm in `WindowEventQueueSnapshot`.
- **Coalescing rate** — the share of coalescing-eligible events recognised as
  redundant and skipped. Reported rather than budgeted: the right value depends
  entirely on what the user was doing, and both a high and a low rate can be
  correct.
- **Desktop creation** and **desktop switch** durations, kept as separate series.
  Creation is separate for a specific reason — see below.
- **Idle processor use** over the measured interval, and working-set and private
  memory at the moment of reading.

## Desktops are created at startup, not on a window's path

Configured Managed Desktops are created by startup reconciliation, before any
window is assigned: `ManagedDesktopReconciliationHostedService.StartAsync`
reconciles with the `Startup` trigger as part of coming up, and creation happens
there.

That is why creation is timed as its own series. Creating a virtual desktop is by
far the most expensive thing in the pipeline, and if it ever happened while a
window was waiting it would dominate that window's latency. So the test for this
is not a duration — it is a count. In a report covering ordinary window activity,
`DesktopsCreated` must be zero.

## Nothing is measured on a schedule

There is no sampling timer, no polling loop, and no recurring enumeration
anywhere in the shipping pipeline. Window events come from an out-of-context
WinEvent hook, desktop changes come from a topology notification, and shell
disruptions come from Windows messages — all push, none polled.

DesktopShift has four event-driven, one-shot timer sites. They coalesce an event
burst, hold a newly opened window for its foreground signal, confirm that a
briefly hidden BSP window stayed unavailable, and recover if a native
`CurrentChanged` notification is not followed by `Switched`. The same switch
timer also holds one short settling period after `Switched`, because per-window
desktop state can finish later than the Shell notification. Each timer has an
infinite period and cannot repeat. No timer starts while the related event is
absent.

Performance counters are read only when somebody asks: when a user exports a
diagnostic bundle, or when a benchmark builds a report. An idle-cost figure
produced by a recurring measurement would be reporting its own overhead, which is
the one thing it must not do.

`NoRecurringPollingScanTests` checks this against the repository's own source on
every test run, rather than leaving it as a claim in this document.

## When a budget is missed

A missed figure does not warn and does not quietly pass. Every miss produces a
`PerformanceBudgetBreach` naming the metric, the budget, the measured value, and
the consequence for somebody using the application, and
`PerformanceBudgetVerdict.IsReleaseBlocked` becomes true. Either the numbers are
met, or the reason they were not is written down as a release blocker.

Latency figures are judged only once at least `MinimumSampleCount` assignments
have been measured — 20 by default. Below that the verdict reports the budget as
untested, which is neither a pass nor a blocker. Three samples cannot carry a P99.

Idle processor use is judged over any interval of at least one second. Windows
accounts processor time in scheduler ticks of about 16 ms, so over a shorter
window a single tick landing inside or outside the interval swings the answer by
more than the one percent the budget is about. Below a second the report says
"not measurable" instead of producing a number nobody should act on.

## Running the benchmarks

The repeatable benchmarks live in
`tests/DesktopShift.Core.Tests/Performance/AssignmentLatencyBenchmarkTests.cs`.
They drive the real wiring — the bounded queue, the hosted pump, the processor,
the assignment service, and a desktop provider with no real Windows behind it —
through the normal-local workload and assert the percentiles against
`PerformanceBudget.NormalLocal`.

```bash
dotnet test tests/DesktopShift.Core.Tests/DesktopShift.Core.Tests.csproj --no-build -p:Platform=x64 --filter FullyQualifiedName~Performance
```

Build the test project directly for x64 first; a solution build writes to a
different output directory and `dotnet test` will otherwise run a stale assembly.

The timing assertions need a machine that can measure reliably. On a host without
a high-resolution timer, or one where a warm-up calibration shows the scheduler
cannot deliver a short sleep within tolerance — a loaded shared CI runner, for
instance — the benchmark reports inconclusive rather than failing. The structural
assertions, which do not depend on wall-clock accuracy, always run.

## What still needs a real machine

Two figures cannot be measured without the packaged application actually running,
because they are properties of the process rather than of the pipeline: idle
processor use and idle memory over a long session with real windows.

`docs/manual-tests/performance-budgets.md` is the procedure for measuring both on
an installed build, and is what the release checklist uses.
