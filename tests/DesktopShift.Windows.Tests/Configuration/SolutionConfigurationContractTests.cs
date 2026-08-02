using System.Xml.Linq;
using DesktopShift.Windows.Tests.Packaging;

namespace DesktopShift.Windows.Tests.Configuration;

[TestClass]
public sealed class SolutionConfigurationContractTests
{
    [TestMethod]
    public void ArchitectureSpecificManagedProjects_MapBothSupportedPlatforms()
    {
        string root = PackagingRepository.RequireRoot();
        XDocument solution = PackagingRepository.LoadXml(
            Path.Combine(root, "DesktopShift.slnx"));

        string[] supportedPlatforms = ["x64", "ARM64"];
        string[] missingMappings = solution
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("Project", StringComparison.Ordinal))
            .Select(element => new
            {
                Element = element,
                Path = element.Attribute("Path")?.Value,
            })
            .Where(project =>
                project.Path?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ==
                true)
            .Where(project => IsArchitectureSpecificProject(root, project.Path!))
            .SelectMany(project => supportedPlatforms
                .Where(platform => !HasSolutionMapping(project.Element, platform))
                .Select(platform => $"{project.Path} ({platform})"))
            .ToArray();

        Assert.IsEmpty(
            missingMappings,
            "Every architecture-specific managed project must map both supported " +
            "solution platforms to the matching project platform:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, missingMappings));
    }

    private static bool IsArchitectureSpecificProject(
        string root,
        string relativePath)
    {
        string projectPath = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument project = PackagingRepository.LoadXml(projectPath);

        string[] platforms = project
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals(
                    "Platforms",
                    StringComparison.Ordinal))
            .SelectMany(element => element.Value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return platforms.Contains("x64", StringComparer.OrdinalIgnoreCase) ||
            platforms.Contains("ARM64", StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasSolutionMapping(XElement project, string platform)
    {
        return project
            .Elements()
            .Where(element =>
                element.Name.LocalName.Equals(
                    "Platform",
                    StringComparison.Ordinal))
            .Any(element =>
            {
                string? projectPlatform = element.Attribute("Project")?.Value;
                string? solutionPlatform = element.Attribute("Solution")?.Value;
                return string.Equals(
                        projectPlatform,
                        platform,
                        StringComparison.OrdinalIgnoreCase) &&
                    (solutionPlatform is null ||
                        solutionPlatform.EndsWith(
                            $"|{platform}",
                            StringComparison.OrdinalIgnoreCase));
            });
    }
}
