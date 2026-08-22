using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tiling;

/// <summary>Title-free counts from one reconciliation pass.</summary>
/// <param name="AtUtc">When the pass ran.</param>
/// <param name="ConsideredWindows">How many vouched windows were read.</param>
/// <param name="UnreadableWindows">
/// Windows whose reads failed. A gone window is forgotten. A temporarily
/// unavailable or access-denied window stays tracked without a layout slot so
/// its later restore event can make it rejoin.
/// </param>
/// <param name="IgnoredWindows">Windows an ignore rule claimed.</param>
/// <param name="FloatedWindows">Windows kept out of the tree by policy.</param>
/// <param name="SkippedHiddenWindows">
/// Visible-state skips: minimized, cloaked, or not visible right now.
/// </param>
/// <param name="SkippedMaximizedWindows">
/// Maximized windows left alone until they return to normal state.
/// </param>
/// <param name="SkippedFullScreenWindows">
/// Windows the full-screen heuristic identified, treated like maximized ones.
/// </param>
/// <param name="PlacedWindows">Windows in the committed position batch.</param>
/// <param name="BatchOutcome">How the single batch ended.</param>
/// <param name="BatchSkippedWindows">
/// Handles DeferWindowPos rejected; each was named before its batch was
/// abandoned and rebuilt without it.
/// </param>
/// <param name="BatchFailedWindows">
/// For EndFailed only: every window of the batch Windows could not commit.
/// </param>
/// <param name="ReadBackAdjustments">
/// Windows whose actual rectangle differed from the requested one after one
/// post-commit read — the signature of a foreign size or snap limit. Read
/// once, never polled.
/// </param>
public sealed record TilingReconcileReport(
    DateTimeOffset AtUtc,
    int ConsideredWindows,
    int UnreadableWindows,
    int IgnoredWindows,
    int FloatedWindows,
    int SkippedHiddenWindows,
    int SkippedMaximizedWindows,
    int SkippedFullScreenWindows,
    int PlacedWindows,
    TilingBatchOutcome BatchOutcome = TilingBatchOutcome.Committed,
    ImmutableArray<nint> BatchSkippedWindows = default,
    ImmutableArray<nint> BatchFailedWindows = default,
    ImmutableArray<nint> ReadBackAdjustments = default)
{
    /// <summary>A report for a pass that placed nothing.</summary>
    public static TilingReconcileReport PlacedNothing(DateTimeOffset atUtc) =>
        new(atUtc, 0, 0, 0, 0, 0, 0, 0, 0);

    private ImmutableArray<nint> BatchSkipped =>
        BatchSkippedWindows.IsDefault ? [] : BatchSkippedWindows;

    private ImmutableArray<nint> BatchFailed =>
        BatchFailedWindows.IsDefault ? [] : BatchFailedWindows;

    private ImmutableArray<nint> ReadBack =>
        ReadBackAdjustments.IsDefault ? [] : ReadBackAdjustments;

    /// <summary>Whether anything went wrong enough to look at.</summary>
    public bool HasFailures =>
        UnreadableWindows > 0 ||
        BatchSkipped.Length > 0 ||
        BatchFailed.Length > 0 ||
        ReadBack.Length > 0;

    /// <summary>A title-free diagnostic line.</summary>
    public string ToDiagnosticString()
    {
        return $"considered={ConsideredWindows} placed={PlacedWindows} " +
            $"float={FloatedWindows} ignore={IgnoredWindows} " +
            $"hidden={SkippedHiddenWindows} maximized={SkippedMaximizedWindows} " +
            $"fullscreen={SkippedFullScreenWindows} unreadable={UnreadableWindows} " +
            $"batch={BatchOutcome} batchSkipped={BatchSkipped.Length} " +
            $"batchFailed={BatchFailed.Length} adjusted={ReadBack.Length}";
    }
}

/// <summary>
/// One window the layout tracks: its slot, its workspace, and what its last
/// read said about frame margins.
/// </summary>
internal sealed class TilingTrackedWindow
{
    /// <summary>The layout slot, or null while floating or unadmitted.</summary>
    public LeafToken? Token { get; set; }

    /// <summary>The workspace holding <see cref="Token"/>, when any.</summary>
    public TilingWorkspaceKey? WorkspaceKey { get; set; }

