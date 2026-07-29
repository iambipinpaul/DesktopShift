using System.Text;

namespace DesktopShift.Windows.Tests.Security;

/// <summary>
/// One shipping source file, with its comments already removed.
/// </summary>
/// <param name="RelativePath">
/// The path relative to the repository root, so a failure names a file a reader
/// can open rather than a path from this machine.
/// </param>
/// <param name="Text">The file exactly as it is on disk.</param>
/// <param name="Code">
/// The same file with every comment blanked out. Prohibited-technique checks run
/// against this, because a comment that names a technique in order to forbid it
/// is the opposite of a violation.
/// </param>
internal sealed record SourceFile(
    string RelativePath,
    string Text,
    string Code);

/// <summary>
/// Reads the repository's own shipping source so a test can make claims about
/// the code that ships rather than about one type's behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The repository root is found by walking up from the test assembly until the
/// solution file appears. Nothing is hard-coded to a machine, and a run where
/// the source tree is absent — a published test assembly, for instance — is
/// reported as inconclusive rather than failed, because absence of the source
/// is not evidence of a violation.
/// </para>
/// <para>
/// Only <c>src</c> is read. Generated output under <c>obj</c> and <c>bin</c> is
/// excluded because it is not authored, and the tests themselves are excluded
/// because a scanner has to be able to name the techniques it forbids.
/// </para>
/// </remarks>
internal static class RepositorySource
{
    private const string SolutionFileName = "DesktopShift.slnx";

    private static readonly string[] SourceExtensions =
        [".cs", ".cpp", ".h"];

    private static readonly string[] ExcludedDirectorySegments =
        [
            Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
        ];

    /// <summary>
    /// Finds the repository root, or null when the source tree is not present
    /// next to the test assembly.
    /// </summary>
    /// <returns>The repository root directory, or null.</returns>
    public static string? TryFindRoot()
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

        return null;
    }

    /// <summary>
    /// Reads every authored source file under <c>src</c>.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The files to scan, ordered by path.</returns>
    public static IReadOnlyList<SourceFile> ReadShippingSources(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string sourceRoot = Path.Combine(root, "src");
        List<SourceFile> files = [];

        foreach (string path in Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsAuthoredSource(path))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            files.Add(
                new SourceFile(
                    Path.GetRelativePath(root, path),
                    text,
                    RemoveComments(text)));
        }

        return files;
    }

    /// <summary>
    /// Reads every application manifest, packaged and unpackaged.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The manifests to scan, ordered by path.</returns>
    public static IReadOnlyList<SourceFile> ReadManifests(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string sourceRoot = Path.Combine(root, "src");
        List<SourceFile> files = [];

        foreach (string path in Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(path);
            bool isManifest =
                extension.Equals(".manifest", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".appxmanifest", StringComparison.OrdinalIgnoreCase);
            if (!isManifest || IsGenerated(path))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            files.Add(
                new SourceFile(Path.GetRelativePath(root, path), text, text));
        }

        return files;
    }

    /// <summary>
    /// Blanks out every comment in C# or C++ source while leaving string and
    /// character literals in place.
    /// </summary>
    /// <remarks>
    /// Comments go because the codebase documents the techniques it refuses to
    /// use, and a scanner that tripped on its own prohibition would be useless.
    /// Literals stay because a banned API reached indirectly — by name, through
    /// a string — is still the banned API.
    /// </remarks>
    /// <param name="text">The source to strip.</param>
    /// <returns>The source with comments replaced by whitespace.</returns>
    public static string RemoveComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder result = new(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && next == '*')
            {
                index += 2;
                while (index + 1 < text.Length &&
                    !(text[index] == '*' && text[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(text.Length, index + 2);
                _ = result.Append(' ');
                continue;
            }

            if (current == '@' && next == '"')
            {
                index = CopyVerbatimString(text, index, result);
                continue;
            }

            if (current is '"' or '\'')
            {
                index = CopyQuoted(text, index, result);
                continue;
            }

            _ = result.Append(current);
            index++;
        }

        return result.ToString();
    }

    private static int CopyVerbatimString(
        string text,
        int index,
        StringBuilder result)
    {
        _ = result.Append(text[index]);
        index++;
        _ = result.Append(text[index]);
        index++;

        while (index < text.Length)
        {
            if (text[index] != '"')
            {
                _ = result.Append(text[index]);
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == '"')
            {
                _ = result.Append(text[index]);
                _ = result.Append(text[index + 1]);
                index += 2;
                continue;
            }

            _ = result.Append(text[index]);
            return index + 1;
        }

        return index;
    }

    private static int CopyQuoted(string text, int index, StringBuilder result)
    {
        char quote = text[index];
        _ = result.Append(quote);
        index++;

        while (index < text.Length)
        {
            char current = text[index];
            if (current == '\\' && index + 1 < text.Length)
            {
                _ = result.Append(current);
                _ = result.Append(text[index + 1]);
                index += 2;
                continue;
            }

            _ = result.Append(current);
            index++;
            if (current == quote || current == '\n')
            {
                return index;
            }
        }

        return index;
    }

    private static bool IsAuthoredSource(string path) =>
        SourceExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase) &&
        !IsGenerated(path);

    private static bool IsGenerated(string path) =>
        ExcludedDirectorySegments.Any(
            segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase)) ||
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
}
