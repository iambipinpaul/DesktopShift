using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;
using DesktopShift.Windows.Recovery;

namespace DesktopShift.Windows.Tests.Recovery;

/// <summary>
/// What the Windows registration set checks, drops, and takes again — driven
/// entirely by fakes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here installs a hook, activates the native bridge, or registers a
/// real topology notification, and the fake provider counts every operation that
/// could create, switch, or enumerate a desktop so that a regression into
/// touching the machine shows up as a number rather than as a changed machine.
/// </para>
/// <para>
/// The failure modes are the interesting half. A dead native bridge and a shell
/// that refuses hooks are exactly what an Explorer restart looks like from
/// inside the process, and both are reached here by scripting a fake rather than
/// by restarting anything.
/// </para>
/// </remarks>
[TestClass]
public sealed class WindowsNativeRegistrationSetTests
{
    private const string WindowHooksCode = "recovery.window_hooks_not_restored";

    private const string TopologyNotificationsCode =
        "recovery.topology_notifications_not_restored";

    private static readonly Guid CurrentDesktopId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly VirtualDesktopCapabilities ObservableCapabilities = new(
        CanGetWindowDesktopId: true,
        CanMoveWindowToDesktop: true,
        CanEnumerateDesktops: true,
        CanGetCurrentDesktop: true,
        CanCreateDesktop: true,
        CanSwitchDesktop: true,
        CanObserveTopologyChanges: true);

    [TestMethod]
    public async Task CheckAsync_WhenEverythingIsStillHeld_ReadsAsIntact()
    {
        FakeWindowEventSource eventSource = new(isRunning: true);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationValidity validity = await registrations.CheckAsync();

        Assert.IsTrue(validity.IsIntact);
        Assert.IsTrue(validity.WindowHooksLive);
        Assert.IsTrue(validity.TopologyNotificationsLive);
        Assert.IsTrue(validity.IsObservable);
        Assert.IsGreaterThan(0, validity.Explanation.Length);

        // One query, no loop, no retry, no poll — and nothing was changed or
        // re-registered just to find out.
        Assert.AreEqual(1, provider.CurrentDesktopQueryCount);
        Assert.AreEqual(0, provider.NotificationRegistrationCount);
        Assert.AreEqual(0, provider.ForbiddenCallCount);
        Assert.AreEqual(0, eventSource.StartCount);
        Assert.AreEqual(0, eventSource.StopCount);
    }

    [TestMethod]
    public async Task CheckAsync_WhenTheNativeBridgeHasDied_ReadsAsBroken()
    {
        // The registration is held by the same bridge object that answers the
        // query, so a bridge that has stopped answering is a bridge that has
        // stopped holding it.
        FakeWindowEventSource eventSource = new(isRunning: true);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
            CurrentDesktopResult = DesktopTopologyProviderResult<Guid>.Failed(
                "native.current_desktop_failed",
                "The shell interface went away.",
                unchecked((int)0x80004005)),
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationValidity validity = await registrations.CheckAsync();

        Assert.IsFalse(validity.IsIntact);
        Assert.IsTrue(validity.WindowHooksLive);
        Assert.IsFalse(validity.TopologyNotificationsLive);
        Assert.IsTrue(validity.IsObservable);
        Assert.AreEqual(1, provider.CurrentDesktopQueryCount);
        Assert.AreEqual(0, provider.ForbiddenCallCount);
    }

    [TestMethod]
    public async Task CheckAsync_WhenTheHooksAreGone_ReadsAsBroken()
    {
        FakeWindowEventSource eventSource = new(isRunning: false);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationValidity validity = await registrations.CheckAsync();

        Assert.IsFalse(validity.IsIntact);
        Assert.IsFalse(validity.WindowHooksLive);
        Assert.IsTrue(validity.TopologyNotificationsLive);
    }

    [TestMethod]
    public async Task CheckAsync_WhenTopologyCannotBeObserved_DoesNotReadAsBreakage()
    {
        // In Limited Mode the notification capability does not exist to begin
        // with. Reporting its absence as damage would send every display change
        // into a rebuild that could not possibly help.
        FakeWindowEventSource eventSource = new(isRunning: true);
        FakeTopologyProvider provider = new()
        {
            Capabilities = VirtualDesktopCapabilities.DocumentedLimited,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationValidity validity = await registrations.CheckAsync();

        Assert.IsTrue(validity.IsIntact);
        Assert.IsTrue(validity.WindowHooksLive);
        Assert.IsFalse(validity.IsObservable);
        Assert.IsFalse(validity.TopologyNotificationsLive);

        // A provider that cannot observe is not even asked.
        Assert.AreEqual(0, provider.CurrentDesktopQueryCount);
        Assert.AreEqual(0, provider.ForbiddenCallCount);
    }

    [TestMethod]
    public async Task CheckAsync_WhenTheProbeThrows_ReadsAsUnknownRatherThanThrowing()
    {
        FakeWindowEventSource eventSource = new(isRunning: true);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
            CurrentDesktopFailure =
                new InvalidOperationException("the shell interface went away"),
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationValidity validity = await registrations.CheckAsync();

        // Not knowing is a real answer, and it never reads as intact.
        Assert.IsFalse(validity.IsIntact);
        Assert.IsFalse(validity.WindowHooksLive);
        Assert.IsFalse(validity.TopologyNotificationsLive);
        Assert.IsFalse(validity.IsObservable);
        Assert.Contains("the shell interface went away", validity.Explanation);
    }

    [TestMethod]
    public async Task CheckAsync_WhenCancelled_PropagatesWithoutTouchingWindows()
    {
        FakeWindowEventSource eventSource = new(isRunning: true);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await registrations.CheckAsync(cancellation.Token));

        Assert.AreEqual(0, provider.CurrentDesktopQueryCount);
        Assert.AreEqual(0, provider.NotificationRegistrationCount);
        Assert.AreEqual(0, eventSource.StartCount);
        Assert.AreEqual(0, eventSource.StopCount);
    }

