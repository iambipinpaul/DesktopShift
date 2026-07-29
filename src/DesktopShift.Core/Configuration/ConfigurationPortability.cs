using System.Collections.Immutable;
using DesktopShift.Core.Diagnostics;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// The file an export writes and an import reads.
/// </summary>
/// <remarks>
/// <para>
/// An envelope rather than a bare document, so a file the user finds on disk
/// months later says what it is, which application wrote it, and when. The
/// configuration itself is nested unchanged, which is what lets an import accept
/// either an exported file or a raw <c>configuration.json</c> copied straight
/// out of the application folder.
/// </para>
/// <para>
/// Every member here is deliberately machine-independent. Nothing names the
/// computer, the signed-in account, a window, or a runtime virtual desktop. The
/// bindings between a Managed Desktop and the actual desktop GUID it resolved to
/// on one machine live in their own file and are never part of configuration —
/// carrying them would make an exported file valid on exactly one computer.
/// </para>
/// </remarks>
/// <param name="FormatVersion">The envelope's own version, independent of the schema.</param>
/// <param name="Application">Always <c>DesktopShift</c>.</param>
/// <param name="ExportedAtUtc">When the file was written, in UTC.</param>
/// <param name="Configuration">The configuration document itself.</param>
public sealed record PortableConfigurationEnvelope(
    int FormatVersion,
    string Application,
    DateTimeOffset ExportedAtUtc,
    ConfigurationDocument Configuration);

/// <summary>
/// Converts a configuration between the form that runs on this machine and the
/// form that can be carried to another one.
/// </summary>
/// <remarks>
/// <para>
/// One member of the document is genuinely machine-bound:
/// <see cref="ApplicationRule.ExecutablePaths"/>. A path under the signed-in
/// user's profile both names the account and points somewhere that does not
/// exist on the machine the file is carried to. It is rewritten to
/// <see cref="UserProfileToken"/> on the way out and expanded again on the way
/// in, so the rule keeps working on both machines and the exported file never
/// carries the account name.
/// </para>
/// <para>
/// Nothing else needs converting, and that is a claim worth stating rather than
/// leaving implicit: window titles, URLs, and command lines have no member on
/// any configuration type to begin with, so there is nothing to strip.
/// </para>
/// </remarks>
public static class ConfigurationPortability
{
    /// <summary>The current envelope format.</summary>
    public const int FormatVersion = 1;

    /// <summary>What a user profile directory is written as in a portable file.</summary>
    public const string UserProfileToken = DiagnosticRedaction.UserProfileToken;

    /// <summary>
    /// Rewrites a document so it can be read on another machine.
    /// </summary>
    /// <param name="document">The document as it runs here.</param>
    /// <param name="userProfilePath">
    /// This machine's profile directory. Supplied by the caller because a
    /// redirected or roamed profile does not necessarily live under
    /// <c>X:\Users</c>, and because a pure function is the only kind that can be
    /// tested without depending on whoever is signed in.
    /// </param>
    /// <returns>The portable document.</returns>
    public static ConfigurationDocument ToPortable(
        ConfigurationDocument document,
        string? userProfilePath) =>
        MapExecutablePaths(
            document,
            path => DiagnosticRedaction.Redact(path, userProfilePath) ?? path);

    /// <summary>
    /// Rewrites a portable document so it runs here.
    /// </summary>
    /// <param name="document">The document as it arrived.</param>
    /// <param name="userProfilePath">This machine's profile directory.</param>
    /// <returns>The local document.</returns>
    public static ConfigurationDocument FromPortable(
        ConfigurationDocument document,
        string? userProfilePath)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(userProfilePath))
        {
            return document;
        }

        string profile = userProfilePath.TrimEnd('\\', '/');
        return MapExecutablePaths(
            document,
            path => path.Replace(
                UserProfileToken,
                profile,
                StringComparison.OrdinalIgnoreCase));
    }

    private static ConfigurationDocument MapExecutablePaths(
        ConfigurationDocument document,
        Func<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(document);

        ImmutableArray<ApplicationRule> rules =
        [
            .. document.ApplicationRules.Select(rule =>
                rule.ExecutablePaths.IsDefaultOrEmpty
                    ? rule
                    : rule with
                    {
                        ExecutablePaths = [.. rule.ExecutablePaths.Select(map)],
                    }),
        ];

        return document with { ApplicationRules = rules };
    }
}
