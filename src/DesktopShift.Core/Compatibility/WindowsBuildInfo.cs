using System.Runtime.InteropServices;

namespace DesktopShift.Core.Compatibility;

public sealed record WindowsBuildInfo
{
    public WindowsBuildInfo(
        bool isWindows,
        int major,
        int minor,
        int build,
        int revision,
        Architecture architecture)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(build);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        IsWindows = isWindows;
        Major = major;
        Minor = minor;
        Build = build;
        Revision = revision;
        Architecture = architecture;
    }

    public bool IsWindows { get; }

    public int Major { get; }

    public int Minor { get; }

    public int Build { get; }

    public int Revision { get; }

    public Architecture Architecture { get; }

    public string ExactVersion => $"{Major}.{Minor}.{Build}.{Revision}";
}

public interface IWindowsBuildInfoProvider
{
    WindowsBuildInfo GetCurrent();
}

public enum WindowsBuildSupport
{
    UnsupportedPlatform,
    UnsupportedWindowsVersion,
    Supported,
    SupportedForEnterpriseOnly,
    UnknownWindows11Build,
}

public sealed record WindowsBuildAssessment(
    WindowsBuildSupport Support,
    string DisplayVersion,
    string Explanation)
{
    public bool IsRecognized =>
        Support is WindowsBuildSupport.Supported or WindowsBuildSupport.SupportedForEnterpriseOnly;
}

public static class WindowsBuildClassifier
{
    public static WindowsBuildAssessment Classify(WindowsBuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);

        if (!build.IsWindows)
        {
            return new(
                WindowsBuildSupport.UnsupportedPlatform,
                $"Non-Windows {build.ExactVersion}",
                "DesktopShift requires Windows 11.");
        }

        if (build.Major != 10 || build.Build < 22000)
        {
            return new(
                WindowsBuildSupport.UnsupportedWindowsVersion,
                $"Windows {build.ExactVersion}",
                "DesktopShift requires Windows 11.");
        }

        return build.Build switch
        {
            22631 => new(
                WindowsBuildSupport.SupportedForEnterpriseOnly,
                $"Windows 11 23H2 (build {build.ExactVersion})",
                "Windows 11 23H2 is supported only on in-support Enterprise and IoT Enterprise editions."),
            26100 => new(
                WindowsBuildSupport.Supported,
                $"Windows 11 24H2 (build {build.ExactVersion})",
                "This Windows 11 build family is recognized."),
            26200 => new(
                WindowsBuildSupport.Supported,
                $"Windows 11 25H2 (build {build.ExactVersion})",
                "This Windows 11 build family is recognized."),
            28000 => new(
                WindowsBuildSupport.Supported,
                $"Windows 11 26H1 (build {build.ExactVersion})",
                "This Windows 11 build family is recognized and must use an independently validated adapter for Full Mode."),
            _ => new(
                WindowsBuildSupport.UnknownWindows11Build,
                $"Windows 11 (unrecognized build {build.ExactVersion})",
                "This Windows 11 build is not in DesktopShift's validated build matrix. Safe Limited Mode remains available."),
        };
    }
}
