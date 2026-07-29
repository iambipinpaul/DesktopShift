using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

/// <summary>
/// The signed-in account's profile directory, as Windows reports it.
/// </summary>
/// <remarks>
/// Read once. The profile directory cannot change while the process is running,
/// and re-reading it per export would only make two calls able to disagree.
/// </remarks>
internal sealed class EnvironmentUserProfilePath : IUserProfilePath
{
    public string DirectoryPath { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
