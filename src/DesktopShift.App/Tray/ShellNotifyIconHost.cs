using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopShift.Core;

namespace DesktopShift.App.Tray;

/// <summary>
/// Owns the DesktopShift notification-area icon through <c>Shell_NotifyIcon</c>.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 has no notification-area control, so the icon is created directly
/// against the documented shell API. That API is public and documented, so it
/// belongs in the application layer rather than the native bridge, which exists
/// only to isolate the private virtual-desktop COM interfaces.
/// </para>
/// <para>
/// Shell_NotifyIcon needs a window to deliver its callback message to, so this
/// host creates a hidden top-level window of its own. The window is not
/// message-only: TrackPopupMenu will not display a menu for a window that
/// cannot become foreground, and a message-only window never can.
/// </para>
/// <para>
/// Construct and dispose on the UI thread. The hidden window's messages are
/// pumped by the thread that created it, which is the same message loop WinUI
/// already runs.
/// </para>
/// </remarks>
public sealed class ShellNotifyIconHost : ITrayIconHost
{
    private const string WindowClassName = "DesktopShift.NotificationAreaHost";
    private const uint CallbackMessage = NativeMethods.WmApp + 1;
    private const uint IconId = 1;

    private readonly NativeMethods.WindowProcedure _windowProcedure;
    private readonly uint _taskbarCreatedMessage;
    private readonly nint _instanceHandle;
    private readonly nint _windowHandle;
    private readonly nint _iconHandle;
    private readonly bool _ownsIconHandle;
    private readonly object _syncRoot = new();
    private TrayMenuModel? _currentMenu;
    private bool _isIconAdded;
    private bool _isDisposed;

    public ShellNotifyIconHost()
    {
        // SetWindowLongPtr keeps the function pointer, not the delegate object.
        // Holding the delegate in a field for the window's whole lifetime is
        // what stops the GC collecting it out from under the shell.
        _windowProcedure = OnWindowMessage;
        _instanceHandle = NativeMethods.GetModuleHandle(null);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        RegisterWindowClass();
        _windowHandle = CreateHiddenWindow();
        (_iconHandle, _ownsIconHandle) = LoadApplicationIcon();
    }

    public event EventHandler<TrayCommandEventArgs>? CommandInvoked;

