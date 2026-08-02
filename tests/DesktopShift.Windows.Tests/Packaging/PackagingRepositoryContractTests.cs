using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DesktopShift.Windows.Tests.Packaging;

[TestClass]
public sealed partial class PackagingRepositoryContractTests
{
    private static readonly string[] PrivateKeyExtensions =
        [".pfx", ".p12", ".key", ".snk"];

    private static readonly string[] ExcludedDirectorySegments =
        [
            Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + ".vs" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + ".artifacts" + Path.DirectorySeparatorChar,
        ];

    [TestMethod]
    public void PackageManifest_PinsIdentityVersionArchitectureAndVisualAssets()
    {
        string root = PackagingRepository.RequireRoot();
        string manifestPath = Path.Combine(
            root,
            "src",
            "DesktopShift.App",
            "Package.appxmanifest");
        XDocument manifest = PackagingRepository.LoadXml(manifestPath);

        XElement identity = PackagingRepository.RequireSingleDescendant(
            manifest,
            "Identity");
        Assert.AreEqual(
            "BipinPaul.DesktopShift",
            PackagingRepository.RequireAttribute(identity, "Name"));
        Assert.AreEqual(
            "CN=B035082D-0ECF-4DD6-B68E-293CC1A48C47",
            PackagingRepository.RequireAttribute(identity, "Publisher"),
            "Publisher must stay the Microsoft Store publisher ID; changing it " +
            "changes the package identity and orphans installed copies.");
        Assert.AreEqual(
            "x64",
            PackagingRepository.RequireAttribute(identity, "ProcessorArchitecture"));

        string version = PackagingRepository.RequireAttribute(identity, "Version");
        Assert.IsTrue(
            FourPartVersion().IsMatch(version),
            $"Package identity version must be four numeric parts, but was {version}.");
        Assert.AreNotEqual(
            new Version(0, 0, 0, 0),
            Version.Parse(version),
            "A release package cannot use the all-zero version.");

        string[] resourceLanguages = manifest
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                "Resource",
                StringComparison.Ordinal))
            .Select(element => element.Attribute("Language")?.Value)
            .OfType<string>()
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "en-us" },
            resourceLanguages,
            "DesktopShift ships English only. A generated language set derives " +
            "the Store listing's supported languages from build inputs, so it " +
            "drifts whenever the resource layout changes.");

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

        Assert.IsNotEmpty(assetReferences, "The package must declare visual assets.");
        foreach (string assetReference in assetReferences)
        {
            Assert.IsTrue(
                HasSourceAsset(Path.GetDirectoryName(manifestPath)!, assetReference),
                $"No source asset satisfies manifest reference {assetReference}.");
        }
    }

    [TestMethod]
    public void PackageManifest_DeclaresOptInStartupWithoutElevationOrMachineInstallHooks()
    {
        string root = PackagingRepository.RequireRoot();
        XDocument manifest = PackagingRepository.LoadXml(
            Path.Combine(
                root,
                "src",
                "DesktopShift.App",
                "Package.appxmanifest"));

        XElement startupExtension = manifest
            .Descendants()
            .Single(element =>
                element.Name.LocalName.Equals("Extension", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attribute("Category")?.Value,
                    "windows.startupTask",
                    StringComparison.Ordinal));
        XElement startupTask = PackagingRepository.RequireSingleDescendant(
            startupExtension,
            "StartupTask");

        Assert.AreEqual(
            "DesktopShiftStartupTask",
            PackagingRepository.RequireAttribute(startupTask, "TaskId"));
        Assert.AreEqual(
            "false",
            PackagingRepository.RequireAttribute(startupTask, "Enabled"));
        Assert.AreEqual(
            "DesktopShift",
            PackagingRepository.RequireAttribute(startupTask, "DisplayName"));

        string manifestText = manifest.ToString(SaveOptions.DisableFormatting);
        string[] prohibitedDeclarations =
            [
                "allowElevation",
                "packagedServices",
                "windows.service",
                "windows.customInstall",
                "ImmediateRegistration",
            ];

        foreach (string declaration in prohibitedDeclarations)
        {
            Assert.IsFalse(
                manifestText.Contains(
                    declaration,
                    StringComparison.OrdinalIgnoreCase),
                $"Per-user packaging must not declare {declaration}.");
        }

        string[] capabilities = manifest
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                "Capability",
                StringComparison.Ordinal))
            .Select(element => element.Attribute("Name")?.Value)
            .OfType<string>()
            .ToArray();
        CollectionAssert.Contains(capabilities, "runFullTrust");
        CollectionAssert.DoesNotContain(capabilities, "allowElevation");

        XElement vclibs = manifest
            .Descendants()
            .Single(element =>
                element.Name.LocalName.Equals(
                    "PackageDependency",
                    StringComparison.Ordinal) &&
                string.Equals(
                    element.Attribute("Name")?.Value,
                    "Microsoft.VCLibs.140.00.UWPDesktop",
                    StringComparison.Ordinal));
        Assert.IsTrue(
            Version.TryParse(vclibs.Attribute("MinVersion")?.Value, out _),
            "The native bridge's MSVC runtime dependency must be versioned.");
    }

    [TestMethod]
    public void AppProject_BuildsWindowedSelfContainedX64AndArm64PackagesWithNativeBridge()
    {
        string root = PackagingRepository.RequireRoot();
        XDocument project = PackagingRepository.LoadXml(
            Path.Combine(
                root,
                "src",
                "DesktopShift.App",
                "DesktopShift.App.csproj"));

        Assert.AreEqual(
            "WinExe",
            PackagingRepository.RequireProperty(project, "OutputType"));
        CollectionAssert.AreEquivalent(
            new[] { "x64", "ARM64" },
            SplitProperty(project, "Platforms"));
        CollectionAssert.AreEquivalent(
            new[] { "win-x64", "win-arm64" },
            SplitProperty(project, "RuntimeIdentifiers"));
        CollectionAssert.AreEquivalent(
            new[] { "win-x64", "win-arm64" },
            PropertyValues(project, "RuntimeIdentifier"));
        Assert.AreEqual(
            "true",
            PackagingRepository.RequireProperty(project, "EnableMsixTooling"));
        Assert.AreEqual(
            "MSIX",
            PackagingRepository.RequireProperty(project, "WindowsPackageType"));
        Assert.AreEqual(
            "true",
            PackagingRepository.RequireProperty(
                project,
                "WindowsAppSDKSelfContained"));
        Assert.AreEqual(
            "Never",
            PackagingRepository.RequireProperty(project, "AppxBundle"));
        Assert.AreEqual(
            "false",
            PackagingRepository.RequireProperty(
                project,
                "AppxAutoIncrementPackageRevision"));
        Assert.AreEqual(
            "false",
            PackagingRepository.RequireProperty(
                project,
                "AppxPackageSigningEnabled"));
        Assert.AreEqual(
            "SHA256",
            PackagingRepository.RequireProperty(
                project,
                "AppxPackageSigningTimestampDigestAlgorithm"));
        Assert.AreEqual(
            "1.0.0.0",
            PackagingRepository.RequireProperty(project, "AppxPackageVersion"));

        XElement nativeBridge = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName.Equals("None", StringComparison.Ordinal) &&
                (element.Attribute("Include")?.Value.Contains(
                    "DesktopShift.NativeBridge.dll",
                    StringComparison.OrdinalIgnoreCase) ?? false));

        StringAssert.Contains(
            PackagingRepository.RequireAttribute(nativeBridge, "Include"),
            @"bin\$(Platform)\$(Configuration)\DesktopShift.NativeBridge.dll",
            StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(
            "DesktopShift.NativeBridge.dll",
            PackagingRepository.RequireAttribute(nativeBridge, "Link"));
        Assert.AreEqual(
            "PreserveNewest",
            PackagingRepository.RequireAttribute(
                nativeBridge,
                "CopyToOutputDirectory"));
        Assert.AreEqual(
            "PreserveNewest",
            PackagingRepository.RequireAttribute(
                nativeBridge,
                "CopyToPublishDirectory"));

        string[] manuallyEditablePayloads = project
            .Descendants()
            .Where(element =>
                element.Name.LocalName is "Content" or "None")
            .Select(element =>
                element.Attribute("Include")?.Value ??
                element.Attribute("Update")?.Value)
            .OfType<string>()
            .Where(path =>
                path.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsEmpty(
            manuallyEditablePayloads,
            "The installed app must not depend on a user-edited config payload.");

        XElement[] architecturePokes = project
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("XmlPoke", StringComparison.Ordinal) &&
                (element.Attribute("Query")?.Value.Contains(
                    "ProcessorArchitecture",
                    StringComparison.Ordinal) ?? false))
            .ToArray();
        Assert.HasCount(
            1,
            architecturePokes,
            "Package generation must stamp the selected platform into the manifest.");

        foreach ((string fileName, string platform, string runtimeIdentifier) in new[]
        {
            ("win-x64.pubxml", "x64", "win-x64"),
            ("win-arm64.pubxml", "ARM64", "win-arm64"),
        })
        {
            XDocument profile = PackagingRepository.LoadXml(
                Path.Combine(
                    root,
                    "src",
                    "DesktopShift.App",
                    "Properties",
                    "PublishProfiles",
                    fileName));
            Assert.AreEqual(
                platform,
                PackagingRepository.RequireProperty(profile, "Platform"));
            Assert.AreEqual(
                runtimeIdentifier,
                PackagingRepository.RequireProperty(profile, "RuntimeIdentifier"));
        }
    }

    [TestMethod]
    public void PackageAndAssemblyVersions_StayAligned()
    {
        string root = PackagingRepository.RequireRoot();
        XDocument manifest = PackagingRepository.LoadXml(
            Path.Combine(
                root,
                "src",
                "DesktopShift.App",
                "Package.appxmanifest"));
        XDocument project = PackagingRepository.LoadXml(
            Path.Combine(
                root,
                "src",
                "DesktopShift.App",
                "DesktopShift.App.csproj"));

        Version packageVersion = Version.Parse(
            PackagingRepository.RequireAttribute(
                PackagingRepository.RequireSingleDescendant(manifest, "Identity"),
                "Version"));
        Version defaultPackageVersion = Version.Parse(
            PackagingRepository.RequireProperty(project, "AppxPackageVersion"));
        string fileVersionExpression = PackagingRepository.RequireProperty(
            project,
            "FileVersion");
        Version productVersion = Version.Parse(
            PackagingRepository.RequireProperty(project, "Version"));

        Assert.AreEqual(packageVersion, defaultPackageVersion);
        Assert.AreEqual(
            "$(AppxPackageVersion)",
            fileVersionExpression,
            "Assembly file version must follow the release package version override.");
        Assert.AreEqual(packageVersion.Major, productVersion.Major);
        Assert.AreEqual(packageVersion.Minor, productVersion.Minor);
        Assert.AreEqual(packageVersion.Build, productVersion.Build);
    }

    [TestMethod]
    public void NativeBridgeProject_BuildsX64AndArm64Configurations()
    {
        string root = PackagingRepository.RequireRoot();
        XDocument project = PackagingRepository.LoadXml(
            Path.Combine(
                root,
                "src",
                "DesktopShift.NativeBridge",
                "DesktopShift.NativeBridge.vcxproj"));

        string[] configurations = project
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                "ProjectConfiguration",
                StringComparison.Ordinal))
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Debug|x64",
                "Release|x64",
                "Debug|ARM64",
                "Release|ARM64",
            },
            configurations);
    }

    [TestMethod]
    public void Repository_ContainsSigningHooksButNoPrivateKeyMaterial()
    {
        string root = PackagingRepository.RequireRoot();
        string[] privateKeys = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !ExcludedDirectorySegments.Any(
                segment => path.Contains(
                    segment,
                    StringComparison.OrdinalIgnoreCase)))
            .Where(path => PrivateKeyExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
        Assert.IsEmpty(
            privateKeys,
            "Private key files must never be committed:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, privateKeys));

        string automationText = string.Join(
            Environment.NewLine,
            Directory
                .EnumerateFiles(
                    Path.Combine(root, "eng", "packaging"),
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    Path.GetExtension(path).Equals(
                        ".ps1",
                        StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        StringAssert.Contains(
            automationText,
            "AppxPackageSigningEnabled",
            StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(
            automationText,
            "PackageCertificateKeyFile",
            StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(
            automationText,
            "DESKTOPSHIFT_PACKAGE_CERTIFICATE_PASSWORD",
            StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(
            automationText,
            "signtool",
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ReleaseAutomation_RequiresPackageValidationAndSmokeBeforePublication()
    {
        string root = PackagingRepository.RequireRoot();
        string staticValidation = Path.Combine(
            root,
            "eng",
            "packaging",
            "Validate-Msix.ps1");
        string smokeValidation = Path.Combine(
            root,
            "eng",
            "packaging",
            "Smoke-Test-Msix.ps1");

        Assert.IsTrue(
            File.Exists(staticValidation),
            "The release pipeline needs a reusable static package validator.");
        Assert.IsTrue(
            File.Exists(smokeValidation),
            "The release pipeline needs a reusable install-launch-uninstall smoke test.");

        string[] workflows = Directory
            .EnumerateFiles(Path.Combine(root, ".github", "workflows"))
            .Where(path =>
                Path.GetExtension(path) is ".yml" or ".yaml")
            .ToArray();
        string? releaseWorkflowPath = workflows.SingleOrDefault(path =>
            File.ReadAllText(path).Contains(
                "Smoke-Test-Msix.ps1",
                StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(
            releaseWorkflowPath,
            "Exactly one workflow must run the package smoke gate.");
        string workflowText = File.ReadAllText(releaseWorkflowPath);

        int artifactContract = workflowText.IndexOf(
            "PackagingArtifactContractTests",
            StringComparison.OrdinalIgnoreCase);
        int staticContract = workflowText.IndexOf(
            "Validate-Msix.ps1",
            StringComparison.OrdinalIgnoreCase);
        int smokeContract = workflowText.IndexOf(
            "Smoke-Test-Msix.ps1",
            StringComparison.OrdinalIgnoreCase);
        int publication = workflowText.IndexOf(
            "upload-artifact",
            StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(
            artifactContract >= 0,
            "The signed package must run the artifact-level contract tests.");
        Assert.IsTrue(
            staticContract > artifactContract,
            "Static package validation must follow artifact construction.");
        Assert.IsTrue(
            smokeContract > staticContract,
            "Install-launch-uninstall smoke must follow static validation.");
        Assert.IsTrue(
            publication > smokeContract,
            "Artifact publication must happen only after every package gate.");
    }

    private static bool HasSourceAsset(
        string manifestDirectory,
        string manifestReference)
    {
        string normalized = manifestReference.Replace(
            '\\',
            Path.DirectorySeparatorChar);
        string exactPath = Path.Combine(manifestDirectory, normalized);
        if (File.Exists(exactPath))
        {
            return true;
        }

        string directory = Path.GetDirectoryName(exactPath)!;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(exactPath);
        string extension = Path.GetExtension(exactPath);
        return Directory
            .EnumerateFiles(directory, stem + ".*" + extension)
            .Any();
    }

    private static string[] SplitProperty(XDocument project, string propertyName) =>
        PropertyValues(project, propertyName)
            .SelectMany(value => value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] PropertyValues(XDocument project, string propertyName) =>
        project
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                propertyName,
                StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex FourPartVersion();
}
