using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Compatibility;

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
