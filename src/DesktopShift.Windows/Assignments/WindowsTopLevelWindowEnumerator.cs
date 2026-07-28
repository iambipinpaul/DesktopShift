using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopShift.Core.Assignments;

namespace DesktopShift.Windows.Assignments;

public sealed class WindowsTopLevelWindowEnumerator :
    ITopLevelWindowEnumerator
{
    private readonly IEnumWindowsApi nativeApi;

    public WindowsTopLevelWindowEnumerator()
        : this(new EnumWindowsApi())
    {
    }

    internal WindowsTopLevelWindowEnumerator(IEnumWindowsApi nativeApi)
    {
        this.nativeApi =
            nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    public IReadOnlyList<nint> Enumerate()
    {
        List<nint> windows = [];
        EnumWindowsResult result = nativeApi.Enumerate(windows.Add);
        if (!result.Succeeded)
        {
            throw new Win32Exception(
                result.ErrorCode,
                "EnumWindows failed while collecting top-level windows.");
        }

        return windows;
    }
}

internal sealed record EnumWindowsResult(
    bool Succeeded,
    int ErrorCode = 0);

internal interface IEnumWindowsApi
{
    EnumWindowsResult Enumerate(Action<nint> onWindow);
}

internal sealed class EnumWindowsApi : IEnumWindowsApi
{
    private const int UnhandledExceptionError = 574;

    public EnumWindowsResult Enumerate(Action<nint> onWindow)
    {
        ArgumentNullException.ThrowIfNull(onWindow);

        // EnumWindows is the documented reliable one-pass top-level enumeration
        // API: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enumwindows
        Exception? callbackException = null;
        EnumWindowsCallback callback = (windowHandle, context) =>
        {
            _ = context;
            try
            {
                onWindow(windowHandle);
                return true;
            }
            catch (Exception exception)
            {
                callbackException = exception;
                Marshal.SetLastPInvokeError(UnhandledExceptionError);
                return false;
            }
        };

        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.EnumWindows(callback, 0);
        int errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);

        if (callbackException is not null)
        {
            throw new InvalidOperationException(
                "The top-level window enumeration callback failed.",
                callbackException);
        }

        return new EnumWindowsResult(succeeded, errorCode);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsCallback(
        nint windowHandle,
        nint context);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(
            EnumWindowsCallback callback,
            nint context);
    }
}
