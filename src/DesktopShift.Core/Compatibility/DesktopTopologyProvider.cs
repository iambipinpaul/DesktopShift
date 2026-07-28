namespace DesktopShift.Core.Compatibility;

public enum DesktopTopologyProviderMode
{
    Limited,
    Full,
}

public sealed record DesktopTopologyProviderIdentity(
    string Id,
    string DisplayName,
    string Version,
    DesktopTopologyProviderMode Mode,
    bool UsesPrivateApis);

public enum DesktopTopologyProviderAvailability
{
    NotTested,
    Ready,
    Failed,
}

public sealed record DesktopTopologyProviderState(
    DesktopTopologyProviderIdentity Identity,
    VirtualDesktopCapabilities Capabilities,
    DesktopTopologyProviderAvailability Availability,
    string Explanation);

public enum DesktopTopologyResultOutcome
{
    Succeeded,
    Unsupported,
    Unavailable,
    Failed,
    Cancelled,
}

public sealed record DesktopTopologyProviderError(
    string Code,
    string Message,
    int? HResult = null,
    int? NativeErrorCode = null);

public sealed record DesktopTopologyProviderFallback(
    string RequestedProviderId,
    string Stage,
    DesktopTopologyProviderError Error);

public interface IDesktopTopologyFallbackSource
{
    DesktopTopologyProviderFallback? LastFallback { get; }
}

public sealed record DesktopTopologyProviderResult(
    DesktopTopologyResultOutcome Outcome,
    DesktopTopologyProviderError? Error = null)
{
    public bool IsSuccess => Outcome == DesktopTopologyResultOutcome.Succeeded;

    public static DesktopTopologyProviderResult Succeeded() =>
        new(DesktopTopologyResultOutcome.Succeeded);

    public static DesktopTopologyProviderResult Unsupported(string code, string message) =>
        new(
            DesktopTopologyResultOutcome.Unsupported,
            new DesktopTopologyProviderError(code, message));

    public static DesktopTopologyProviderResult Failed(
        string code,
        string message,
        int? hResult = null,
        int? nativeErrorCode = null) =>
        new(
            DesktopTopologyResultOutcome.Failed,
            new DesktopTopologyProviderError(code, message, hResult, nativeErrorCode));
}

public sealed record DesktopTopologyProviderResult<T>(
    DesktopTopologyResultOutcome Outcome,
    T? Value = default,
    DesktopTopologyProviderError? Error = null)
{
    public bool IsSuccess => Outcome == DesktopTopologyResultOutcome.Succeeded;

    public static DesktopTopologyProviderResult<T> Succeeded(T value) =>
        new(DesktopTopologyResultOutcome.Succeeded, value);

    public static DesktopTopologyProviderResult<T> Unsupported(string code, string message) =>
        new(
            DesktopTopologyResultOutcome.Unsupported,
            default,
            new DesktopTopologyProviderError(code, message));

    public static DesktopTopologyProviderResult<T> Failed(
        string code,
        string message,
        int? hResult = null,
        int? nativeErrorCode = null) =>
        new(
            DesktopTopologyResultOutcome.Failed,
            default,
            new DesktopTopologyProviderError(code, message, hResult, nativeErrorCode));
}

public sealed record VirtualDesktopDescriptor(
    Guid Id,
    string? DisplayName,
    int Position,
    bool IsCurrent);

public sealed class DesktopTopologyChangedEventArgs : EventArgs
{
    public DesktopTopologyChangedEventArgs(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    public string Reason { get; }
}

public interface IDesktopTopologyProvider
{
    DesktopTopologyProviderIdentity Identity { get; }

    VirtualDesktopCapabilities Capabilities { get; }

    event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged;

    ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
        WindowsBuildInfo build,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
        Guid desktopId,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
        CancellationToken cancellationToken = default);
}
