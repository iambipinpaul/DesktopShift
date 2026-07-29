using System.Runtime.InteropServices;
using System.Security.Principal;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Compatibility;

/// <summary>
/// Reads DesktopShift's own elevation state from its own token.
/// </summary>
/// <remarks>
/// This exists so a diagnostic can state the privileges DesktopShift actually
/// holds. It reads the current process and nothing else: no other process is
/// opened, no token is duplicated or adjusted, and no privilege is enabled. A
/// failure to read is reported as "not elevated", because the unelevated answer
/// is the one that promises less.
/// </remarks>
public sealed class WindowsProcessPrivilegeProvider : IProcessPrivilegeProvider
{
    public bool IsCurrentProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(
                WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class EnvironmentWindowsBuildInfoProvider : IWindowsBuildInfoProvider
{
    public WindowsBuildInfo GetCurrent()
    {
        Version version = Environment.OSVersion.Version;

        return new WindowsBuildInfo(
            OperatingSystem.IsWindows(),
            version.Major,
            version.Minor,
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision),
            RuntimeInformation.OSArchitecture);
    }
}
