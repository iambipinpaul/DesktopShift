using System.Runtime.InteropServices;
using System.Text;

namespace DesktopShift.Windows.Observation;

public interface IWindowsWindowNativeApi
{
    bool IsWindow(nint windowHandle);

    bool IsWindowVisible(nint windowHandle);

    nint GetRootOwner(nint windowHandle);

    nint GetOwner(nint windowHandle);

    long GetWindowStyle(nint windowHandle);

    long GetWindowExtendedStyle(nint windowHandle);

    bool IsCloaked(nint windowHandle);

    nint GetShellWindow();

    nint GetDesktopWindow();

    uint GetWindowProcessId(nint windowHandle);

    string GetWindowClass(nint windowHandle);

    string? GetWindowTitle(nint windowHandle);

    /// <summary>
    /// Reads the AppUserModelID a window declares for itself through its shell
    /// property store (<c>PKEY_AppUserModel_ID</c>).
    /// </summary>
    /// <remarks>
    /// A process can host windows that belong to different shell applications:
    /// every Chrome or Edge installed web app (PWA) runs inside the browser's own
    /// process, and the only thing that tells its window apart from a browser
    /// window — and from another installed app — is the ID set on the window.
    /// It is what the taskbar groups by, and it outranks the process's ID there.
    /// </remarks>
    /// <param name="windowHandle">The top-level window.</param>
    /// <returns>
    /// The window's explicit AppUserModelID, or <see langword="null"/> when it
    /// declares none or the property store cannot be read.
    /// </returns>
    string? GetWindowAppUserModelId(nint windowHandle);

    /// <summary>
    /// Visits the descendants of <paramref name="windowHandle"/> until
    /// <paramref name="onChild"/> returns <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Used to find the process that owns the content inside a frame window,
    /// so enumeration stops as soon as the caller has what it came for rather
    /// than walking a tree it does not need.
    /// </remarks>
    /// <param name="windowHandle">The window whose descendants are visited.</param>
    /// <param name="onChild">
    /// Receives each descendant and returns whether to keep going. It must not
    /// throw: it runs inside a native callback.
    /// </param>
    void EnumerateChildWindows(nint windowHandle, Func<nint, bool> onChild);
}

public sealed class WindowsWindowNativeApi : IWindowsWindowNativeApi
{
    private const uint GetAncestorRootOwner = 3;
    private const uint GetWindowOwner = 4;
    private const int WindowStyle = -16;
    private const int WindowExtendedStyle = -20;
    private const uint DwmWindowAttributeCloaked = 14;
    private const ushort VariantTypeWideString = 31;

    // PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5.
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    public bool IsWindow(nint windowHandle) =>
        NativeMethods.IsWindow(windowHandle);

    public bool IsWindowVisible(nint windowHandle) =>
        NativeMethods.IsWindowVisible(windowHandle);

    public nint GetRootOwner(nint windowHandle) =>
        NativeMethods.GetAncestor(windowHandle, GetAncestorRootOwner);

    /// <summary>
    /// Reads a window's owner through <c>GetWindow(GW_OWNER)</c>, which Windows
    /// documents as the way to obtain an owner. <c>GetAncestor(GA_ROOTOWNER)</c>
    /// walks the chain <c>GetParent</c> returns, and <c>GetParent</c> reports an
    /// owner only for a <c>WS_POPUP</c> window, so it cannot be relied on alone.
    /// </summary>
    /// <param name="windowHandle">The window whose owner is read.</param>
    /// <returns>The owner window, or zero when the window has none.</returns>
    public nint GetOwner(nint windowHandle) =>
        NativeMethods.GetWindow(windowHandle, GetWindowOwner);

    public long GetWindowStyle(nint windowHandle) =>
        NativeMethods.GetWindowLongPtr(windowHandle, WindowStyle).ToInt64();

    public long GetWindowExtendedStyle(nint windowHandle) =>
        NativeMethods.GetWindowLongPtr(windowHandle, WindowExtendedStyle).ToInt64();

    public bool IsCloaked(nint windowHandle)
    {
        int cloaked = 0;
        int result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            DwmWindowAttributeCloaked,
            ref cloaked,
            sizeof(int));
        return result >= 0 && cloaked != 0;
    }

    public nint GetShellWindow() => NativeMethods.GetShellWindow();

    public nint GetDesktopWindow() => NativeMethods.GetDesktopWindow();

    public uint GetWindowProcessId(nint windowHandle)
    {
        NativeMethods.GetWindowThreadProcessId(windowHandle, out uint processId);
        return processId;
    }

    public string GetWindowClass(nint windowHandle)
    {
        StringBuilder buffer = new(256);
        int length = NativeMethods.GetClassName(
            windowHandle,
            buffer,
            buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    public string? GetWindowTitle(nint windowHandle)
    {
        int length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return null;
        }

        StringBuilder buffer = new(length + 1);
        return NativeMethods.GetWindowText(
            windowHandle,
            buffer,
            buffer.Capacity) > 0
                ? buffer.ToString()
                : null;
    }

    public string? GetWindowAppUserModelId(nint windowHandle)
    {
        Guid storeId = typeof(IPropertyStore).GUID;
        if (NativeMethods.SHGetPropertyStoreForWindow(
                windowHandle,
                ref storeId,
                out IPropertyStore? store) < 0 ||
            store is null)
        {
            return null;
        }

        try
        {
            PropertyKey key = AppUserModelIdKey;
            if (store.GetValue(ref key, out PropVariant value) < 0)
            {
                return null;
            }

            try
            {
                return value.ValueType == VariantTypeWideString &&
                    value.Pointer != 0 &&
                    Marshal.PtrToStringUni(value.Pointer) is { Length: > 0 } id
                        ? id
                        : null;
            }
            finally
            {
                _ = NativeMethods.PropVariantClear(ref value);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            _ = Marshal.ReleaseComObject(store);
        }
    }

    public void EnumerateChildWindows(nint windowHandle, Func<nint, bool> onChild)
    {
        ArgumentNullException.ThrowIfNull(onChild);

        NativeMethods.EnumChildWindowsCallback callback =
            (child, context) =>
            {
                _ = context;
                return onChild(child);
            };

        _ = NativeMethods.EnumChildWindows(windowHandle, callback, 0);
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey
    {
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        public Guid FormatId { get; }

        public uint PropertyId { get; }
    }

    // A PROPVARIANT is 16 bytes on 32-bit and 24 bytes on 64-bit Windows; only
    // the type tag and the first pointer-sized value are read.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort ValueType;

        [FieldOffset(8)]
        public nint Pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        internal static extern int SHGetPropertyStoreForWindow(
            nint windowHandle,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PropVariant value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate bool EnumChildWindowsCallback(
            nint windowHandle,
            nint context);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            nint parentWindow,
            EnumChildWindowsCallback callback,
            nint context);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint windowHandle);

        [DllImport("user32.dll")]
        internal static extern nint GetAncestor(
            nint windowHandle,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(
            nint windowHandle,
            uint command);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongPtrW",
            SetLastError = true)]
        internal static extern nint GetWindowLongPtr(
            nint windowHandle,
            int index);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            nint windowHandle,
            uint attribute,
            ref int value,
            int valueSize);

        [DllImport("user32.dll")]
        internal static extern nint GetShellWindow();

        [DllImport("user32.dll")]
        internal static extern nint GetDesktopWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            nint windowHandle,
            out uint processId);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(
            nint windowHandle,
            StringBuilder className,
            int maxCount);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowTextLengthW",
            CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(nint windowHandle);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowTextW",
            CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(
            nint windowHandle,
            StringBuilder text,
            int maxCount);
    }
}
