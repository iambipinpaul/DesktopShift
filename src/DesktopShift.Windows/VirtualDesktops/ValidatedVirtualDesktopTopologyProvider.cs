using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.VirtualDesktops;

public sealed class ValidatedVirtualDesktopTopologyProvider :
    IDesktopTopologyProvider,
    IDesktopTopologyFallbackSource,
    IValidatedWindowDesktopMover,
    IDisposable
{
    private static readonly VirtualDesktopCapabilities FullManagedDesktopCapabilities = new(
        CanGetWindowDesktopId: true,
        CanMoveWindowToDesktop: true,
        CanEnumerateDesktops: true,
        CanGetCurrentDesktop: true,
        CanCreateDesktop: true,
        CanSwitchDesktop: true,
        CanObserveTopologyChanges: true);

    /// <summary>
    /// Full Mode on a build that also proved it lays the shell manager vtable
    /// out where naming and reordering live.
    /// </summary>
    /// <remarks>
    /// This is a strict superset, and the difference is deliberately the only
    /// difference. Failing to prove the extended layout costs naming and
    /// desktop reordering only — the provider still enumerates, creates,
    /// switches and moves windows exactly as before, and remains in Full Mode.
    /// </remarks>
    private static readonly VirtualDesktopCapabilities FullCapabilitiesWithExtendedLayout =
        FullManagedDesktopCapabilities with
        {
            CanRenameDesktop = true,
            CanReorderDesktop = true,
        };

    /// <summary>
    /// The shortest gap between two rebuild attempts.
    /// </summary>
    /// <remarks>
    /// A shell that is genuinely gone fails every call, and every one of those
    /// failures asks to rebuild. Without a floor, a foreground burst would turn
    /// one dead shell into an activation attempt per window event. This bounds
    /// the cost of being wrong to one attempt per interval while still letting
    /// the first failure after a restart rebuild immediately.
    /// </remarks>
    private const long ReconnectCooldownMilliseconds = 5_000;

    private readonly object syncRoot = new();
    private readonly LimitedVirtualDesktopTopologyService limitedProvider;
    private readonly INativeVirtualDesktopBridgeFactory bridgeFactory;
    private INativeVirtualDesktopBridge? activeBridge;
    private DesktopTopologyProviderIdentity identity;
    private VirtualDesktopCapabilities capabilities;
    private DesktopTopologyProviderFallback? lastFallback;
    private WindowsBuildInfo? validatedBuild;
    private long? lastReconnectTicks;
    private int reconnectCount;
    private bool disposed;

    public ValidatedVirtualDesktopTopologyProvider()
        : this(
            new LimitedVirtualDesktopTopologyService(),
            new ShellNativeVirtualDesktopBridgeFactory())
    {
    }

    internal ValidatedVirtualDesktopTopologyProvider(
        LimitedVirtualDesktopTopologyService limitedProvider,
        INativeVirtualDesktopBridgeFactory bridgeFactory)
    {
        this.limitedProvider =
            limitedProvider ?? throw new ArgumentNullException(nameof(limitedProvider));
        this.bridgeFactory =
            bridgeFactory ?? throw new ArgumentNullException(nameof(bridgeFactory));
        identity = limitedProvider.Identity;
        capabilities = limitedProvider.Capabilities;
    }

    public DesktopTopologyProviderIdentity Identity
    {
        get
        {
            lock (syncRoot)
            {
                return identity;
            }
        }
    }

    public VirtualDesktopCapabilities Capabilities
    {
        get
        {
            lock (syncRoot)
            {
                return capabilities;
            }
        }
    }

    public DesktopTopologyProviderFallback? LastFallback
    {
        get
        {
            lock (syncRoot)
            {
                return lastFallback;
            }
        }
    }

    /// <summary>
    /// How many times a dead shell adapter was rebuilt in place rather than
    /// demoted to Limited Mode.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can prove that a transport failure was answered by one
    /// rebuild rather than by a silent retry loop, and so the cooldown is
    /// observable rather than merely asserted about.
    /// </remarks>
    internal int ReconnectCount
    {
        get
        {
            lock (syncRoot)
            {
                return reconnectCount;
            }
        }
    }

    public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged;

    public async ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
        WindowsBuildInfo build,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);

        DesktopTopologyProviderResult limitedResult =
            await limitedProvider.TestCompatibilityAsync(build, cancellationToken)
                .ConfigureAwait(false);
        if (!limitedResult.IsSuccess)
        {
            ActivateFallback(
                "documented-provider-check",
                limitedResult.Error ??
                new DesktopTopologyProviderError(
                    "provider.documented_check_failed",
                    "The documented Windows provider compatibility check failed."));
            return limitedResult;
        }

        string requestedProviderId = GetRequestedProviderId(build.Build);
        NativeBridgeResult<INativeVirtualDesktopBridge> activation;
        try
        {
            activation = bridgeFactory.TryCreate(build.Build);
        }
        catch (Exception exception)
        {
            activation = NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                new NativeBridgeError(
                    "native.activation_exception",
                    "NativeBridgeActivation",
                    exception.Message,
                    exception.HResult));
        }

        if (!activation.IsSuccess)
        {
            ActivateFallback(
                requestedProviderId,
                ToProviderError(
                    activation.Error,
                    "native.activation_failed",
                    "The native virtual-desktop adapter could not be activated."),
                activation.Error?.Stage);
            return DesktopTopologyProviderResult.Succeeded();
        }

        INativeVirtualDesktopBridge bridge = activation.Value!;
        NativeBridgeResult validation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            validation = bridge.Validate();
        }
        catch (OperationCanceledException)
        {
            bridge.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            validation = NativeBridgeResult.Failed(
                new NativeBridgeError(
                    "native.validation_exception",
                    "BehaviorValidation",
                    exception.Message,
                    exception.HResult));
        }

        if (!validation.IsSuccess)
        {
            bridge.Dispose();
            ActivateFallback(
                requestedProviderId,
                ToProviderError(
                    validation.Error,
                    "native.validation_failed",
                    "The native virtual-desktop adapter did not pass harmless validation."),
                validation.Error?.Stage);
            return DesktopTopologyProviderResult.Succeeded();
        }

        // Naming and reordering need manager slots past the validated prefix,
        // so they are not inferred from Full Mode — they are asked for and
        // answered together. The bridge
        // refuses outright on a build family that is not cleared for those
        // slots, so an unrecognized build never reaches them, and the probe
        // itself only ever performs a read-only lookup.
        //
        // A refusal here is not a fallback. It costs naming and desktop
        // reordering while leaving every other Full Mode capability standing.
        bool canUseExtendedLayout;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            canUseExtendedLayout = bridge.ProbeDesktopLookup().IsSuccess;
        }
        catch (OperationCanceledException)
        {
            bridge.Dispose();
            throw;
        }
        catch (Exception)
        {
            canUseExtendedLayout = false;
        }

        lock (syncRoot)
        {
            if (disposed)
            {
                bridge.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }
            activeBridge?.Dispose();
            activeBridge = bridge;
            identity = CreateFullIdentity(build.Build);
            capabilities = canUseExtendedLayout
                ? FullCapabilitiesWithExtendedLayout
                : FullManagedDesktopCapabilities;
            lastFallback = null;

            // Remembered so a later rebuild targets the family this machine
            // actually validated, and the cooldown is cleared so the first
            // failure after a fresh activation can rebuild without waiting.
            validatedBuild = build;
            lastReconnectTicks = null;
        }

        DesktopTopologyProviderResult notificationResult =
            await StartTopologyNotificationsAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!notificationResult.IsSuccess)
        {
            // Notification readiness is part of Full Mode. The registration
            // method has already disposed the native bridge and activated the
            // documented Limited provider, which remains a successful safe mode.
            return DesktopTopologyProviderResult.Succeeded();
        }

        return DesktopTopologyProviderResult.Succeeded();
    }

    public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return limitedProvider.EnumerateDesktopsAsync(cancellationToken);
        }

        try
        {
            NativeBridgeResult<NativeDesktopSnapshot> result = InvokeWithReconnect(
                bridge,
                static live => live.ReadSnapshot());
            if (!result.IsSuccess)
            {
                DesktopTopologyProviderError error = ToProviderError(
                    result.Error,
                    "native.enumeration_failed",
                    "The validated desktop inventory could not be read.");
                ActivateFallback(
                    Identity.Id,
                    error,
                    result.Error?.Stage,
                    raiseEvent: true);
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Failed(
                        error.Code,
                        error.Message,
                        error.HResult,
                        error.NativeErrorCode));
            }

            NativeDesktopSnapshot snapshot = result.Value!;
            ImmutableArray<VirtualDesktopDescriptor> desktops = snapshot.Desktops
                .OrderBy(static desktop => desktop.Position)
                .Select(
                    desktop => new VirtualDesktopDescriptor(
                        desktop.Id,
                        desktop.DisplayName,
                        desktop.Position,
                        desktop.Id == snapshot.CurrentDesktopId))
                .ToImmutableArray();

            return ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                    desktops));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(
                FailOperation<IReadOnlyList<VirtualDesktopDescriptor>>(
                    "native.enumeration_exception",
                    "Native desktop enumeration failed unexpectedly.",
                    exception));
        }
    }

    public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return limitedProvider.GetCurrentDesktopIdAsync(cancellationToken);
        }

        try
        {
            NativeBridgeResult<NativeDesktopSnapshot> result = InvokeWithReconnect(
                bridge,
                static live => live.ReadSnapshot());
            if (result.IsSuccess)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult<Guid>.Succeeded(
                        result.Value!.CurrentDesktopId));
            }

            DesktopTopologyProviderError error = ToProviderError(
                result.Error,
                "native.current_desktop_failed",
                "The current desktop could not be read.");
            ActivateFallback(
                Identity.Id,
                error,
                result.Error?.Stage,
                raiseEvent: true);
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    error.Code,
                    error.Message,
                    error.HResult,
                    error.NativeErrorCode));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(
                FailOperation<Guid>(
                    "native.current_desktop_exception",
                    "The current desktop query failed unexpectedly.",
                    exception));
        }
    }

    public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return limitedProvider.CreateDesktopAsync(cancellationToken);
        }

        try
        {
            NativeBridgeResult<Guid> result = InvokeWithReconnect(
                bridge,
                static live => live.CreateDesktop());
            if (result.IsSuccess)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult<Guid>.Succeeded(result.Value));
            }

            DesktopTopologyProviderError error = ToProviderError(
                result.Error,
                "native.desktop_creation_failed",
                "The validated adapter could not create a virtual desktop.");
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    error.Code,
                    error.Message,
                    error.HResult,
                    error.NativeErrorCode));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    "native.creation_exception",
                    "Virtual desktop creation failed unexpectedly.",
                    exception.HResult));
        }
    }

    public ValueTask<DesktopTopologyProviderResult> RenameDesktopAsync(
        Guid desktopId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        cancellationToken.ThrowIfCancellationRequested();

        // Asked and answered at activation. Reading the flag rather than
        // trying and seeing keeps the call off a build whose layout was never
        // established, which is the whole point of establishing it.
        if (!Capabilities.CanRenameDesktop)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Unsupported(
                    "native.rename_unsupported",
                    "This Windows build did not prove it lays the shell manager out where desktop naming lives, so naming is unavailable."));
        }

        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Unsupported(
                    "native.rename_unsupported",
                    "The validated adapter is no longer active, so desktop naming is unavailable."));
        }

        try
        {
            NativeBridgeResult result = bridge.SetDesktopName(desktopId, displayName);
            if (result.IsSuccess)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Succeeded());
            }

            // A refused name is an operation failure, not a broken adapter, so
            // this deliberately does not activate the fallback the way a failed
            // enumeration does. Naming is cosmetic; losing it must never cost
            // the caller its bindings.
            DesktopTopologyProviderError error = ToProviderError(
                result.Error,
                "native.desktop_rename_failed",
                "The validated adapter could not name the virtual desktop.");
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    error.Code,
                    error.Message,
                    error.HResult,
                    error.NativeErrorCode));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    "native.rename_exception",
                    "Virtual desktop naming failed unexpectedly.",
                    exception.HResult));
        }
    }

    public ValueTask<DesktopTopologyProviderResult> MoveDesktopAsync(
        Guid desktopId,
        int targetPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetPosition);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Capabilities.CanReorderDesktop)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Unsupported(
                    "native.reorder_unsupported",
                    "This Windows build did not prove the extended shell-manager layout, so Task View reordering is unavailable."));
        }

        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Unsupported(
                    "native.reorder_unsupported",
                    "The validated adapter is no longer active, so Task View reordering is unavailable."));
        }

        try
        {
            NativeBridgeResult result = bridge.MoveDesktop(desktopId, targetPosition);
            if (result.IsSuccess)
            {
                return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
            }

            DesktopTopologyProviderError error = ToProviderError(
                result.Error,
                "native.desktop_reorder_failed",
                "The validated adapter could not reorder the virtual desktop.");
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    error.Code,
                    error.Message,
                    error.HResult,
                    error.NativeErrorCode));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    "native.reorder_exception",
                    "Virtual desktop reordering failed unexpectedly.",
                    exception.HResult));
        }
    }

    public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
        Guid desktopId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return limitedProvider.SwitchDesktopAsync(
                desktopId,
                cancellationToken);
        }

        try
        {
            NativeBridgeResult result = InvokeWithReconnect(
                bridge,
                live => live.SwitchDesktop(desktopId));
            if (result.IsSuccess)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Succeeded());
            }

            DesktopTopologyProviderError error = ToProviderError(
                result.Error,
                "native.desktop_switch_failed",
                "The validated adapter could not switch virtual desktops.");
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    error.Code,
                    error.Message,
                    error.HResult,
                    error.NativeErrorCode));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    "native.switch_exception",
                    "Virtual desktop switching failed unexpectedly.",
                    exception.HResult));
        }
    }

    public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        INativeVirtualDesktopBridge? bridge = GetActiveBridge();
        if (bridge is null)
        {
            return limitedProvider.StartTopologyNotificationsAsync(cancellationToken);
        }

        try
        {
            NativeBridgeResult result = bridge.StartNotifications(OnNativeTopologyChanged);
            if (result.IsSuccess)
            {
                return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
            }

            // Registration is the one operation a rebuild performs itself, so
            // this site reconnects without retrying: a rebuild that returned an
            // adapter has already registered on it, and asking again would
            // register twice against the same shell.
            if (IsShellTransportFailure(result.Error) && TryReconnect() is not null)
            {
                return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
            }

            DesktopTopologyProviderError error = ToProviderError(
                result.Error,
                "native.notification_registration_failed",
                "Topology notification registration failed.");
            ActivateFallback(
                Identity.Id,
                error,
                result.Error?.Stage,
                raiseEvent: true);
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    error.Code,
                    error.Message,
                    error.HResult,
                    error.NativeErrorCode));
        }
        catch (Exception exception)
        {
            DesktopTopologyProviderError error = new(
                "native.notification_exception",
                "Topology notification registration failed unexpectedly.",
                exception.HResult);
            ActivateFallback(Identity.Id, error, raiseEvent: true);
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    error.Code,
                    error.Message,
                    error.HResult));
        }
    }

    NativeBridgeResult IValidatedWindowDesktopMover.MoveWindowToDesktop(
        nint windowHandle,
        Guid desktopId)
    {
        lock (syncRoot)
        {
            ThrowIfDisposed();
            if (activeBridge is null)
            {
                return NativeBridgeResult.Failed(
                    new NativeBridgeError(
                        "native.window_move_unavailable",
                        "ProviderSelection",
                        "Window moves require a validated native virtual-desktop adapter, but DesktopShift is currently in Limited Mode.",
                        unchecked((int)0x80070032)));
            }

            try
            {
                // Adapter replacement and disposal use this same lock. Hold it
                // through the single native call so recovery cannot invalidate
                // the bridge after selection but before the move completes.
                // A refusal for one HWND remains an operation failure and does
                // not demote the validated provider.
                return activeBridge.MoveWindowToDesktop(
                    windowHandle,
                    desktopId);
            }
            catch (Exception exception)
            {
                return NativeBridgeResult.Failed(
                    new NativeBridgeError(
                        "native.window_move_exception",
                        "WindowMove",
                        "The native window move failed unexpectedly.",
                        exception.HResult));
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeBridge?.Dispose();
            activeBridge = null;
        }
    }

    private static DesktopTopologyProviderIdentity CreateFullIdentity(int build) =>
        new(
            GetRequestedProviderId(build),
            build switch
            {
                26100 => "Windows Shell adapter (24H2)",
                26200 => "Windows Shell adapter (25H2)",
                _ => "Windows Shell adapter",
            },
            "shell-abi-24h2-v1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: true);

    private static string GetRequestedProviderId(int build) =>
        build switch
        {
            22631 => "windows.shell.23h2.22631",
            26100 => "windows.shell.24h2.26100",
            26200 => "windows.shell.25h2.26200",
            28000 => "windows.shell.26h1.28000",
            _ => $"windows.shell.unrecognized.{build}",
        };

    private static DesktopTopologyProviderError ToProviderError(
        NativeBridgeError? error,
        string fallbackCode,
        string fallbackMessage) =>
        error is null
            ? new DesktopTopologyProviderError(fallbackCode, fallbackMessage)
            : new DesktopTopologyProviderError(
                error.Code,
                error.Message,
                error.HResult);

    private INativeVirtualDesktopBridge? GetActiveBridge()
    {
        lock (syncRoot)
        {
            ThrowIfDisposed();
            return activeBridge;
        }
    }

    /// <summary>
    /// Whether a failure means "the shell process behind this adapter is gone"
    /// rather than "this shell cannot do virtual desktops".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction is the whole reason this type has two answers to a failed
    /// native call. Limited Mode is the honest response to a Windows build whose
    /// shell will never satisfy the contract — it is a property of the machine,
    /// so it is worth latching. A dead COM proxy is the opposite: the machine is
    /// fine and the object is stale, which happens every single time Explorer
    /// restarts or is killed. Latching Limited Mode on one of those meant a
    /// transient shell hiccup silently disabled assignment until the user
    /// restarted the app, with the only visible symptom being every later target
    /// reported as unresolved.
    /// </para>
    /// <para>
    /// These are the RPC and COM codes for a server that went away mid-call or
    /// between calls. Anything outside this set is treated as a real refusal and
    /// still demotes, so widening the set is the only way to make a genuine
    /// incompatibility look recoverable.
    /// </para>
    /// </remarks>
    private static bool IsShellTransportFailure(NativeBridgeError? error) =>
        error is not null &&
        (uint)error.HResult is
            0x800706BA or // RPC_S_SERVER_UNAVAILABLE
            0x800706BE or // RPC_S_CALL_FAILED
            0x800706BF or // RPC_S_CALL_FAILED_DNE
            0x80010007 or // RPC_E_SERVER_DIED
            0x80010012 or // RPC_E_SERVER_DIED_DNE
            0x80010108 or // RPC_E_DISCONNECTED
            0x800401FD;   // CO_E_OBJNOTCONNECTED

    /// <summary>
    /// Runs one native operation and, when it fails the way a departed shell
    /// fails, rebuilds the adapter and runs it exactly once more.
    /// </summary>
    /// <remarks>
    /// Once more, not until it works. A second transport failure against a
    /// freshly validated adapter is no longer a stale proxy, so it is reported
    /// and demotes like any other failure.
    /// </remarks>
    private NativeBridgeResult<T> InvokeWithReconnect<T>(
        INativeVirtualDesktopBridge bridge,
        Func<INativeVirtualDesktopBridge, NativeBridgeResult<T>> operation)
    {
        NativeBridgeResult<T> result = operation(bridge);
        if (result.IsSuccess || !IsShellTransportFailure(result.Error))
        {
            return result;
        }

        INativeVirtualDesktopBridge? reconnected = TryReconnect();
        return reconnected is null ? result : operation(reconnected);
    }

    /// <inheritdoc cref="InvokeWithReconnect{T}"/>
    private NativeBridgeResult InvokeWithReconnect(
        INativeVirtualDesktopBridge bridge,
        Func<INativeVirtualDesktopBridge, NativeBridgeResult> operation)
    {
        NativeBridgeResult result = operation(bridge);
        if (result.IsSuccess || !IsShellTransportFailure(result.Error))
        {
            return result;
        }

        INativeVirtualDesktopBridge? reconnected = TryReconnect();
        return reconnected is null ? result : operation(reconnected);
    }

    /// <summary>
    /// Rebuilds the native adapter against the build this provider already
    /// validated, and returns the live adapter, or null if Full Mode could not
    /// be re-established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This proves exactly what activation proves, in the same order: a bridge
    /// that validates, and notifications that register. A rebuild that skipped
    /// either would be a route into Full Mode that never established Full Mode.
    /// The extended layout is asked for separately; a refusal costs naming and
    /// desktop reordering only, which is the same bargain activation makes.
    /// </para>
    /// <para>
    /// Activation happens outside the lock because it is COM work against a
    /// shell that may still be starting up, and the swap happens inside it.
    /// Returning the new adapter rather than a bool is what lets the caller
    /// retry without a second lock acquisition and without racing a disposal
    /// between the two.
    /// </para>
    /// </remarks>
    private INativeVirtualDesktopBridge? TryReconnect()
    {
        int build;
        lock (syncRoot)
        {
            // Never in Full Mode means there is nothing to restore: an adapter
            // this provider never validated is not one it may activate here.
            if (disposed || validatedBuild is null)
            {
                return null;
            }

            if (lastReconnectTicks is long previous &&
                Environment.TickCount64 - previous < ReconnectCooldownMilliseconds)
            {
                return null;
            }

            lastReconnectTicks = Environment.TickCount64;
            build = validatedBuild.Build;
        }

        NativeBridgeResult<INativeVirtualDesktopBridge> activation;
        try
        {
            activation = bridgeFactory.TryCreate(build);
        }
        catch (Exception)
        {
            return null;
        }

        if (!activation.IsSuccess)
        {
            return null;
        }

        INativeVirtualDesktopBridge bridge = activation.Value!;
        bool canUseExtendedLayout;
        try
        {
            if (!bridge.Validate().IsSuccess ||
                !bridge.StartNotifications(OnNativeTopologyChanged).IsSuccess)
            {
                bridge.Dispose();
                return null;
            }

            canUseExtendedLayout = bridge.ProbeDesktopLookup().IsSuccess;
        }
        catch (Exception)
        {
            bridge.Dispose();
            return null;
        }

        lock (syncRoot)
        {
            if (disposed)
            {
                bridge.Dispose();
                return null;
            }

            activeBridge?.Dispose();
            activeBridge = bridge;
            identity = CreateFullIdentity(build);
            capabilities = canUseExtendedLayout
                ? FullCapabilitiesWithExtendedLayout
                : FullManagedDesktopCapabilities;

            // The fallback record is cleared because it is no longer true. A
            // stale one would keep reporting Limited Mode in compatibility
            // diagnostics for a provider that is demonstrably running Full.
            lastFallback = null;
            reconnectCount++;
        }

        // Raised outside the lock, and raised at all because every desktop id
        // held by a caller was handed out by the adapter that just died. The
        // listeners that reconcile bindings need to know to look again.
        TopologyChanged?.Invoke(
            this,
            new DesktopTopologyChangedEventArgs("ProviderReconnected"));
        return bridge;
    }

    private void ActivateFallback(
        string requestedProviderId,
        DesktopTopologyProviderError error,
        string? stage = null,
        bool raiseEvent = false)
    {
        lock (syncRoot)
        {
            activeBridge?.Dispose();
            activeBridge = null;
            identity = limitedProvider.Identity;
            capabilities = limitedProvider.Capabilities;
            lastFallback = new DesktopTopologyProviderFallback(
                requestedProviderId,
                stage ?? GetFallbackStage(error.Code),
                error);
        }

        if (raiseEvent)
        {
            TopologyChanged?.Invoke(
                this,
                new DesktopTopologyChangedEventArgs("ProviderFallbackActivated"));
        }
    }

    private DesktopTopologyProviderResult<T> FailOperation<T>(
        string code,
        string message,
        Exception exception)
    {
        DesktopTopologyProviderError error = new(code, message, exception.HResult);
        ActivateFallback(Identity.Id, error, raiseEvent: true);
        return DesktopTopologyProviderResult<T>.Failed(
            error.Code,
            error.Message,
            error.HResult);
    }

    private void OnNativeTopologyChanged(string reason) =>
        TopologyChanged?.Invoke(this, new DesktopTopologyChangedEventArgs(reason));

    private static string GetFallbackStage(string code)
    {
        int separator = code.LastIndexOf('.');
        return separator >= 0 && separator < code.Length - 1
            ? code[(separator + 1)..]
            : code;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}

/// <summary>
/// Resolves each move against the provider's currently validated native
/// adapter.
/// </summary>
/// <remarks>
/// Shell recovery replaces the adapter owned by the provider. Callers retain
/// this seam rather than an adapter instance, so they cannot keep using the
/// stale bridge that recovery discarded.
/// </remarks>
internal interface IValidatedWindowDesktopMover
{
    NativeBridgeResult MoveWindowToDesktop(nint windowHandle, Guid desktopId);
}
