using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Observation;

public sealed class WindowsWindowClassifier : IWindowClassifier
{
    private const long ChildWindowStyle = 0x40000000L;
    private const long ToolWindowExtendedStyle = 0x00000080L;

    private static readonly HashSet<string> TransientClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "#32768",
            "SysShadow",
            "TaskListThumbnailWnd",
            "tooltips_class32",
            "Xaml_WindowedPopupClass",
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

        nint rootOwner = nativeApi.GetRootOwner(windowHandle);
        if (rootOwner == 0)
        {
            rootOwner = windowHandle;
        }

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
}