    public void Show(TrayMenuModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _currentMenu = model;
            NativeMethods.NotifyIconData data = CreateIconData(NativeMethods.NifMessage |
                NativeMethods.NifIcon |
                NativeMethods.NifTip);
            data.TipText = model.Tooltip;

            if (_isIconAdded)
            {
                _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
                return;
            }

            _isIconAdded = NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data);
        }
    }

    public void Notify(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        lock (_syncRoot)
        {
            if (_isDisposed || !_isIconAdded)
            {
                return;
            }

            NativeMethods.NotifyIconData data = CreateIconData(NativeMethods.NifInfo);
            data.InfoTitle = Truncate(notification.Title, NativeMethods.InfoTitleLength - 1);
            data.InfoText = Truncate(notification.Message, NativeMethods.InfoLength - 1);
            data.InfoFlags = notification.Severity switch
            {
                TrayNotificationSeverity.Error => NativeMethods.NiifError,
                TrayNotificationSeverity.Warning => NativeMethods.NiifWarning,
                _ => NativeMethods.NiifInfo,
            };

            _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_isIconAdded)
            {
                NativeMethods.NotifyIconData data = CreateIconData(0);
                _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
                _isIconAdded = false;
            }
        }

        CommandInvoked = null;

        if (_ownsIconHandle && _iconHandle != 0)
        {
            _ = NativeMethods.DestroyIcon(_iconHandle);
        }

        if (_windowHandle != 0)
        {
            _ = NativeMethods.DestroyWindow(_windowHandle);
        }
    }

    private void RegisterWindowClass()
    {
        NativeMethods.WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            InstanceHandle = _instanceHandle,
            ClassName = WindowClassName,
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) != 0)
        {
            return;
        }

        int error = Marshal.GetLastWin32Error();
        if (error != NativeMethods.ErrorClassAlreadyExists)
        {
            throw new Win32Exception(
                error,
                "DesktopShift could not register its notification-area window class.");
        }
    }

    private nint CreateHiddenWindow()
    {
        nint handle = NativeMethods.CreateWindowEx(
            dwExStyle: NativeMethods.WsExToolWindow,
            lpClassName: WindowClassName,
            lpWindowName: ProductInfo.ApplicationName,
            dwStyle: NativeMethods.WsPopup,
            x: 0,
            y: 0,
            nWidth: 0,
            nHeight: 0,
            hWndParent: 0,
            hMenu: 0,
            hInstance: _instanceHandle,
            lpParam: 0);

        return handle != 0
            ? handle
            : throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "DesktopShift could not create its notification-area window.");
    }

    private static (nint Handle, bool IsOwned) LoadApplicationIcon()
    {
        string? executablePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executablePath))
        {
            nint extracted = NativeMethods.ExtractIcon(0, executablePath, 0);

            // ExtractIcon returns 1 when the file holds no icon at all, which is
            // not a handle and must never be destroyed.
            if (extracted != 0 && extracted != 1)
            {
                return (extracted, true);
            }
        }

        // The shared system icon is owned by Windows, so it is used but never
        // destroyed.
        return (NativeMethods.LoadIcon(0, NativeMethods.IdiApplication), false);
    }

    private NativeMethods.NotifyIconData CreateIconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        TipText = string.Empty,
        InfoText = string.Empty,
        InfoTitle = string.Empty,
    };

    private nint OnWindowMessage(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            uint notification = (uint)(lParam & 0xFFFF);
            switch (notification)
            {
                case NativeMethods.WmLButtonUp:
                    Raise(TrayCommand.Open);
                    return 0;

                case NativeMethods.WmRButtonUp:
                case NativeMethods.WmContextMenu:
                    ShowContextMenu();
                    return 0;

                default:
                    break;
            }
        }
        else if (message == _taskbarCreatedMessage)
        {
            RestoreIconAfterExplorerRestart();
            return 0;
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void RestoreIconAfterExplorerRestart()
    {
        TrayMenuModel? menu;
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            // Explorer forgets every icon when it restarts, so the next Show has
            // to add the icon again rather than modify one the shell no longer
            // knows about.
            _isIconAdded = false;
            menu = _currentMenu;
        }

        if (menu is not null)
        {
            Show(menu);
        }
    }

    private void ShowContextMenu()
    {
        TrayMenuModel? menu;
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            menu = _currentMenu;
        }

        if (menu is null || !NativeMethods.GetCursorPos(out NativeMethods.Point cursor))
        {
            return;
        }

        nint menuHandle = NativeMethods.CreatePopupMenu();
        if (menuHandle == 0)
        {
            return;
        }

        try
        {
            foreach (TrayMenuItem item in menu.Items)
            {
                if (item.HasSeparatorBefore)
                {
                    _ = NativeMethods.AppendMenu(
                        menuHandle,
                        NativeMethods.MfSeparator,
                        0,
                        null);
                }

                uint flags = NativeMethods.MfString |
                    (item.IsEnabled ? NativeMethods.MfEnabled : NativeMethods.MfGrayed);
                _ = NativeMethods.AppendMenu(
                    menuHandle,
                    flags,
                    ToCommandId(item.Command),
                    item.Label);
            }

            // The shell requires the owning window to be foreground before the
            // menu appears, and a trailing null message so the menu dismisses
            // when the user clicks elsewhere.
            _ = NativeMethods.SetForegroundWindow(_windowHandle);
            int selected = NativeMethods.TrackPopupMenuEx(
                menuHandle,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCmd | NativeMethods.TpmNonNotify,
                cursor.X,
                cursor.Y,
                _windowHandle,
                0);
            _ = NativeMethods.PostMessage(_windowHandle, NativeMethods.WmNull, 0, 0);

            if (TryFromCommandId(selected, out TrayCommand command))
            {
                Raise(command);
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menuHandle);
        }
    }

    private void Raise(TrayCommand command)
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }
        }

        CommandInvoked?.Invoke(this, new TrayCommandEventArgs(command));
    }

    // Menu command 0 means "nothing was picked", so identifiers start at one.
    private static uint ToCommandId(TrayCommand command) => (uint)command + 1;

    private static bool TryFromCommandId(int commandId, out TrayCommand command)
    {
        command = default;
        if (commandId <= 0)
        {
            return false;
        }

        TrayCommand candidate = (TrayCommand)(commandId - 1);
        if (!Enum.IsDefined(candidate))
        {
            return false;
        }

        command = candidate;
        return true;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static class NativeMethods
    {
        internal const int InfoLength = 256;
        internal const int InfoTitleLength = 64;
        internal const int TipLength = 128;

        internal const uint WmApp = 0x8000;
        internal const uint WmNull = 0x0000;
        internal const uint WmLButtonUp = 0x0202;
        internal const uint WmRButtonUp = 0x0205;
        internal const uint WmContextMenu = 0x007B;

        internal const uint NimAdd = 0x00000000;
        internal const uint NimModify = 0x00000001;
        internal const uint NimDelete = 0x00000002;

        internal const uint NifMessage = 0x00000001;
        internal const uint NifIcon = 0x00000002;
        internal const uint NifTip = 0x00000004;
        internal const uint NifInfo = 0x00000010;

        internal const uint NiifInfo = 0x00000001;
        internal const uint NiifWarning = 0x00000002;
        internal const uint NiifError = 0x00000003;

        internal const uint MfString = 0x00000000;
        internal const uint MfEnabled = 0x00000000;
        internal const uint MfGrayed = 0x00000001;
        internal const uint MfSeparator = 0x00000800;

        internal const uint TpmRightButton = 0x0002;
        internal const uint TpmReturnCmd = 0x0100;
        internal const uint TpmNonNotify = 0x0080;

        internal const uint WsPopup = 0x80000000;
        internal const uint WsExToolWindow = 0x00000080;

        internal const int ErrorClassAlreadyExists = 1410;
        internal const int IdiApplication = 32512;

        internal delegate nint WindowProcedure(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClassEx
        {
            public uint Size;
            public uint Style;
            public nint WindowProcedure;
            public int ClassExtraBytes;
            public int WindowExtraBytes;
            public nint InstanceHandle;
            public nint IconHandle;
            public nint CursorHandle;
            public nint BackgroundBrush;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string ClassName;
            public nint SmallIconHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotifyIconData
        {
            public uint Size;
            public nint WindowHandle;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public nint IconHandle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TipLength)]
            public string TipText;
            public uint State;
            public uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoLength)]
            public string InfoText;
            public uint VersionOrTimeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoTitleLength)]
            public string InfoTitle;
            public uint InfoFlags;
            public Guid ItemGuid;
            public nint BalloonIconHandle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellNotifyIcon(
            uint message,
            ref NotifyIconData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
        internal static extern nint CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            nint hWndParent,
            nint hMenu,
            nint hInstance,
            nint lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint windowHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
        internal static extern uint RegisterWindowMessage(string message);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        internal static extern nint GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadIconW")]
        internal static extern nint LoadIcon(nint instanceHandle, nint iconName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconW")]
        internal static extern nint ExtractIcon(
            nint instanceHandle,
            string executablePath,
            uint iconIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint iconHandle);

        [DllImport("user32.dll")]
        internal static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenu(
            nint menuHandle,
            uint flags,
            uint itemId,
            string? item);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(nint menuHandle);

        [DllImport("user32.dll")]
        internal static extern int TrackPopupMenuEx(
            nint menuHandle,
            uint flags,
            int x,
            int y,
            nint windowHandle,
            nint parameters);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint windowHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);
    }
}
