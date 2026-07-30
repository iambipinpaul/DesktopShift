namespace DesktopShift.Core.Observation;

/// <param name="ProcessId">
/// The process that owns <paramref name="RootWindowHandle"/>. This is what the
/// window still has to belong to for the handle to be the same window it was a
/// moment ago, so it stays the frame's process even when the application lives
/// somewhere else.
/// </param>
/// <param name="ContentProcessId">
/// The process that owns the content inside the window, when that is a
/// different process from the one owning the window itself — a legacy Store
/// app, whose frame belongs to <c>ApplicationFrameHost.exe</c> while the
/// application is its own process. Identity is resolved against this when it is
/// present, so rules see the application rather than its host.
/// </param>
public sealed record QualifiedWindow(
    nint OriginalWindowHandle,
    nint RootWindowHandle,
    uint ProcessId,
    string WindowClass,
    uint? ContentProcessId = null);

public sealed record WindowIdentity(
    uint ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? PackageFamilyName,
    string? AppUserModelId,
    string WindowClass,
    string? WindowTitle,
    string? CommandLine)
{
    public WindowSafeIdentity ToSafeIdentity() => new(
        ProcessName,
        PackageFamilyName,
        AppUserModelId,
        WindowClass);
}

public sealed record WindowSafeIdentity(
    string ProcessName,
    string? PackageFamilyName,
    string? AppUserModelId,
    string WindowClass);

public enum WindowIdentityResolutionFailure
{
    None,
    StaleWindow,
    ProcessExited,
    AccessDenied,
    NativeFailure,
}

public sealed record WindowIdentityResolution(
    WindowIdentity? Identity,
    WindowIdentityResolutionFailure Failure,
    int? NativeErrorCode = null)
{
    public bool IsSuccessful => Identity is not null;

    public static WindowIdentityResolution Succeeded(WindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new WindowIdentityResolution(identity, WindowIdentityResolutionFailure.None);
    }

    public static WindowIdentityResolution Failed(
        WindowIdentityResolutionFailure failure,
        int? nativeErrorCode = null) =>
        new(null, failure, nativeErrorCode);
}

public interface IWindowIdentityResolver
{
    ValueTask<WindowIdentityResolution> ResolveAsync(
        QualifiedWindow window,
        CancellationToken cancellationToken = default);
}