    [TestMethod]
    public async Task InvalidateAsync_DropsTheHooks()
    {
        FakeWindowEventSource eventSource = new(isRunning: true);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        await registrations.InvalidateAsync();

        Assert.AreEqual(1, eventSource.StopCount);
        Assert.IsFalse(eventSource.IsRunning);
        Assert.AreEqual(0, eventSource.StartCount);
        Assert.AreEqual(0, provider.ForbiddenCallCount);
    }

    [TestMethod]
    public async Task InvalidateAsync_WhenUnhookingFails_IsStillNotAnError()
    {
        // Unhooking a hook whose shell is already gone is the expected outcome
        // of an Explorer restart, not a fault to report.
        FakeWindowEventSource eventSource = new(isRunning: true)
        {
            StopFailure = new InvalidOperationException("the hook is already gone"),
        };
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        await registrations.InvalidateAsync();

        Assert.AreEqual(1, eventSource.StopCount);
    }

    [TestMethod]
    public async Task ReregisterAsync_TakesTheHooksAndTheTopologyRegistrationAgain()
    {
        FakeWindowEventSource eventSource = new(isRunning: false);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationResult result = await registrations.ReregisterAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, eventSource.StartCount);
        Assert.IsTrue(eventSource.IsRunning);
        Assert.AreEqual(1, provider.NotificationRegistrationCount);
        Assert.AreEqual(0, provider.ForbiddenCallCount);
    }

    [TestMethod]
    public async Task ReregisterAsync_WhenTopologyIsNotObservable_AsksOnlyForTheHooks()
    {
        FakeWindowEventSource eventSource = new(isRunning: false);
        FakeTopologyProvider provider = new()
        {
            Capabilities = VirtualDesktopCapabilities.DocumentedLimited,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationResult result = await registrations.ReregisterAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, eventSource.StartCount);
        Assert.AreEqual(0, provider.NotificationRegistrationCount);
    }

    [TestMethod]
    public async Task ReregisterAsync_WhenTheShellRefusesTheHooks_ReportsInsteadOfThrowing()
    {
        FakeWindowEventSource eventSource = new(isRunning: false)
        {
            StartFailure =
                new InvalidOperationException("SetWinEventHook was refused"),
        };
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationResult result = await registrations.ReregisterAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(WindowHooksCode, result.Code);
        Assert.Contains("SetWinEventHook was refused", result.Message);
        Assert.IsNotNull(result.HResult);

        // A shell that refuses hooks is never asked for notifications either.
        Assert.AreEqual(0, provider.NotificationRegistrationCount);
    }

    [TestMethod]
    public async Task ReregisterAsync_WhenCancelled_DoesNotTakeAnyRegistration()
    {
        FakeWindowEventSource eventSource = new(isRunning: false);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await registrations.ReregisterAsync(cancellation.Token));

        Assert.AreEqual(0, eventSource.StartCount);
        Assert.AreEqual(0, provider.NotificationRegistrationCount);
    }

    [TestMethod]
    public async Task ReregisterAsync_WhenTopologyNotificationsFail_ReportsTheFailure()
    {
        FakeWindowEventSource eventSource = new(isRunning: false);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
            NotificationResult = DesktopTopologyProviderResult.Failed(
                "native.notification_registration_failed",
                "The notification sink was refused.",
                unchecked((int)0x80004005)),
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationResult result = await registrations.ReregisterAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(TopologyNotificationsCode, result.Code);
        Assert.Contains("The notification sink was refused.", result.Message);
        Assert.AreEqual(unchecked((int)0x80004005), result.HResult);
    }

    [TestMethod]
    public async Task ReregisterAsync_WhenTopologyRegistrationThrows_ReportsInsteadOfThrowing()
    {
        FakeWindowEventSource eventSource = new(isRunning: false);
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
            NotificationFailure =
                new InvalidOperationException("the sink could not be advised"),
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationResult result = await registrations.ReregisterAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(TopologyNotificationsCode, result.Code);
        Assert.Contains("the sink could not be advised", result.Message);
    }

    [TestMethod]
    public async Task ReregisterAsync_WhenTheHooksDidNotComeBack_DoesNotClaimSuccess()
    {
        // Start returning without complaining is not evidence that anything is
        // hooked. Success is read back from the source's own state.
        FakeWindowEventSource eventSource = new(isRunning: false)
        {
            StartLeavesTheHooksDead = true,
        };
        FakeTopologyProvider provider = new()
        {
            Capabilities = ObservableCapabilities,
        };
        WindowsNativeRegistrationSet registrations = new(eventSource, provider);

        NativeRegistrationResult result = await registrations.ReregisterAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(WindowHooksCode, result.Code);
        Assert.AreEqual(1, eventSource.StartCount);
    }

    [TestMethod]
    public void Constructor_RejectsAWindowEventSourceThatCannotBeRestarted()
    {
        // Failing at composition beats silently never recovering: a source that
        // cannot be stopped would let every pass report success while the hooks
        // stayed dead.
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new WindowsNativeRegistrationSet(
                new UnrestartableWindowEventSource(),
                new FakeTopologyProvider()));

        Assert.AreEqual("eventSource", exception.ParamName);
    }

    [TestMethod]
    public void Constructor_RejectsMissingDependencies()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(
            () => new WindowsNativeRegistrationSet(
                null!,
                new FakeTopologyProvider()));
        _ = Assert.ThrowsExactly<ArgumentNullException>(
            () => new WindowsNativeRegistrationSet(
                new FakeWindowEventSource(),
                null!));
    }

    /// <summary>
    /// A window event source that records what recovery asked of it.
    /// </summary>
    private sealed class FakeWindowEventSource :
        IWindowEventSource,
        IRestartableWindowEventSource
    {
        public FakeWindowEventSource(bool isRunning = false) => IsRunning = isRunning;

        public bool IsRunning { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Exception? StartFailure { get; init; }

        public Exception? StopFailure { get; init; }

        /// <summary>
        /// Makes <see cref="Start"/> succeed while leaving no hooks installed,
        /// which is what a shell that is still coming up looks like.
        /// </summary>
        public bool StartLeavesTheHooksDead { get; init; }

        public void Start()
        {
            StartCount++;
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            IsRunning = !StartLeavesTheHooksDead;
        }

        public void Stop()
        {
            StopCount++;
            if (StopFailure is not null)
            {
                throw StopFailure;
            }

            IsRunning = false;
        }

        public void Dispose() => IsRunning = false;
    }

    /// <summary>
    /// A source that does not implement <see cref="IRestartableWindowEventSource"/>.
    /// </summary>
    private sealed class UnrestartableWindowEventSource : IWindowEventSource
    {
        public bool IsRunning => true;

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A topology provider that answers the one query recovery makes and counts
    /// every call recovery must never make.
    /// </summary>
    private sealed class FakeTopologyProvider : IDesktopTopologyProvider
    {
        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.provider",
            "Test provider",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: false);

        public VirtualDesktopCapabilities Capabilities { get; init; } =
            VirtualDesktopCapabilities.DocumentedLimited;

        public DesktopTopologyProviderResult<Guid> CurrentDesktopResult { get; init; } =
            DesktopTopologyProviderResult<Guid>.Succeeded(CurrentDesktopId);

        public Exception? CurrentDesktopFailure { get; init; }

        public DesktopTopologyProviderResult NotificationResult { get; init; } =
            DesktopTopologyProviderResult.Succeeded();

        public Exception? NotificationFailure { get; init; }

        public int CurrentDesktopQueryCount { get; private set; }

        public int NotificationRegistrationCount { get; private set; }

        /// <summary>
        /// How many times something recovery is not allowed to do was attempted:
        /// creating a desktop, switching to one, enumerating them, or re-running
        /// the compatibility test. The counter is the assertion surface for "the
        /// machine was not touched".
        /// </summary>
        public int ForbiddenCallCount { get; private set; }

        // Recovery never subscribes to topology changes through this seam, so
        // the event is declared and left inert rather than backed by a field
        // nothing would ever raise.
        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            CurrentDesktopQueryCount++;
            if (CurrentDesktopFailure is not null)
            {
                throw CurrentDesktopFailure;
            }

            return ValueTask.FromResult(CurrentDesktopResult);
        }

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            NotificationRegistrationCount++;
            if (NotificationFailure is not null)
            {
                throw NotificationFailure;
            }

            return ValueTask.FromResult(NotificationResult);
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default)
        {
            _ = build;
            _ = cancellationToken;
            ForbiddenCallCount++;
            throw new NotSupportedException(
                "Recovery does not re-run compatibility through the registration set.");
        }

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            ForbiddenCallCount++;
            throw new NotSupportedException(
                "Checking a registration does not enumerate desktops.");
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            ForbiddenCallCount++;
            throw new NotSupportedException("Recovery never creates a desktop.");
        }

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            _ = desktopId;
            _ = cancellationToken;
            ForbiddenCallCount++;
            throw new NotSupportedException("Recovery never switches desktops.");
        }
    }
}
