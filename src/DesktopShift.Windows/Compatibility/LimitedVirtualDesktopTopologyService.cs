using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Compatibility;

public sealed class LimitedVirtualDesktopTopologyService : IDesktopTopologyProvider
{
    private const string UnsupportedCode = "desktop_topology.unsupported_in_limited_mode";
    private const string UnsupportedMessage =
        "The documented Windows virtual-desktop API does not expose this topology operation.";

    public DesktopTopologyProviderIdentity Identity { get; } = new(
        "windows.documented.limited",
        "Windows documented API",
        "1",
        DesktopTopologyProviderMode.Limited,
        UsesPrivateApis: false);

    public VirtualDesktopCapabilities Capabilities =>
        VirtualDesktopCapabilities.DocumentedLimited;

    public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
        WindowsBuildInfo build,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        cancellationToken.ThrowIfCancellationRequested();

        if (!build.IsWindows)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    "platform.not_windows",
                    "The documented Windows virtual-desktop API is unavailable on this platform."));
        }

        if (Capabilities.HasPrivateTopologyCapabilities || Identity.UsesPrivateApis)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    "provider.unsafe_limited_capability",
                    "The Limited Mode provider advertised a private topology capability."));
        }

        return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
        CancellationToken cancellationToken = default) =>
        Unsupported<IReadOnlyList<VirtualDesktopDescriptor>>(cancellationToken);

    public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
        CancellationToken cancellationToken = default) =>
        Unsupported<Guid>(cancellationToken);

    public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
        CancellationToken cancellationToken = default) =>
        Unsupported<Guid>(cancellationToken);

    public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
        Guid desktopId,
        CancellationToken cancellationToken = default) =>
        Unsupported(cancellationToken);

    public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
        CancellationToken cancellationToken = default) =>
        Unsupported(cancellationToken);

    private static ValueTask<DesktopTopologyProviderResult<T>> Unsupported<T>(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            DesktopTopologyProviderResult<T>.Unsupported(UnsupportedCode, UnsupportedMessage));
    }

    private static ValueTask<DesktopTopologyProviderResult> Unsupported(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            DesktopTopologyProviderResult.Unsupported(UnsupportedCode, UnsupportedMessage));
    }
}
