using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Windows.Compatibility;

namespace DesktopShift.Windows.Tests.Security;

/// <summary>
/// Proves DesktopShift is built to run as the signed-in user, and says so.
/// </summary>
/// <remarks>
/// <para>
/// Two separate promises are checked here. The first is that nothing in the
/// shipping application asks Windows for administrator rights — not in a
/// manifest, not through a package capability. The second is that the
/// consequence of holding only the user's own rights is explained rather than
/// left to be discovered: some windows cannot be managed, and no missing
/// companion is the reason.
/// </para>
/// <para>
/// The manifest checks read the repository's own files, because a
/// <c>requestedExecutionLevel</c> added later would change how the shipped
/// application starts without changing a single line of code.
/// </para>
/// </remarks>
[TestClass]
public sealed class ElevationBoundaryTests
{
    /// <summary>
    /// The only package capability DesktopShift declares.
    /// <c>runFullTrust</c> is what every packaged desktop application declares
    /// in order to run as a normal Win32 process; it grants the user's own
    /// rights, not administrator rights.
    /// </summary>
    private static readonly string[] AllowedPackageCapabilities =
        ["runFullTrust"];

    [TestMethod]
    public void ApplicationManifests_NeverRequestElevationOrUiAccess()
    {
        string? root = RepositorySource.TryFindRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return;
        }

        IReadOnlyList<SourceFile> manifests = RepositorySource.ReadManifests(root);
        Assert.IsGreaterThan(
            0,
            manifests.Count,
            "No application manifest was found, so the scan proved nothing.");

        List<string> violations = [];
        foreach (SourceFile manifest in manifests)
        {
            foreach (Match match in Regex.Matches(
                manifest.Text,
                @"requireAdministrator|highestAvailable|uiAccess\s*=\s*""true""|allowElevation",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                violations.Add($"{manifest.RelativePath}: '{match.Value}'");
            }
        }

        Assert.IsEmpty(
            violations,
            "A manifest asks for rights beyond the signed-in user's own:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void PackageManifest_DeclaresNoCapabilityBeyondRunningAsTheUser()
    {
        string? root = RepositorySource.TryFindRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return;
        }

        SourceFile? package = RepositorySource
            .ReadManifests(root)
            .FirstOrDefault(
                static manifest => manifest.RelativePath.EndsWith(
                    ".appxmanifest",
                    StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(package, "The package manifest was not found.");

        string[] declared = XDocument
            .Parse(package.Text)
            .Descendants()
            .Where(static element => element.Name.LocalName == "Capability")
            .Select(static element => element.Attribute("Name")?.Value ?? string.Empty)
            .ToArray();

        CollectionAssert.AreEquivalent(AllowedPackageCapabilities, declared);
    }

    [TestMethod]
    public void ExportedDiagnostics_CarryTheElevatedCompanionBoundary()
    {
        // The boundary belongs in what a user sends to a maintainer, not only in
        // a view they had to be looking at while the failure was on screen.
        StringAssert.Contains(
            DiagnosticBundleWriter.SecurityNotice,
            "never requests elevation");
        StringAssert.Contains(
            DiagnosticBundleWriter.SecurityNotice,
            PrivilegeBoundary.ElevatedCompanionNotice);
        StringAssert.Contains(
            PrivilegeBoundary.ElevatedCompanionNotice,
            "0x80070005");
        StringAssert.Contains(
            PrivilegeBoundary.ElevatedCompanionNotice,
            "No such companion is implemented");
        StringAssert.Contains(
            PrivilegeBoundary.ElevatedCompanionNotice,
            "works without one");
    }

    [TestMethod]
    public void CurrentTestProcess_IsReadableWithoutTouchingAnyOtherProcess()
    {
        // The real provider only ever reads this process's own token. Calling it
        // proves the read works and changes nothing on the machine; the answer
        // itself depends on how the test run was started, so it is not asserted.
        WindowsProcessPrivilegeProvider provider = new();

        bool first = provider.IsCurrentProcessElevated();
        bool second = provider.IsCurrentProcessElevated();

        Assert.AreEqual(first, second);
    }
}
