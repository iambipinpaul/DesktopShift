using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DesktopShift.App.Tray;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Presentation.Tests.Tray;

internal sealed class FakeTrayIconHost : ITrayIconHost
{
    private readonly List<TrayMenuModel> _shownMenus = [];
    private readonly List<TrayNotification> _notifications = [];

    public event EventHandler<TrayCommandEventArgs>? CommandInvoked;

    public IReadOnlyList<TrayMenuModel> ShownMenus => _shownMenus;

    public IReadOnlyList<TrayNotification> Notifications => _notifications;

    public TrayMenuModel? LastMenu => _shownMenus.Count == 0 ? null : _shownMenus[^1];

    public bool IsDisposed { get; private set; }

    public void Show(TrayMenuModel model) => _shownMenus.Add(model);

    public void Notify(TrayNotification notification) => _notifications.Add(notification);

    public void Dispose() => IsDisposed = true;

    /// <summary>
    /// Simulates the user picking an item from the notification-area menu.
    /// </summary>
    public void Click(TrayCommand command) =>
        CommandInvoked?.Invoke(this, new TrayCommandEventArgs(command));

    public TrayMenuItem ItemFor(TrayCommand command) =>
        LastMenu?.Items.Single(item => item.Command == command) ??
        throw new InvalidOperationException("The tray icon has not shown a menu.");
}

internal sealed class FakeShellWindowSurface : IShellWindowSurface
{
    public bool IsVisible { get; private set; }

    public int ShowCount { get; private set; }

    public int HideCount { get; private set; }

    public List<string> Navigations { get; } = [];

    public void ShowAndActivate()
    {
        IsVisible = true;
        ShowCount++;
    }

    public void Hide()
    {
        IsVisible = false;
        HideCount++;
    }

    public void NavigateTo(string destinationKey) => Navigations.Add(destinationKey);
}

internal sealed class FakePauseController : IAutomaticAssignmentPauseController
{
    public bool IsPaused { get; private set; }

    public event EventHandler<AutomaticAssignmentPauseChangedEventArgs>? PauseStateChanged;

    public void Pause() => SetPaused(true);

    public void Resume() => SetPaused(false);

    public bool TogglePause()
    {
        SetPaused(!IsPaused);
        return IsPaused;
    }

    private void SetPaused(bool isPaused)
    {
        if (IsPaused == isPaused)
        {
            return;
        }

        IsPaused = isPaused;
        PauseStateChanged?.Invoke(this, new AutomaticAssignmentPauseChangedEventArgs(isPaused));
    }
}

internal sealed class FakeReassignmentService : IWindowReassignmentService
{
    private readonly TaskCompletionSource? _gate;

    public FakeReassignmentService(TaskCompletionSource? gate = null)
    {
        _gate = gate;
    }

    public int ReassignAllCount { get; private set; }

    public Exception? FailWith { get; set; }

    public Task<WindowReassignmentBatchResult> ReconcileStartupAsync(
        CancellationToken cancellationToken = default) =>
        ReassignAllAsync(cancellationToken);

    public async Task<WindowReassignmentBatchResult> ReassignAllAsync(
        CancellationToken cancellationToken = default)
    {
        ReassignAllCount++;

        if (_gate is not null)
        {
            await _gate.Task.WaitAsync(cancellationToken);
        }

        return FailWith is null
            ? new WindowReassignmentBatchResult(
                Guid.NewGuid(),
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromMilliseconds(5),
                WindowEventKind.ManualReassignment,
                3,
                [])
            : throw FailWith;
    }
}

internal sealed class FakeCompatibilityCoordinator : ICompatibilityCoordinator
{
    public FakeCompatibilityCoordinator(CompatibilityStatus? status = null)
    {
        Current = status ?? CompatibilityStatuses.FullModePassed();
    }

    public CompatibilityStatus Current { get; private set; }

    public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged;

    public ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Current.LastTest);
    }

    public void Publish(CompatibilityStatus status)
    {
        Current = status;
        StatusChanged?.Invoke(this, new CompatibilityStatusChangedEventArgs(status));
    }
}

internal static class CompatibilityStatuses
{
    public static CompatibilityStatus FullModePassed() => Create(
        DesktopTopologyProviderMode.Full,
        new CompatibilityTestResult(
            CompatibilityTestOutcome.PassedFullMode,
            DateTimeOffset.UnixEpoch,
            "Full Mode test passed.",
            []));

