using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// The shape every Application Rule value has to have before the matcher can do
/// anything with it.
/// </summary>
/// <remarks>
/// <para>
/// The matcher compares each identity verbatim against a signal the identity
/// resolver read from the running process, so a value that cannot be produced by
/// that resolver is dead configuration: it is not a rule that matches nothing
/// today and something tomorrow, it is a rule that can never match. A pattern
/// test here is therefore a correctness test, not a style preference.
/// </para>
/// <para>
/// The same tests are used by the editor, which reports them against the field a
/// user typed into, and by the document validator, which reports them against a
/// JSON path. Keeping one implementation is what stops the editor from accepting
/// a rule the document validator would reject on save.
/// </para>
/// </remarks>
public static class ApplicationRuleShape
{
    /// <summary>
    /// The buffer <c>GetClassName</c> is read into, so a longer class could
    /// never be observed on a window.
    /// </summary>
    public const int MaximumWindowClassLength = 256;

    private static readonly char[] EntrySeparators = ['\n', '\r', ',', ';'];

    private static readonly char[] WildcardCharacters = ['*', '?'];

    private static readonly char[] InvalidFileNameCharacters =
        Path.GetInvalidFileNameChars();

    private static readonly char[] InvalidPathCharacters =
        Path.GetInvalidPathChars();

    /// <summary>
    /// Splits the free text of one editor field into identity entries.
    /// </summary>
    /// <remarks>
    /// Newlines, commas, and semicolons all separate entries so a user can paste
    /// a list in whatever shape they already have it. Blank entries are dropped
    /// rather than reported, because a trailing newline is not a mistake worth
    /// interrupting an edit for; a blank entry that survives trimming is still
    /// reported by the document validator.
    /// </remarks>
    /// <param name="text">The raw text of one field.</param>
    /// <returns>The trimmed, non-empty entries in the order they were typed.</returns>
    public static ImmutableArray<string> ParseEntries(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return [.. text
            .Split(EntrySeparators, StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Renders stored entries back into the one-per-line text an editor field
    /// shows.
    /// </summary>
    /// <param name="entries">The stored entries.</param>
    /// <returns>One entry per line.</returns>
    public static string FormatEntries(ImmutableArray<string> entries) =>
        entries.IsDefaultOrEmpty
            ? string.Empty
            : string.Join(Environment.NewLine, entries);

    /// <summary>
    /// Tests a rule identifier. The identifier is a stable key: it appears in
    /// every activity record, in the JSON document, and in the duplicate test,
    /// so it may not carry whitespace or characters that make two rules look
    /// alike.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns>Whether the identifier can be used.</returns>
    public static bool IsValidRuleId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');

    /// <summary>
    /// Tests a process name. The resolver reports
    /// <c>Path.GetFileName(executablePath)</c>, so anything carrying a
    /// directory separator or a wildcard can never equal what is observed.
    /// </summary>
    /// <param name="value">The candidate process name.</param>
    /// <returns>Whether the process name can ever match.</returns>
    public static bool IsValidProcessName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(InvalidFileNameCharacters) < 0 &&
        value.IndexOfAny(WildcardCharacters) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Tests an executable path. The resolver reports the full image path, so a
    /// relative path or a wildcard can never equal what is observed.
    /// </summary>
    /// <param name="value">The candidate executable path.</param>
    /// <returns>Whether the executable path can ever match.</returns>
    public static bool IsValidExecutablePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(InvalidPathCharacters) < 0 &&
        value.IndexOfAny(WildcardCharacters) < 0 &&
        Path.IsPathFullyQualified(value);

    /// <summary>
    /// Tests a package family name, whose documented shape is
    /// <c>Name_PublisherId</c>. A value without the publisher half is a package
    /// name, which no running process ever reports.
    /// </summary>
    /// <param name="value">The candidate package family name.</param>
    /// <returns>Whether the package family name can ever match.</returns>
    public static bool IsValidPackageFamilyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        int separator = value.IndexOf('_', StringComparison.Ordinal);
        return separator > 0 &&
            separator == value.LastIndexOf('_') &&
            separator < value.Length - 1;
    }

    /// <summary>
    /// Tests an AppUserModelId. Packaged identities look like
    /// <c>PackageFamilyName!ApplicationId</c>, but an unpackaged application may
    /// declare any string it likes, so only the mistakes that are certainly
    /// mistakes are rejected: an empty value and a file system path.
    /// </summary>
    /// <param name="value">The candidate AppUserModelId.</param>
    /// <returns>Whether the AppUserModelId can ever match.</returns>
    public static bool IsValidAppUserModelId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Tests a window class. A class is compared for equality, never as a
    /// pattern, and cannot be longer than the buffer it is read into.
    /// </summary>
    /// <param name="value">The candidate window class.</param>
    /// <returns>Whether the window class can ever match.</returns>
    public static bool IsValidWindowClass(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumWindowClassLength &&
        value.IndexOfAny(WildcardCharacters) < 0;

    /// <summary>
    /// Tests whether a switch policy can ever be reached by the triggers a rule
    /// declares.
    /// </summary>
    /// <remarks>
    /// A desktop switch is only ever considered on a foreground activation, so
    /// any policy other than <see cref="DesktopSwitchPolicy.Never"/> needs the
    /// <see cref="ApplicationRuleTrigger.ForegroundActivated"/> trigger.
    /// <see cref="DesktopSwitchPolicy.OnNewWindowActivation"/> additionally only
    /// applies to the first activation of a window the rule already matched
    /// while it was being created or shown, so it also needs one of those two
    /// triggers. Without them the policy is a setting that can never take
    /// effect.
    /// </remarks>
    /// <param name="policy">The declared switch policy.</param>
    /// <param name="triggers">The declared triggers.</param>
    /// <returns>Whether the policy can ever take effect.</returns>
    public static bool IsSwitchPolicyReachable(
        DesktopSwitchPolicy policy,
        ImmutableArray<ApplicationRuleTrigger> triggers)
    {
        if (policy == DesktopSwitchPolicy.Never)
        {
            return true;
        }

        if (triggers.IsDefaultOrEmpty ||
            !triggers.Contains(ApplicationRuleTrigger.ForegroundActivated))
        {
            return false;
        }

        return policy != DesktopSwitchPolicy.OnNewWindowActivation ||
            triggers.Contains(ApplicationRuleTrigger.WindowCreated) ||
            triggers.Contains(ApplicationRuleTrigger.WindowShown);
    }

    /// <summary>
    /// Explains, in the words of the triggers a user can turn on, why a switch
    /// policy can never take effect.
    /// </summary>
    /// <param name="policy">The unreachable switch policy.</param>
    /// <returns>The explanation shown beside the switch policy control.</returns>
    public static string DescribeUnreachableSwitchPolicy(
        DesktopSwitchPolicy policy) =>
        policy == DesktopSwitchPolicy.OnNewWindowActivation
            ? "Switching on a new window needs the Foreground activated trigger and either the Window created or the Window shown trigger."
            : "Switching on foreground activation needs the Foreground activated trigger.";
}
