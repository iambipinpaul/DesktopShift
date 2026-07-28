using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Observation;

public sealed class WindowsWindowClassifier : IWindowClassifier
{
    private const long ChildWindowStyle = 0x40000000L;
    private const long ToolWindowExtendedStyle = 0x00000080L;

    // An owner chain is a handful of links deep in practice. The bound exists so
    // a malformed or racing chain can never spin, not because deep chains are
    // expected.
    private const int MaximumOwnerChainDepth = 16;

    private static readonly HashSet<string> TransientClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "#32768",
            "CicMarshalWndClass",
            "IME",
            "MSCTFIME UI",
            "SysShadow",
            "TaskListThumbnailWnd",
            "tooltips_class32",
            "Xaml_WindowedPopupClass",
        };

    // Chromium's hidden message-only, renderer compositing, and GPU
    // intermediate surfaces. Created by the browser process and by every
    // renderer / GPU / utility / crash-handler subprocess, and shared by all
    // Chromium-based shells rather than being Edge- or Chrome-specific.
    private static readonly HashSet<string> BrowserHelperClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Chrome_MessageWindow",
            "Chrome_RenderWidgetHostHWND",
            "Intermediate D3D Window",
        };

    private static readonly HashSet<string> ShellClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
        };

    private static readonly HashSet<string> SystemUiProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "dwm.exe",
            "LockApp.exe",
            "SearchHost.exe",
            "ShellExperienceHost.exe",
            "sihost.exe",
            "StartMenuExperienceHost.exe",
            "TextInputHost.exe",
        };

    private readonly IWindowsWindowNativeApi nativeApi;
    private readonly uint currentProcessId;

    public WindowsWindowClassifier(
        IWindowsWindowNativeApi? nativeApi = null,
        uint? currentProcessId = null)
    {
        this.nativeApi = nativeApi ?? new WindowsWindowNativeApi();
        this.currentProcessId = currentProcessId ??
            unchecked((uint)Environment.ProcessId);
    }

    public WindowQualification Qualify(nint windowHandle)
    {
        if (windowHandle == 0 || !nativeApi.IsWindow(windowHandle))
        {
            return WindowQualification.Skipped(WindowSkipReason.StaleWindow);
        }

        if ((nativeApi.GetWindowStyle(windowHandle) & ChildWindowStyle) != 0)
        {
            return WindowQualification.Skipped(WindowSkipReason.ChildWindow);
        }

        nint rootOwner = ResolveRootOwner(windowHandle);
        if (!nativeApi.IsWindow(rootOwner))
        {
            return WindowQualification.Skipped(WindowSkipReason.StaleWindow);
        }

        if (rootOwner == nativeApi.GetShellWindow() ||
            rootOwner == nativeApi.GetDesktopWindow())
        {
            return WindowQualification.Skipped(WindowSkipReason.ShellWindow);
        }

        string windowClass = nativeApi.GetWindowClass(rootOwner);
        if (ShellClasses.Contains(windowClass))
        {
            return WindowQualification.Skipped(WindowSkipReason.ShellWindow);
        }

        if (BrowserHelperClasses.Contains(windowClass))
        {
            return WindowQualification.Skipped(
                WindowSkipReason.BrowserHelperWindow);
        }

        if (TransientClasses.Contains(windowClass))
        {
            return WindowQualification.Skipped(WindowSkipReason.TransientWindow);
        }

        if ((nativeApi.GetWindowExtendedStyle(rootOwner) &
            ToolWindowExtendedStyle) != 0)
        {
            return WindowQualification.Skipped(WindowSkipReason.ToolWindow);
        }

        if (nativeApi.IsCloaked(rootOwner))
        {
            return WindowQualification.Skipped(WindowSkipReason.CloakedWindow);
        }

        if (!nativeApi.IsWindowVisible(rootOwner))
        {
            return WindowQualification.Skipped(WindowSkipReason.InvisibleWindow);
        }

        uint processId = nativeApi.GetWindowProcessId(rootOwner);
        if (processId == 0)
        {
            return WindowQualification.Skipped(WindowSkipReason.StaleWindow);
        }

        if (processId == currentProcessId)
        {
            return WindowQualification.Skipped(
                WindowSkipReason.DesktopShiftWindow);
        }

        return WindowQualification.Qualified(
            new QualifiedWindow(
                windowHandle,
                rootOwner,
                processId,
                windowClass));
    }

    public WindowSkipReason ClassifyIdentity(
        QualifiedWindow window,
        WindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.ProcessId == currentProcessId ||
            identity.ProcessName.StartsWith(
                "DesktopShift",
                StringComparison.OrdinalIgnoreCase))
        {
            return WindowSkipReason.DesktopShiftWindow;
        }

        return SystemUiProcesses.Contains(identity.ProcessName)
            ? WindowSkipReason.SystemWindow
            : WindowSkipReason.None;
    }

    /// <summary>
    /// Resolves the window that owns <paramref name="windowHandle"/>, so an
    /// owned dialog is never classified, matched, or assigned on its own
    /// identity.
    /// </summary>
    /// <remarks>
    /// Windows exposes two views of ownership and they do not agree.
    /// <c>GetAncestor(GA_ROOTOWNER)</c> walks the chain <c>GetParent</c>
    /// returns, and <c>GetParent</c> reports an owner only for a
    /// <c>WS_POPUP</c> window, so an owned window created without that style
    /// reports itself as its own root. <c>GetWindow(GW_OWNER)</c> is the
    /// documented reader for any owner. The ancestor result is therefore
    /// continued along the owner chain instead of being trusted alone.
    /// <para>
    /// This is what makes a Remote Desktop reconnection or credential dialog
    /// follow its session frame. The dialog may even belong to another process
    /// — a credential prompt is often hosted by <c>CredentialUIBroker.exe</c> —
    /// and ownership still resolves to the frame, so the session's identity and
    /// its desktop are the ones that decide where the dialog goes.
    /// </para>
    /// </remarks>
    /// <param name="windowHandle">The window an event described.</param>
    /// <returns>
    /// The topmost owner, or <paramref name="windowHandle"/> when it owns
    /// itself.
    /// </returns>
    private nint ResolveRootOwner(nint windowHandle)
    {
        nint rootOwner = nativeApi.GetRootOwner(windowHandle);
        if (rootOwner == 0)
        {
            rootOwner = windowHandle;
        }

        for (int depth = 0; depth < MaximumOwnerChainDepth; depth++)
        {
            nint owner = nativeApi.GetOwner(rootOwner);
            if (owner == 0 || owner == rootOwner)
            {
                return rootOwner;
            }

            rootOwner = owner;
        }

        return rootOwner;
    }
}
