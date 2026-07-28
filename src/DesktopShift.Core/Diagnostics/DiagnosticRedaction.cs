using System.Text.RegularExpressions;

namespace DesktopShift.Core.Diagnostics;

/// <summary>
/// Removes user-identifying path segments from text that leaves the process.
/// </summary>
/// <remarks>
/// <para>
/// Titles, browser URLs, and command lines are kept out structurally rather
/// than scrubbed: <see cref="ActivityRecord"/> carries
/// <see cref="Observation.WindowSafeIdentity"/>, which has no member for them.
/// Redaction covers what remains — the file system paths that name the signed-in
/// account, such as a log location under the user's profile.
/// </para>
/// <para>
/// The rewrite is deliberately conservative. It replaces the profile directory,
/// never the rest of the path, so a bundle still tells a maintainer which file
/// a message came from.
/// </para>
/// </remarks>
public static partial class DiagnosticRedaction
{
    /// <summary>The token a redacted user profile directory is replaced by.</summary>
    public const string UserProfileToken = "%USERPROFILE%";

    /// <summary>
    /// Replaces every <c>X:\Users\&lt;name&gt;</c> prefix with
    /// <see cref="UserProfileToken"/>. Shared profiles, which name no user, are
    /// left intact.
    /// </summary>
    /// <param name="value">The text to redact.</param>
    /// <returns>The redacted text, or null when the input was null.</returns>
    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return UserProfilePattern().Replace(value, UserProfileToken);
    }

    /// <summary>
    /// Redacts the standard profile pattern and, additionally, an explicitly
    /// supplied profile directory. A redirected or roamed profile does not
    /// necessarily live under <c>X:\Users</c>, so the caller that knows the real
    /// location passes it here.
    /// </summary>
    /// <param name="value">The text to redact.</param>
    /// <param name="userProfilePath">The profile directory to replace.</param>
    /// <returns>The redacted text, or null when the input was null.</returns>
    public static string? Redact(string? value, string? userProfilePath)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string redacted = value;
        if (!string.IsNullOrWhiteSpace(userProfilePath))
        {
            redacted = redacted.Replace(
                userProfilePath.TrimEnd('\\', '/'),
                UserProfileToken,
                StringComparison.OrdinalIgnoreCase);
        }

        return Redact(redacted);
    }

    // Separators are matched one or two at a time because the text being
    // redacted is often already JSON, where a path arrives with its
    // backslashes escaped. "Public", "Default", "Default User", and
    // "All Users" are shared profiles; they identify no one, so replacing them
    // would only make a bundle harder to read without protecting anything.
    [GeneratedRegex(
        @"[A-Za-z]:[\\/]{1,2}Users[\\/]{1,2}(?!(?:Public|Default|Default User|All Users)(?:[\\/]{1,2}|$))[^\\/:*?""<>|\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePattern();
}
