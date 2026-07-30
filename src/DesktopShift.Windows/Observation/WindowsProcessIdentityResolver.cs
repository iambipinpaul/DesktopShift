using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DesktopShift.Core.Observation;
using Microsoft.Win32.SafeHandles;

namespace DesktopShift.Windows.Observation;

/// <summary>
/// The only process access right DesktopShift ever asks Windows for.
/// </summary>
/// <remarks>
/// <para>
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is the least right that satisfies
/// every question DesktopShift asks about another process: its image file name,
/// its package family name, and its application user model id. It is granted
/// across integrity levels far more often than the wider query right, which is
/// why an unelevated DesktopShift can classify most windows at all.
/// </para>
/// <para>
/// It is deliberately a single named constant so that widening it — to
/// <c>PROCESS_QUERY_INFORMATION</c>, to any memory right, or to full access —
/// is a visible edit in one place rather than a changed literal at a call site.
/// </para>
/// </remarks>
public static class ProcessAccessRights
{
    /// <summary>
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.
    /// </summary>
    public const uint QueryLimitedInformation = 0x1000;
}

public sealed record WindowIdentityResolutionOptions(
    bool IncludeWindowTitle = false,
    bool IncludeCommandLine = false);

public interface IProcessCommandLineReader
{
    ValueTask<string?> TryReadAsync(
        uint processId,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableProcessCommandLineReader :
    IProcessCommandLineReader
{
    public ValueTask<string?> TryReadAsync(
        uint processId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(null);
}

public interface IWindowsCommandLineApi
{
    SafeProcessHandle OpenProcess(uint desiredAccess, uint processId);

    int QueryCommandLine(
        SafeProcessHandle process,
        nint buffer,
        uint bufferLength,
        out uint requiredLength);
}

/// <summary>
/// Reads a process's command line, and is used only when a caller explicitly
/// asks for one.
/// </summary>
/// <remarks>
/// A command line can carry a file path, a URL, or a credential, so it is never
/// part of the identity DesktopShift matches rules on and has no member on
/// <see cref="WindowSafeIdentity"/> to travel in. The default resolver does not
/// construct this reader at all; see
/// <see cref="WindowsProcessIdentityResolver"/>.
/// </remarks>
public sealed class WindowsProcessCommandLineReader :
    IProcessCommandLineReader
{
    private const uint QueryLimitedInformation =
        ProcessAccessRights.QueryLimitedInformation;
    private readonly IWindowsCommandLineApi nativeApi;

    public WindowsProcessCommandLineReader(
        IWindowsCommandLineApi? nativeApi = null)
    {
        this.nativeApi = nativeApi ?? new WindowsCommandLineApi();
    }

    public ValueTask<string?> TryReadAsync(
        uint processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SafeProcessHandle process = nativeApi.OpenProcess(
            QueryLimitedInformation,
            processId);
        if (process.IsInvalid)
        {
            return ValueTask.FromResult<string?>(null);
        }

        nativeApi.QueryCommandLine(
            process,
            0,
            0,
            out uint requiredLength);
        if (requiredLength == 0 || requiredLength > 1024 * 1024)
        {
            return ValueTask.FromResult<string?>(null);
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            int status = nativeApi.QueryCommandLine(
                process,
                buffer,
                requiredLength,
                out _);
            if (status < 0)
            {
                return ValueTask.FromResult<string?>(null);
            }

            NativeUnicodeString commandLine =
                Marshal.PtrToStructure<NativeUnicodeString>(buffer);
            if (commandLine.Length == 0 || commandLine.Buffer == 0)
            {
                return ValueTask.FromResult<string?>(string.Empty);
            }

            string? value = Marshal.PtrToStringUni(
                commandLine.Buffer,
                commandLine.Length / sizeof(char));
            return ValueTask.FromResult<string?>(value);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NativeUnicodeString
    {
        public NativeUnicodeString(
            ushort length,
            ushort maximumLength,
            nint buffer)
        {
            Length = length;
            MaximumLength = maximumLength;
            Buffer = buffer;
        }

        public ushort Length { get; }

        public ushort MaximumLength { get; }

        public nint Buffer { get; }
    }
}

public sealed class WindowsCommandLineApi : IWindowsCommandLineApi
{
    private const int ProcessCommandLineInformation = 60;

    public SafeProcessHandle OpenProcess(uint desiredAccess, uint processId) =>
        NativeMethods.OpenProcess(
            desiredAccess,
            inheritHandle: false,
            processId);

    public int QueryCommandLine(
        SafeProcessHandle process,
        nint buffer,
        uint bufferLength,
        out uint requiredLength) =>
        NativeMethods.NtQueryInformationProcess(
            process,
            ProcessCommandLineInformation,
            buffer,
            bufferLength,
            out requiredLength);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            SafeProcessHandle process,
            int processInformationClass,
            nint processInformation,
            uint processInformationLength,
            out uint returnLength);
    }
}

/// <summary>
/// Resolves the identity DesktopShift matches rules on, using the least process
/// access right that can answer the question.
/// </summary>
/// <remarks>
/// <para>
/// One handle is opened per window event, for
/// <see cref="ProcessAccessRights.QueryLimitedInformation"/> only, and is closed
/// before the method returns. No memory is read, no thread is touched, and no
/// token is opened.
/// </para>
/// <para>
/// A process running at a higher integrity level refuses even that handle.
/// That is reported as
/// <see cref="WindowIdentityResolutionFailure.AccessDenied"/> with the Windows
/// error code intact, so the pipeline can skip the window and say why, rather
/// than reaching for a wider right that would still be refused.
/// </para>
/// </remarks>
public sealed class WindowsProcessIdentityResolver : IWindowIdentityResolver
{
    private const uint QueryLimitedInformation =
        ProcessAccessRights.QueryLimitedInformation;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    private readonly IWindowsWindowNativeApi windowApi;
    private readonly IWindowsProcessIdentityApi processApi;
    private readonly IProcessCommandLineReader commandLineReader;
    private readonly WindowIdentityResolutionOptions options;

    public WindowsProcessIdentityResolver(
        IWindowsWindowNativeApi? windowApi = null,
        IWindowsProcessIdentityApi? processApi = null,
        IProcessCommandLineReader? commandLineReader = null,
        WindowIdentityResolutionOptions? options = null)
    {
        this.windowApi = windowApi ?? new WindowsWindowNativeApi();
        this.processApi = processApi ?? new WindowsProcessIdentityApi();
        this.options = options ?? new WindowIdentityResolutionOptions();

        // The shipping configuration asks for no command line, so the reader
        // that would query one is not even constructed. A capability that is
        // never built cannot be reached by a later accident.
        this.commandLineReader =
            commandLineReader ??
            (this.options.IncludeCommandLine
                ? new WindowsProcessCommandLineReader()
                : new UnavailableProcessCommandLineReader());
    }

    public async ValueTask<WindowIdentityResolution> ResolveAsync(
        QualifiedWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSameWindowProcess(window))
        {
            return WindowIdentityResolution.Failed(
                WindowIdentityResolutionFailure.StaleWindow);
        }

        // Everything the identity describes is read from the process the
        // application actually runs in. For almost every window that is the
        // process owning the window; for a legacy Store app it is the process
        // whose content the frame is displaying, and asking the frame instead
        // would describe ApplicationFrameHost.exe for every such app alike.
        uint identityProcessId = window.ContentProcessId ?? window.ProcessId;

        using SafeProcessHandle process = processApi.OpenProcess(
            QueryLimitedInformation,
            identityProcessId);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return WindowIdentityResolution.Failed(
                error == ErrorAccessDenied
                    ? WindowIdentityResolutionFailure.AccessDenied
                    : error == ErrorInvalidParameter
                        ? WindowIdentityResolutionFailure.ProcessExited
                        : WindowIdentityResolutionFailure.NativeFailure,
                error);
        }

        string? executablePath = processApi.TryGetExecutablePath(process);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            int error = Marshal.GetLastWin32Error();
            return WindowIdentityResolution.Failed(
                error == ErrorAccessDenied
                    ? WindowIdentityResolutionFailure.AccessDenied
                    : WindowIdentityResolutionFailure.ProcessExited,
                error);
        }

        string? commandLine = options.IncludeCommandLine
            ? await commandLineReader
                .TryReadAsync(identityProcessId, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (!IsSameWindowProcess(window))
        {
            return WindowIdentityResolution.Failed(
                WindowIdentityResolutionFailure.StaleWindow);
        }

        WindowIdentity identity = new(
            identityProcessId,
            Path.GetFileName(executablePath),
            executablePath,
            processApi.TryGetPackageFamilyName(process),
            processApi.TryGetAppUserModelId(process),
            window.WindowClass,
            options.IncludeWindowTitle
                ? windowApi.GetWindowTitle(window.RootWindowHandle)
                : null,
            commandLine);
        return WindowIdentityResolution.Succeeded(identity);
    }

    private bool IsSameWindowProcess(QualifiedWindow window) =>
        windowApi.IsWindow(window.RootWindowHandle) &&
        windowApi.GetWindowProcessId(window.RootWindowHandle) ==
            window.ProcessId;
}

public interface IWindowsProcessIdentityApi
{
    SafeProcessHandle OpenProcess(uint desiredAccess, uint processId);