    /// <summary>Whether classification already ran for this window.</summary>
    public bool DispositionResolved { get; set; }

    /// <summary>The classification result once resolved.</summary>
    public TilingDisposition Disposition { get; set; }

    /// <summary>
    /// The outer rectangle this coordinator last committed for the window,
    /// or null when it was never placed here. The pass may only treat "at
    /// target" as done when it did the placing itself; a foreign window that
    /// merely sits on its planned tile still asks for one placement.
    /// </summary>
    public TileRect? LastPlacedRect { get; set; }

    /// <summary>
    /// A visible-frame width that the application proved it will not shrink
    /// below. Zero means that no foreign minimum was observed.
    /// </summary>
    public int MinimumVisibleWidthPixels { get; set; }

    /// <summary>
    /// A visible-frame height that the application proved it will not shrink
    /// below. Zero means that no foreign minimum was observed.
    /// </summary>
    public int MinimumVisibleHeightPixels { get; set; }
}

/// <summary>
/// Drives the pure BSP engine against real windows through the adapter seams.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator owns every piece of timing and sequencing policy:
/// operation serialization (one reconcile at a time), burst coalescing (many
/// triggers collapse into one pass), feedback suppression (windows DesktopShift
/// just moved do not immediately trigger another move), and reconciliation on
/// startup, desktop switches, and display changes.
/// </para>
/// <para>
/// Coalescing is asynchronous by construction. A trigger only increments a
/// counter and makes sure a drain exists; it never runs a pass on its own
/// call stack. The drain yields to the caller's context first, so every
/// synchronous trigger in the current burst lands before the one pass that
/// answers all of them.
/// </para>
/// <para>
/// One visible reconciliation produces exactly one placement batch: every
/// planned move across every monitor goes to a single
/// <see cref="ITilingPlacementExecutor.Apply"/> call, which commits it as one
/// deferred-position sequence. Actual rectangles are read once afterwards and
/// never polled.
/// </para>
/// <para>
/// When tiling is disabled this class performs no window placement at all —
/// not even a read-back — so turning the feature off restores stock Windows
/// behavior completely.
/// </para>
/// </remarks>
public sealed class TilingCoordinator : ITilingTrigger, IDisposable
{
    /// <summary>
    /// How long a window stays immune to move-size triggers after DesktopShift
    /// moved it itself. Long enough for the placement echo to land, short
    /// enough that a real user drag end is never swallowed.
    /// </summary>
    public static readonly TimeSpan FeedbackSuppressionWindow =
        TimeSpan.FromMilliseconds(250);

    private readonly Func<TilingSettings> settingsSource;
    private readonly ITilingMonitorCatalog monitorCatalog;
    private readonly ITilingWindowReader windowReader;
    private readonly ITilingIdentitySource identitySource;
    private readonly ITilingPlacementExecutor placementExecutor;
    private readonly IDesktopTopologyProvider? topologyProvider;
    private readonly ITopLevelWindowEnumerator? windowEnumerator;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan coalesceDelay;
    private readonly ITilingFocusReader? focusReader;
    private readonly Func<Guid, string?>? managedDesktopKeySource;
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly SemaphoreSlim coalesceWait = new(0, 1);
    private readonly object stateLock = new();
    private readonly Dictionary<nint, TilingTrackedWindow> tracked = [];
    private readonly Dictionary<nint, DateTimeOffset> suppressedUntil = [];
    private readonly TilingLayoutCatalog catalog = new();
    private int pendingRequests;
    private int drainRunning;
    private int enumerateBeforeNextPass;

    /// <summary>Raised after every completed reconciliation pass.</summary>
    public event EventHandler<TilingReconcileReport>? ReconcileCompleted;

    /// <summary>The most recent reconciliation report, if any.</summary>
    public TilingReconcileReport? LastReport { get; private set; }

