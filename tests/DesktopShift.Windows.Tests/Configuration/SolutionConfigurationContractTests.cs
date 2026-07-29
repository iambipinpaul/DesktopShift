using System.Xml.Linq;
using DesktopShift.Windows.Tests.Packaging;

namespace DesktopShift.Windows.Tests.Configuration;

[TestClass]
public sealed class SolutionConfigurationContractTests
{
    [TestMethod]
    public void X64OnlyManagedProjects_ExplicitlyMapSolutionX64ToProjectX64()
    {
        string root = PackagingRepository.RequireRoot();
        XDocument solution = PackagingRepository.LoadXml(
            Path.Combine(root, "DesktopShift.slnx"));

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
            .Where(project => IsX64OnlyProject(root, project.Path!))
            .Where(project => !HasX64SolutionMapping(project.Element))
            .Select(project => project.Path!)
            .ToArray();

        Assert.IsEmpty(
            missingMappings,
            "Every x64-only managed project must map the solution's x64 platform " +
            "to project platform x64:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, missingMappings));
    }

    private static bool IsX64OnlyProject(string root, string relativePath)
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

        return platforms.Length == 1 &&
            platforms[0].Equals("x64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasX64SolutionMapping(XElement project)
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
                        "x64",
                        StringComparison.OrdinalIgnoreCase) &&
                    (solutionPlatform is null ||
                        solutionPlatform.EndsWith(
                            "|x64",
                            StringComparison.OrdinalIgnoreCase));
            });
    }
}