    string? TryGetExecutablePath(SafeProcessHandle process);

    string? TryGetPackageFamilyName(SafeProcessHandle process);

    string? TryGetAppUserModelId(SafeProcessHandle process);
}

public sealed class WindowsProcessIdentityApi : IWindowsProcessIdentityApi
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public SafeProcessHandle OpenProcess(uint desiredAccess, uint processId) =>
        NativeMethods.OpenProcess(
            desiredAccess,
            inheritHandle: false,
            processId);

    public string? TryGetExecutablePath(SafeProcessHandle process)
    {
        uint capacity = 1024;
        StringBuilder buffer = new((int)capacity);
        return NativeMethods.QueryFullProcessImageName(
            process,
            flags: 0,
            buffer,
            ref capacity)
                ? buffer.ToString()
                : null;
    }

    public string? TryGetPackageFamilyName(SafeProcessHandle process) =>
        TryReadProcessString(
            (ref uint length, char[]? buffer) =>
                NativeMethods.GetPackageFamilyName(process, ref length, buffer));

    public string? TryGetAppUserModelId(SafeProcessHandle process) =>
        TryReadProcessString(
            (ref uint length, char[]? buffer) =>
                NativeMethods.GetApplicationUserModelId(
                    process,
                    ref length,
                    buffer));

    private static string? TryReadProcessString(ProcessStringReader reader)
    {
        uint length = 0;
        int firstResult = reader(ref length, null);
        if (firstResult == AppModelErrorNoPackage ||
            (firstResult != ErrorInsufficientBuffer && firstResult != 0) ||
            length == 0)
        {
            return null;
        }

        char[] buffer = new char[length];
        int result = reader(ref length, buffer);
        return result == 0
            ? new string(buffer, 0, Math.Max(0, (int)length - 1))
            : null;
    }

    private delegate int ProcessStringReader(
        ref uint length,
        char[]? buffer);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "QueryFullProcessImageNameW",
            SetLastError = true,
            CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            SafeProcessHandle process,
            uint flags,
            StringBuilder executableName,
            ref uint size);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetPackageFamilyName",
            CharSet = CharSet.Unicode)]
        internal static extern int GetPackageFamilyName(
            SafeProcessHandle process,
            ref uint packageFamilyNameLength,
            [Out] char[]? packageFamilyName);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetApplicationUserModelId",
            CharSet = CharSet.Unicode)]
        internal static extern int GetApplicationUserModelId(
            SafeProcessHandle process,
            ref uint applicationUserModelIdLength,
            [Out] char[]? applicationUserModelId);
    }
}
