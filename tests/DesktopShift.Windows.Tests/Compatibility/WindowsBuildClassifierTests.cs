using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Tests.Compatibility;

[TestClass]
public sealed class WindowsBuildClassifierTests
{
    [TestMethod]
    [DataRow(22631, WindowsBuildSupport.SupportedForEnterpriseOnly, "23H2")]
    [DataRow(26100, WindowsBuildSupport.Supported, "24H2")]
    [DataRow(26200, WindowsBuildSupport.Supported, "25H2")]
    [DataRow(28000, WindowsBuildSupport.Supported, "26H1")]
    public void Classify_RecognizedBuild_ReturnsRelease(
        int buildNumber,
        WindowsBuildSupport expectedSupport,
        string release)
    {
        WindowsBuildInfo build = Build(buildNumber);

        WindowsBuildAssessment result = WindowsBuildClassifier.Classify(build);

        Assert.AreEqual(expectedSupport, result.Support);
        StringAssert.Contains(result.DisplayVersion, release);
        StringAssert.Contains(result.DisplayVersion, build.ExactVersion);
        Assert.IsTrue(result.IsRecognized);
    }

    [TestMethod]
    public void Classify_UnknownWindows11Build_IsSafeAndExplicit()
    {
        WindowsBuildInfo build = Build(29999);

        WindowsBuildAssessment result = WindowsBuildClassifier.Classify(build);

        Assert.AreEqual(WindowsBuildSupport.UnknownWindows11Build, result.Support);
        Assert.IsFalse(result.IsRecognized);
        StringAssert.Contains(result.Explanation, "Safe Limited Mode");
        StringAssert.Contains(result.DisplayVersion, build.ExactVersion);
    }

    [TestMethod]
    public void ExactVersion_IncludesRevision()
    {
        WindowsBuildInfo build = new(
            isWindows: true,
            major: 10,
            minor: 0,
            build: 26200,
            revision: 1234,
            Architecture.X64);

        Assert.AreEqual("10.0.26200.1234", build.ExactVersion);
    }

    private static WindowsBuildInfo Build(int buildNumber) =>
        new(
            isWindows: true,
            major: 10,
            minor: 0,
            build: buildNumber,
            revision: 1,
            Architecture.X64);
}
