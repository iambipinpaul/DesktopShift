using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.VirtualDesktops;

public sealed class ValidatedVirtualDesktopTopologyProvider :
    IDesktopTopologyProvider,
    IDesktopTopologyFallbackSource,
    IDisposable
{
    private const string UnsupportedSwitchCode = "desktop_topology.switch_not_implemented";
    private const string UnsupportedSwitchMessage =
        "This Full Mode adapter does not enable desktop switching.";

    private static readonly VirtualDesktopCapabilities FullManagedDesktopCapabilities = new(
        CanGetWindowDesktopId: true,
        CanMoveWindowToDesktop: true,
        CanEnumerateDesktops: true,
        CanGetCurrentDesktop: true,
        CanCreateDesktop: true,
        CanSwitchDesktop: false,
        CanObserveTopologyChanges: true);

    private readonly object syncRoot = new();
    private readonly LimitedVirtualDesktopTopologyService limitedProvider;
    private readonly INativeVirtualDesktopBridgeFactory bridgeFactory;
    private INativeVirtualDesktopBridge? activeBridge;
    private DesktopTopologyProviderIdentity identity;
    private VirtualDesktopCapabilities capabilities;
    private DesktopTopologyProviderFallback? lastFallback;
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
            capabilities = FullManagedDesktopCapabilities;
            lastFallback = null;
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
            NativeBridgeResult<NativeDesktopSnapshot> result = bridge.ReadSnapshot();
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
            NativeBridgeResult<NativeDesktopSnapshot> result = bridge.ReadSnapshot();
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
            NativeBridgeResult<Guid> result = bridge.CreateDesktop();
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

    public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
        Guid desktopId,
        CancellationToken cancellationToken = default)
    {
        _ = desktopId;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            DesktopTopologyProviderResult.Unsupported(
                UnsupportedSwitchCode,
                UnsupportedSwitchMessage));
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
