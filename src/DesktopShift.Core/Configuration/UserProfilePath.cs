namespace DesktopShift.Core.Configuration;

/// <summary>
/// This machine's user profile directory, as configuration portability needs to
/// see it.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a direct call to <see cref="Environment"/> for the same
/// reason <see cref="IConfigurationStoragePath"/> is one: an export has to
/// rewrite paths under the signed-in account's profile, and a test that proved
/// it did so by reading the machine's real profile would prove something
/// different on every machine it ran on — and nothing at all on a build agent
/// whose profile directory happens to be somewhere no rule points.
/// </para>
/// <para>
/// It is also what makes "carry this file to another computer" testable at all.
/// Importing with a different value here is exactly what a different machine
/// looks like from the configuration's point of view.
/// </para>
/// </remarks>
public interface IUserProfilePath
{
    /// <summary>The profile directory, without a trailing separator.</summary>
    string DirectoryPath { get; }
}