    /// <summary>Whether the current configuration asks the coordinator to run.</summary>
    public bool IsEnabled => settingsSource().IsEnabled;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="settingsSource">
    /// Reads current tiling settings; called once per pass so configuration
    /// changes apply without restarts.
    /// </param>
    /// <param name="monitorCatalog">Monitor and work-area reads.</param>
    /// <param name="windowReader">Window state reads.</param>
    /// <param name="identitySource">Identity resolution for classification.</param>
    /// <param name="placementExecutor">The atomic position batch.</param>
    /// <param name="topologyProvider">
    /// Current-desktop reads and topology change signals, when available.
    /// Without it only explicit passes run.
    /// </param>
    /// <param name="windowEnumerator">
    /// Top-level enumeration for startup reconciliation, when available.
    /// </param>
    /// <param name="clock">Time source for suppression windows.</param>
    /// <param name="coalesceDelay">
    /// How long triggers are allowed to pile up before one pass drains them.
    /// Zero collapses coalescing entirely, which tests use.
    /// </param>
    /// <param name="managedDesktopKeySource">
    /// Resolves a temporary Windows desktop ID to its stable managed desktop
    /// key. An unmapped desktop uses the common tiling policy.
    /// </param>
    public TilingCoordinator(
        Func<TilingSettings> settingsSource,
        ITilingMonitorCatalog monitorCatalog,
        ITilingWindowReader windowReader,
        ITilingIdentitySource identitySource,
        ITilingPlacementExecutor placementExecutor,
        IDesktopTopologyProvider? topologyProvider = null,
        ITopLevelWindowEnumerator? windowEnumerator = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? coalesceDelay = null,
        ITilingFocusReader? focusReader = null,
        Func<Guid, string?>? managedDesktopKeySource = null)
    {
        this.settingsSource = settingsSource ??
            throw new ArgumentNullException(nameof(settingsSource));
        this.monitorCatalog = monitorCatalog ??
            throw new ArgumentNullException(nameof(monitorCatalog));
        this.windowReader = windowReader ??
            throw new ArgumentNullException(nameof(windowReader));
        this.identitySource = identitySource ??
            throw new ArgumentNullException(nameof(identitySource));
        this.placementExecutor = placementExecutor ??
            throw new ArgumentNullException(nameof(placementExecutor));
        this.topologyProvider = topologyProvider;
        this.windowEnumerator = windowEnumerator;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.coalesceDelay = coalesceDelay ?? TimeSpan.FromMilliseconds(50);
        this.focusReader = focusReader;
        this.managedDesktopKeySource = managedDesktopKeySource;

        if (topologyProvider is not null)
        {
            topologyProvider.TopologyChanged += OnTopologyChanged;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (topologyProvider is not null)
        {
            topologyProvider.TopologyChanged -= OnTopologyChanged;
        }

        // A coalesced drain can still be finishing during host shutdown. The
        // two small semaphores are process-local and must stay valid until that
        // fire-and-forget drain returns.
    }

    /// <inheritdoc />
    public void NotifyMoveSizeEnded(nint windowHandle)
    {
        lock (stateLock)
        {
            if (!tracked.ContainsKey(windowHandle))
            {
                return;
            }

            if (suppressedUntil.TryGetValue(
                windowHandle,
                out DateTimeOffset until) && clock() < until)
            {
                // This is our own placement echoing back, not a user drag.
                return;
            }
        }

        RequestReconcile();
    }

    /// <inheritdoc />
    public void NotifyWindowMinimized(nint windowHandle)
    {
        // EVENT_SYSTEM_MINIMIZESTART is authoritative even when IsIconic and
        // the window rectangle have not settled yet. Forgetting the window
        // releases its leaf now. EVENT_SYSTEM_MINIMIZEEND re-vouches the same
        // HWND through NotifyWindowStateChanged when it is restored.
        Forget(windowHandle);
        if (windowEnumerator is not null)
        {
            // A visible survivor can have been forgotten after an earlier
            // assignment returned NotPlaced. Enumerate once in the queued
            // event-driven pass so that survivor can close over this leaf.
            Volatile.Write(ref enumerateBeforeNextPass, 1);
        }

        RequestReconcile();
    }

    /// <inheritdoc />
    public void NotifyWindowStateChanged(nint windowHandle)
    {
        bool newlyVouched;
        lock (stateLock)
        {
            // A restore event is fresh proof that this HWND exists. Some
            // single-instance applications briefly make their HWND look gone
            // while minimizing, so the earlier pass may have forgotten it.
            // Re-vouch it here and resolve its identity again in the pass.
            newlyVouched = tracked.TryAdd(
                windowHandle,
                new TilingTrackedWindow());
        }

        if (newlyVouched && windowEnumerator is not null)
        {
            // The restored HWND was unknown, so another visible incumbent may
            // also have fallen out of tracking during the same minimize burst.
            // Enumerate once inside the queued pass. This is event-driven, not
            // recurring polling, and keeps the observation callback fast.
            Volatile.Write(ref enumerateBeforeNextPass, 1);
        }

        RequestReconcile();
    }

    /// <inheritdoc />
    public void NotifyWindowDestroyed(nint windowHandle)
    {
        bool removedSlot = false;
        lock (stateLock)
        {
            suppressedUntil.Remove(windowHandle);
            if (tracked.Remove(
                windowHandle,
                out TilingTrackedWindow? entry) &&
                entry.Token is LeafToken token &&
                entry.WorkspaceKey is TilingWorkspaceKey key &&
                catalog.Contains(key))
            {
                _ = catalog.GetOrCreate(key).Remove(token);
                removedSlot = true;
            }
        }

        if (removedSlot)
        {
            RequestReconcile();
        }
    }

    /// <inheritdoc />
    public void NotifyAssignmentCompleted(in TilingAssignmentNotification notification)
    {
        if (notification.Disposition == TilingAssignmentDisposition.NotPlaced)
        {
            Forget(notification.WindowHandle);
            // Losing the slot frees layout space; the next pass lets the
            // survivors close over it in one committed batch.
            RequestReconcile();
            return;
        }

        lock (stateLock)
        {
            _ = tracked.TryAdd(
                notification.WindowHandle,
                new TilingTrackedWindow());
            suppressedUntil[notification.WindowHandle] =
                clock().Add(FeedbackSuppressionWindow);
        }

        RequestReconcile();
    }

    /// <summary>
    /// Requests one coalesced reconciliation pass. Returns immediately; the
    /// pass runs in the background once the configured coalescing delay has
    /// let any burst of triggers pile up.
    /// </summary>
    public void RequestReconcile()
    {
        Interlocked.Increment(ref pendingRequests);
        if (Interlocked.CompareExchange(ref drainRunning, 1, 0) == 0)
        {
            _ = DrainAsync();
        }
    }

    /// <summary>
    /// Runs one full reconciliation pass immediately, serialized behind any
    /// other pass. Tests and startup paths call this directly.
    /// </summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = reconcileGate.Release();
        }
    }

    /// <summary>
    /// Vouches for every top-level window and reconciles once. This is the
    /// startup path: with nothing persisted, replaying what is observable now
    /// rebuilds every workspace deterministically.
    /// </summary>
    public async Task ReconcileAllWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        if (windowEnumerator is null || !settingsSource().IsEnabled)
        {
            return;
        }

        VouchAllTopLevelWindows();

        await ReconcileOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        try
        {
            // Yield before the first pass so a burst of synchronous triggers
            // — several NotifyAssignmentCompleted calls in a row, say — all
            // land while the caller is still on its stack. Without this, a
            // fully synchronous pipeline would run one pass per trigger and
            // coalescing would never happen.
            await Task.Yield();

            while (true)
            {
                if (coalesceDelay > TimeSpan.Zero)
                {
                    _ = await coalesceWait
                        .WaitAsync(coalesceDelay)
                        .ConfigureAwait(false);
                }

                await ReconcileOnceAsync().ConfigureAwait(false);

                if (Volatile.Read(ref pendingRequests) == 0)
                {
                    return;
                }

                // A second burst arrived during the pass. The next loop waits
                // for its short coalescing window before answering it.
            }
        }
        finally
        {
            Volatile.Write(ref drainRunning, 0);

            // A trigger that slipped in after the final look must not sit
            // unanswered because the drain had already decided to stop.
            if (Volatile.Read(ref pendingRequests) > 0 &&
                Interlocked.CompareExchange(ref drainRunning, 1, 0) == 0)
            {
                _ = DrainAsync();
            }
        }
    }

    private void OnTopologyChanged(object? sender, DesktopTopologyChangedEventArgs e)
    {
        // Desktop switches must keep every desktop-monitor tree intact. The
        // next pass reads the new current desktop and applies only its tree.
        // A real monitor change produces new monitor keys, so stale monitor
        // workspaces become unreachable without erasing desktop layouts here.
        RequestReconcile();
    }

    private void Forget(nint handle)
    {
        lock (stateLock)
        {
            suppressedUntil.Remove(handle);
            if (tracked.Remove(
                handle,
                out TilingTrackedWindow? entry) &&
                entry.Token is LeafToken token &&
                entry.WorkspaceKey is TilingWorkspaceKey key &&
                catalog.Contains(key))
            {
                _ = catalog.GetOrCreate(key).Remove(token);
            }
        }
    }

    private sealed class PassEntry
    {
        public required nint Handle { get; init; }
        public required TilingWindowState State { get; init; }
        public required TilingTrackedWindow Tracked { get; init; }
        public required TilingMonitorInfo Monitor { get; init; }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        // Every request made before this pass committed to reading the world
        // is answered by it. Clearing here — under the reconcile gate, not in
        // the drain before it — is what collapses a burst of triggers into
        // one pass instead of trailing into a second empty one.
        Volatile.Write(ref pendingRequests, 0);

        TilingSettings settings = settingsSource();
        DateTimeOffset atUtc = clock();
        if (!settings.IsEnabled)
        {
            // Disabled means disabled: no placement, no reads, no echoes.
            Publish(new TilingReconcileReport(atUtc, 0, 0, 0, 0, 0, 0, 0, 0));
            return;
        }

        Guid? currentDesktop = await TryReadCurrentDesktopAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentDesktop is null)
        {
            Publish(new TilingReconcileReport(atUtc, 0, 0, 0, 0, 0, 0, 0, 0));
            return;
        }

        IReadOnlyList<TilingMonitorInfo> monitors = monitorCatalog.ReadMonitors();
        if (Interlocked.Exchange(ref enumerateBeforeNextPass, 0) != 0)
        {
            VouchAllTopLevelWindows();
        }

        nint focusedWindow = focusReader?.ReadFocusedWindow() ?? 0;
        Dictionary<nint, TilingMonitorInfo> byHandle = [];
        foreach (TilingMonitorInfo monitor in monitors)
        {
            byHandle[monitor.Handle] = monitor;
        }

        List<(nint Handle, TilingTrackedWindow Entry)> snapshot;
        lock (stateLock)
        {
            // Handle order is the most deterministic order available without
            // persistence; replaying admissions in it makes a rebuild stable
            // within one session.
            snapshot =
                [.. tracked.Select(pair => (pair.Key, pair.Value))
                    .OrderBy(pair => pair.Key)];
        }

        int considered = 0;
        int unreadable = 0;
        int ignored = 0;
        int floated = 0;
        int hidden = 0;
        int maximized = 0;
        int fullScreen = 0;
        Dictionary<TilingWorkspaceKey, List<PassEntry>> placeableByWorkspace = [];

        foreach ((nint handle, TilingTrackedWindow entry) in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TilingWindowReading reading = windowReader.Read(handle);
            if (reading.Status != TilingReadStatus.Succeeded ||
                reading.State is not TilingWindowState state)
            {
                unreadable++;

                // Only a confirmed dead HWND is forgotten. Some applications
                // briefly refuse GetWindowRect while minimizing. Their same
                // HWND later emits a restore event, so it must remain tracked
                // even though it cannot reserve visible BSP space now.
                if (reading.Status == TilingReadStatus.WindowGone)
                {
                    Forget(handle);
                }
                else
                {
                    ReleaseSlot(entry);
                }

                continue;
            }

            considered++;

            if (state.DesktopId is Guid policyDesktopId &&
                !IsTilingEnabledForDesktop(settings, policyDesktopId))
            {
                // A disabled desktop owns no BSP leaves. Windows keeps the
                // current window positions until the user enables it again.
                floated++;
                ReleaseSlot(entry);
                continue;
            }

            if (!entry.DispositionResolved)
            {
                WindowIdentity? identity =
                    await identitySource.ResolveAsync(handle, cancellationToken)
                        .ConfigureAwait(false);
                entry.Disposition = WindowFloatClassifier.Classify(
                    identity,
                    settings.FloatRules,
                    settings.IgnoreRules);
                entry.DispositionResolved = true;
            }

            switch (entry.Disposition)
            {
                case TilingDisposition.Ignore:
                    ignored++;
                    Forget(handle);
                    continue;
                case TilingDisposition.Float:
                    floated++;
                    ReleaseSlot(entry);
                    continue;
            }

            if (!byHandle.TryGetValue(state.MonitorHandle, out TilingMonitorInfo? monitor))
            {
                // Unknown or vanished monitor: nothing safe to attribute.
                floated++;
                ReleaseSlot(entry);
                continue;
            }

            if (state.DesktopId is null)
            {
                // Elevated or protected: the desktop manager refused to say,
                // so no workspace can claim this window.
                floated++;
                ReleaseSlot(entry);
                continue;
            }

            var workspaceKey = new TilingWorkspaceKey(
                state.DesktopId.Value,
                monitor.DeviceKey);
            if (state.DesktopId.Value != currentDesktop.Value)
            {
                // A window still on the desktop where it was admitted keeps
                // that inactive desktop's slot. A window that moved away from
                // its old workspace releases the old leaf now, so the visible
                // desktop never keeps an empty tile.
                if (entry.WorkspaceKey != workspaceKey)
                {
                    ReleaseSlot(entry);
                }

                continue;
            }

            if (state.IsMinimized || state.IsCloaked || !state.IsVisible)
            {
                hidden++;
                ReleaseSlot(entry);
                continue;
            }

            if (state.IsMaximized)
            {
                maximized++;
                ReleaseSlot(entry);
                continue;
            }

            if (TilingWindowStateRules.IsFullScreen(
                state,
                monitor.MonitorBoundsPixels))
            {
                fullScreen++;
                ReleaseSlot(entry);
                continue;
            }

            EnsureSlot(entry, workspaceKey, monitor, settings, focusedWindow);
            if (entry.Token is not LeafToken admitted)
            {
                // Nothing could split for it; float instead.
                floated++;
                continue;
            }

            if (!placeableByWorkspace.TryGetValue(
                workspaceKey,
                out List<PassEntry>? placeable))
            {
                placeable = [];
                placeableByWorkspace[workspaceKey] = placeable;
            }

            placeable.Add(new PassEntry
            {
                Handle = handle,
                State = state,
                Tracked = entry,
                Monitor = monitor,
            });
        }

        List<TilingPlacementRequest> requests = [];
        foreach ((TilingWorkspaceKey key, List<PassEntry> entries) in placeableByWorkspace)
        {
            TilingWorkspaceState workspace = catalog.GetOrCreate(key);
            HashSet<LeafToken> ownedTokens = entries
                .Where(static entry => entry.Tracked.Token is not null)
                .Select(static entry => entry.Tracked.Token!.Value)
                .ToHashSet();
            workspace.PruneTo(ownedTokens);
            double scaleFactor = entries[0].Monitor.DpiX / 96.0;
            int innerGapPixels =
                new TileGap(settings.InnerGap).ScaleToPixels(scaleFactor);
            int outerGapPixels =
                new TileGap(settings.OuterGap).ScaleToPixels(scaleFactor);
            bool hasForeignMinimum = entries.Any(entry =>
                entry.Tracked.MinimumVisibleWidthPixels > 0 ||
                entry.Tracked.MinimumVisibleHeightPixels > 0);
            ImmutableArray<BspPlacement> placements;
            if (hasForeignMinimum)
            {
                (int defaultWidth, int defaultHeight) = new TileMinimum(
                    settings.MinimumTileWidth,
                    settings.MinimumTileHeight).ScaleToPixels(scaleFactor);
                Dictionary<LeafToken, BspLeafMinimum> minimums = [];
                foreach (LeafToken token in workspace.Tree.Leaves)
                {
                    PassEntry? owner = entries.Find(
                        candidate => candidate.Tracked.Token == token);
                    minimums[token] = new BspLeafMinimum(
                        Math.Max(
                            defaultWidth,
                            owner?.Tracked.MinimumVisibleWidthPixels ?? 0),
                        Math.Max(
                            defaultHeight,
                            owner?.Tracked.MinimumVisibleHeightPixels ?? 0));
                }

                placements = BspLayoutPlanner.PlanConstrained(
                    workspace.Tree,
                    entries[0].Monitor.WorkAreaPixels,
                    innerGapPixels,
                    outerGapPixels,
                    minimums);
            }
            else
            {
                placements = BspLayoutPlanner.Plan(
                    workspace.Tree,
                    entries[0].Monitor.WorkAreaPixels,
                    innerGapPixels,
                    outerGapPixels);
            }

            foreach (BspPlacement placement in placements)
            {
                PassEntry? match = entries.Find(
                    candidate => candidate.Tracked.Token == placement.Token);
                if (match is null)
                {
                    // A reserved slot belonging to a minimized, maximized,
                    // full-screen, or off-desktop window: planned but silent.
                    continue;
                }

                (int left, int top, int right, int bottom) =
                    match.State.FrameMargins;
                TileRect outer = new(
                    placement.Rect.X - left,
                    placement.Rect.Y - top,
                    placement.Rect.Width + left + right,
                    placement.Rect.Height + top + bottom);
                if (outer == match.State.WindowRectPixels &&
                    outer == match.Tracked.LastPlacedRect)
                {
                    // Already exactly where this coordinator put it. A window
                    // that merely sits on its planned tile without having
                    // been placed here still earns one placement.
                    continue;
                }

                requests.Add(new TilingPlacementRequest(match.Handle, outer));
            }
        }

        TilingBatchResult batch = requests.Count == 0
            ? TilingBatchResult.CommittedAll([])
            : placementExecutor.Apply(requests);

        ImmutableArray<nint> adjusted = ReadBackCommitted(
            batch,
            requests,
            out bool foreignMinimumChanged);

        foreach (nint handle in batch.CommittedWindows)
        {
            lock (stateLock)
            {
                suppressedUntil[handle] = clock().Add(FeedbackSuppressionWindow);
                if (tracked.TryGetValue(handle, out TilingTrackedWindow? placed))
                {
                    placed.LastPlacedRect = requests
                        .First(request => request.WindowHandle == handle)
                        .WindowRectPixels;
                }
            }
        }

        Publish(new TilingReconcileReport(
            clock(),
            considered,
            unreadable,
            ignored,
            floated,
            hidden,
            maximized,
            fullScreen,
            batch.CommittedWindows.Count,
            batch.Outcome,
            batch.SkippedWindows.ToImmutableArray(),
            batch.FailedWindows.ToImmutableArray(),
            adjusted));

        if (foreignMinimumChanged)
        {
            // One bounded repair pass uses the new proven minimum. A later
            // pass is requested only if Windows proves an even larger size;
            // unchanged read-back never polls or loops.
            RequestReconcile();
        }
    }

    private ImmutableArray<nint> ReadBackCommitted(
        TilingBatchResult batch,
        List<TilingPlacementRequest> requests,
        out bool foreignMinimumChanged)
    {
        foreignMinimumChanged = false;
        if (batch.CommittedWindows.Count == 0)
        {
            return [];
        }

        Dictionary<nint, TileRect> wanted = requests.ToDictionary(
            request => request.WindowHandle,
            request => request.WindowRectPixels);
        List<nint> adjusted = [];
        foreach (nint handle in batch.CommittedWindows)
        {
            if (!wanted.TryGetValue(handle, out TileRect target))
            {
                continue;
            }

            TilingWindowReading reading = windowReader.Read(handle);
            if (reading.Status == TilingReadStatus.Succeeded &&
                reading.State is TilingWindowState actual &&
                actual.WindowRectPixels != target)
            {
                adjusted.Add(handle);
                (int left, int top, int right, int bottom) = actual.FrameMargins;
                int targetVisibleWidth = Math.Max(
                    target.Width - left - right,
                    0);
                int targetVisibleHeight = Math.Max(
                    target.Height - top - bottom,
                    0);
                lock (stateLock)
                {
                    if (!tracked.TryGetValue(
                        handle,
                        out TilingTrackedWindow? trackedWindow))
                    {
                        continue;
                    }

                    if (actual.VisibleFramePixels.Width > targetVisibleWidth &&
                        actual.VisibleFramePixels.Width >
                            trackedWindow.MinimumVisibleWidthPixels)
                    {
                        trackedWindow.MinimumVisibleWidthPixels =
                            actual.VisibleFramePixels.Width;
                        foreignMinimumChanged = true;
                    }

                    if (actual.VisibleFramePixels.Height > targetVisibleHeight &&
                        actual.VisibleFramePixels.Height >
                            trackedWindow.MinimumVisibleHeightPixels)
                    {
                        trackedWindow.MinimumVisibleHeightPixels =
                            actual.VisibleFramePixels.Height;
                        foreignMinimumChanged = true;
                    }
                }
            }
        }

        return [.. adjusted];
    }

    private void EnsureSlot(
        TilingTrackedWindow entry,
        TilingWorkspaceKey workspaceKey,
        TilingMonitorInfo monitor,
        TilingSettings settings,
        nint focusedWindow)
    {
        if (entry.Token is LeafToken existing &&
            entry.WorkspaceKey == workspaceKey)
        {
            return;
        }

        ReleaseSlot(entry);
        double scaleFactor = monitor.DpiX / 96.0;
        (int minWidthPx, int minHeightPx) = new TileMinimum(
            settings.MinimumTileWidth,
            settings.MinimumTileHeight).ScaleToPixels(scaleFactor);
        TilingWorkspaceState workspace = catalog.GetOrCreate(workspaceKey);
        LeafToken? preferredLeaf = null;
        lock (stateLock)
        {
            if (tracked.TryGetValue(
                    focusedWindow,
                    out TilingTrackedWindow? focused) &&
                focused.WorkspaceKey == workspaceKey)
            {
                preferredLeaf = focused.Token;
            }
        }

        int outerGapPixels =
            new TileGap(settings.OuterGap).ScaleToPixels(scaleFactor);
        TileRect admissionArea = new(
            monitor.WorkAreaPixels.X + outerGapPixels,
            monitor.WorkAreaPixels.Y + outerGapPixels,
            Math.Max(monitor.WorkAreaPixels.Width - (outerGapPixels * 2), 0),
            Math.Max(monitor.WorkAreaPixels.Height - (outerGapPixels * 2), 0));
        if (workspace.TryAdmit(
            admissionArea,
            new TileGap(settings.InnerGap).ScaleToPixels(scaleFactor),
            minWidthPx,
            minHeightPx,
            preferredLeaf,
            out LeafToken admitted))
        {
            entry.Token = admitted;
            entry.WorkspaceKey = workspaceKey;
        }
    }

    private void ReleaseSlot(TilingTrackedWindow entry)
    {
        if (entry.Token is not LeafToken token)
        {
            return;
        }

        if (entry.WorkspaceKey is TilingWorkspaceKey key &&
            catalog.Contains(key))
        {
            _ = catalog.GetOrCreate(key).Remove(token);
        }

        entry.Token = null;
        entry.WorkspaceKey = null;
    }

    private void VouchAllTopLevelWindows()
    {
        if (windowEnumerator is null)
        {
            return;
        }

        IReadOnlyList<nint> handles = windowEnumerator.Enumerate();
        lock (stateLock)
        {
            foreach (nint handle in handles)
            {
                _ = tracked.TryAdd(handle, new TilingTrackedWindow());
            }
        }
    }

    private async Task<Guid?> TryReadCurrentDesktopAsync(
        CancellationToken cancellationToken)
    {
        if (topologyProvider is null ||
            !topologyProvider.Capabilities.CanGetCurrentDesktop)
        {
            return null;
        }

        DesktopTopologyProviderResult<Guid> result =
            await topologyProvider.GetCurrentDesktopIdAsync(cancellationToken)
                .ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }

    private bool IsTilingEnabledForDesktop(
        TilingSettings settings,
        Guid desktopId)
    {
        if (settings.DisabledManagedDesktopKeys.IsEmpty ||
            managedDesktopKeySource is null)
        {
            return true;
        }

        string? semanticKey = managedDesktopKeySource(desktopId);
        return semanticKey is null ||
            settings.IsEnabledForManagedDesktop(semanticKey);
    }

    private void Publish(TilingReconcileReport report)
    {
        LastReport = report;
        ReconcileCompleted?.Invoke(this, report);
    }
}
