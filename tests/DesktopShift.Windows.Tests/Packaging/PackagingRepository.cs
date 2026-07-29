using System.IO.Compression;
using System.Xml.Linq;

namespace DesktopShift.Windows.Tests.Packaging;

internal static class PackagingRepository
{
    private const string SolutionFileName = "DesktopShift.slnx";

    public static string RequireRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        Assert.Inconclusive(
            "The repository source tree is not present next to the test assembly.");
        return string.Empty;
    }

    public static XDocument LoadXml(string path)
    {
        Assert.IsTrue(File.Exists(path), $"Required file was not found: {path}");
        return XDocument.Load(path, LoadOptions.SetLineInfo);
    }

    public static XElement RequireSingleDescendant(
        XContainer container,
        string localName)
    {
        XElement[] matches = container
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                localName,
                StringComparison.Ordinal))
            .ToArray();

        Assert.HasCount(
            1,
            matches,
            $"Expected exactly one {localName} element.");
        return matches[0];
    }

    public static string RequireAttribute(XElement element, string localName)
    {
        XAttribute? attribute = element
            .Attributes()
            .SingleOrDefault(candidate => candidate.Name.LocalName.Equals(
                localName,
                StringComparison.Ordinal));

        Assert.IsNotNull(
            attribute,
            $"{element.Name.LocalName} must declare {localName}.");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(attribute.Value),
            $"{element.Name.LocalName}.{localName} must not be blank.");
        return attribute.Value;
    }

    public static string RequireProperty(XDocument project, string propertyName)
    {
        XElement[] properties = project
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                propertyName,
                StringComparison.Ordinal))
            .ToArray();

        Assert.IsNotEmpty(
            properties,
            $"The project must declare {propertyName} explicitly.");

        string[] values = properties
            .Select(property => property.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.HasCount(
            1,
            values,
            $"{propertyName} must have one unambiguous value.");
        return values[0];
    }

    public static bool PackageContains(
        ZipArchive archive,
        string manifestPath)
    {
        string normalized = manifestPath.Replace('\\', '/');
        string directory = Path.GetDirectoryName(normalized)?
            .Replace('\\', '/') ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(normalized);
        string extension = Path.GetExtension(normalized);

        return archive.Entries.Any(entry =>
        {
            string entryPath = entry.FullName.Replace('\\', '/');
            if (entryPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string entryDirectory = Path.GetDirectoryName(entryPath)?
                .Replace('\\', '/') ?? string.Empty;
            string entryFileName = Path.GetFileName(entryPath);
            return entryDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
                entryFileName.StartsWith(
                    fileName + ".",
                    StringComparison.OrdinalIgnoreCase) &&
                entryFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        });
    }
}
