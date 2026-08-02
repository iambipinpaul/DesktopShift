using System.IO.Compression;
using System.Xml.Linq;

namespace DesktopShift.Windows.Tests.Packaging;

[TestClass]
public sealed class PackagingArtifactContractTests
{
    private const string PackagePathVariable = "DESKTOPSHIFT_MSIX_PATH";
    private const string PackageArchitectureVariable =
        "DESKTOPSHIFT_MSIX_ARCHITECTURE";

    [TestMethod]
    public void SignedPackage_IsACompleteInstallableApplicationForExpectedArchitecture()
    {
        string? packagePath = Environment.GetEnvironmentVariable(
            PackagePathVariable);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            Assert.Inconclusive(
                $"Set {PackagePathVariable} to the final signed MSIX to run " +
                "artifact-level package validation.");
            return;
        }

        packagePath = Path.GetFullPath(packagePath);
        Assert.IsTrue(
            File.Exists(packagePath),
            $"{PackagePathVariable} does not identify a file: {packagePath}");
        Assert.AreEqual(
            ".msix",
            Path.GetExtension(packagePath),
            ignoreCase: true,
            "The release artifact must be an MSIX package.");

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? manifestEntry = archive.GetEntry("AppxManifest.xml");
        Assert.IsNotNull(manifestEntry, "The package has no AppxManifest.xml.");

        XDocument manifest;
        using (Stream manifestStream = manifestEntry.Open())
        {
            manifest = XDocument.Load(manifestStream);
        }

        string expectedArchitecture =
            Environment.GetEnvironmentVariable(PackageArchitectureVariable) ?? "x64";
        Assert.IsTrue(
            expectedArchitecture is "x64" or "arm64",
            $"{PackageArchitectureVariable} must be x64 or arm64.");

        AssertPackageIdentity(manifest, expectedArchitecture);
        AssertStartupContract(manifest);
        AssertVisualAssetsArePresent(manifest, archive);
        AssertPayloadIsSelfContained(archive);
    }

    private static void AssertPackageIdentity(
        XDocument manifest,
        string expectedArchitecture)
    {
        XElement identity = PackagingRepository.RequireSingleDescendant(
            manifest,
            "Identity");
        Assert.AreEqual(
            "BipinPaul.DesktopShift",
            PackagingRepository.RequireAttribute(identity, "Name"));
        Assert.AreEqual(
            "CN=Bipin Paul",
            PackagingRepository.RequireAttribute(identity, "Publisher"));
        Assert.AreEqual(
            expectedArchitecture,
            PackagingRepository.RequireAttribute(identity, "ProcessorArchitecture"));

        Version version = Version.Parse(
            PackagingRepository.RequireAttribute(identity, "Version"));
        Assert.AreNotEqual(new Version(0, 0, 0, 0), version);
    }

    private static void AssertStartupContract(XDocument manifest)
    {
        XElement startupTask = PackagingRepository.RequireSingleDescendant(
            manifest,
            "StartupTask");
        Assert.AreEqual(
            "DesktopShiftStartupTask",
            PackagingRepository.RequireAttribute(startupTask, "TaskId"));
        Assert.AreEqual(
            "false",
            PackagingRepository.RequireAttribute(startupTask, "Enabled"));

        string manifestText = manifest.ToString(SaveOptions.DisableFormatting);
        Assert.IsFalse(
            manifestText.Contains(
                "allowElevation",
                StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            manifestText.Contains(
                "ImmediateRegistration",
                StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertVisualAssetsArePresent(
        XDocument manifest,
        ZipArchive archive)
    {
        string[] assetReferences = manifest
            .Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute =>
                attribute.Name.LocalName is
                    "Logo" or
                    "Square150x150Logo" or
                    "Square44x44Logo" or
                    "Wide310x150Logo" or
                    "Image")
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.IsNotEmpty(assetReferences);
        foreach (string assetReference in assetReferences)
        {
            Assert.IsTrue(
                PackagingRepository.PackageContains(archive, assetReference),
                $"The package is missing visual asset {assetReference}.");
        }
    }

    private static void AssertPayloadIsSelfContained(ZipArchive archive)
    {
        string[] payload = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToArray();

        Assert.IsTrue(
            payload.Any(path => path.Equals(
                "DesktopShift.exe",
                StringComparison.OrdinalIgnoreCase)),
            "The package is missing the windowed application executable.");
        Assert.IsTrue(
            payload.Any(path => path.EndsWith(
                "/DesktopShift.NativeBridge.dll",
                StringComparison.OrdinalIgnoreCase) ||
                path.Equals(
                    "DesktopShift.NativeBridge.dll",
                    StringComparison.OrdinalIgnoreCase)),
            "The package is missing DesktopShift.NativeBridge.dll.");
        Assert.IsTrue(
            payload.Any(path => path.EndsWith(
                "Microsoft.UI.Xaml.dll",
                StringComparison.OrdinalIgnoreCase)),
            "The package is missing the self-contained WinUI runtime.");
        Assert.IsTrue(
            payload.Any(path => path.Contains(
                "Microsoft.WindowsAppRuntime",
                StringComparison.OrdinalIgnoreCase)),
            "The package is missing self-contained Windows App Runtime binaries.");
        Assert.IsTrue(
            payload.Any(path => path.Equals(
                "AppxSignature.p7x",
                StringComparison.OrdinalIgnoreCase)),
            "The final release MSIX must be signed before validation.");
        Assert.IsFalse(
            payload.Any(path => path.EndsWith(
                ".pfx",
                StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)),
            "Private signing material must never be placed in the package.");
    }
}
