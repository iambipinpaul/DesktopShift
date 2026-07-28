using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DesktopShift.Core.Observation;
using Microsoft.Win32.SafeHandles;

namespace DesktopShift.Windows.Observation;

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

public sealed class WindowsProcessCommandLineReader :
    IProcessCommandLineReader
{
    private const uint QueryLimitedInformation = 0x1000;
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

public sealed class WindowsProcessIdentityResolver : IWindowIdentityResolver
{
    private const uint QueryLimitedInformation = 0x1000;
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
        this.commandLineReader =
            commandLineReader ?? new WindowsProcessCommandLineReader();
        this.options = options ?? new WindowIdentityResolutionOptions();
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

        using SafeProcessHandle process = processApi.OpenProcess(
            QueryLimitedInformation,
            window.ProcessId);
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
                .TryReadAsync(window.ProcessId, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (!IsSameWindowProcess(window))
        {
            return WindowIdentityResolution.Failed(
                WindowIdentityResolutionFailure.StaleWindow);
        }

        WindowIdentity identity = new(
            window.ProcessId,
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
