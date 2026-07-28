namespace DesktopShift.Core.Observation;

public sealed record QualifiedWindow(
    nint OriginalWindowHandle,
    nint RootWindowHandle,
    uint ProcessId,
    string WindowClass);

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