    public static CompatibilityStatus LimitedModePassed(DateTimeOffset testedAtUtc) => Create(
        DesktopTopologyProviderMode.Limited,
        new CompatibilityTestResult(
            CompatibilityTestOutcome.PassedLimitedMode,
            testedAtUtc,
            "Limited Mode test passed.",
            []));

    public static CompatibilityStatus TestFailed(DateTimeOffset testedAtUtc) => Create(
        DesktopTopologyProviderMode.Full,
        new CompatibilityTestResult(
            CompatibilityTestOutcome.Failed,
            testedAtUtc,
            "The provider could not enumerate desktops.",
            []));

    private static CompatibilityStatus Create(
        DesktopTopologyProviderMode mode,
        CompatibilityTestResult test)
    {
        WindowsBuildInfo build = new(true, 10, 0, 26100, 1, Architecture.X64);

        return new CompatibilityStatus(
            build,
            WindowsBuildClassifier.Classify(build),
            new DesktopTopologyProviderState(
                new DesktopTopologyProviderIdentity(
                    "test-provider",
                    "Test provider",
                    "1.0",
                    mode,
                    UsesPrivateApis: false),
                VirtualDesktopCapabilities.DocumentedLimited,
                DesktopTopologyProviderAvailability.Ready,
                "Only the documented capabilities are available."),
            test);
    }
}

internal static class AssignmentActivities
{
    public static WindowAssignmentActivity Succeeded(DateTimeOffset startedAtUtc) => Create(
        startedAtUtc,
        WindowAssignmentOutcome.Succeeded,
        WindowMoveOutcome.Succeeded,
        error: null);

    public static WindowAssignmentActivity AlreadyCorrect(DateTimeOffset startedAtUtc) => Create(
        startedAtUtc,
        WindowAssignmentOutcome.Skipped,
        WindowMoveOutcome.AlreadyCorrect,
        error: null);

    public static WindowAssignmentActivity Failed(DateTimeOffset startedAtUtc) => Create(
        startedAtUtc,
        WindowAssignmentOutcome.Failed,
        WindowMoveOutcome.Failed,
        new WindowAssignmentError(
            "move-failed",
            "The window could not be moved.",
            HResult: unchecked((int)0x80004005)));

    private static WindowAssignmentActivity Create(
        DateTimeOffset startedAtUtc,
        WindowAssignmentOutcome outcome,
        WindowMoveOutcome moveOutcome,
        WindowAssignmentError? error) =>
        new(
            Guid.NewGuid(),
            startedAtUtc,
            TimeSpan.FromMilliseconds(4),
            WindowEventKind.Created,
            (nint)77,
            outcome,
            WindowAssignmentSkipReason.None,
            "vscode",
            "code",
            Guid.Empty,
            null,
            new WindowSafeIdentity("Code.exe", null, null, "Chrome_WidgetWin_1"),
            error,
            moveOutcome);
}

internal sealed class StubConfigurationService : IConfigurationService
{
    public StubConfigurationService(ConfigurationState state)
    {
        CurrentState = state;
    }

    public ConfigurationState CurrentState { get; private set; }

    public bool AcceptCandidates { get; set; } = true;

    public int SaveCount { get; private set; }

    public Task<ConfigurationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CurrentState);
    }

    public Task<ConfigurationSaveResult> SaveCandidateAsync(
        ConfigurationDocument candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;

        CurrentState = AcceptCandidates
            ? new ConfigurationState(
                candidate,
                candidate,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch)
            : CurrentState with { Candidate = candidate };

        return Task.FromResult(new ConfigurationSaveResult(AcceptCandidates, CurrentState));
    }
}

internal sealed class FakeStartupRegistration : IStartupRegistration
{
    private StartupRegistrationState _state;

    public FakeStartupRegistration(
        StartupRegistrationState initialState = StartupRegistrationState.Disabled)
    {
        _state = initialState;
    }

    public int SetCount { get; private set; }

    public ValueTask<StartupRegistrationState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_state);
    }

    public ValueTask<StartupRegistrationState> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCount++;

        if (!_state.IsLocked())
        {
            _state = isEnabled
                ? StartupRegistrationState.Enabled
                : StartupRegistrationState.Disabled;
        }

        return ValueTask.FromResult(_state);
    }
}
