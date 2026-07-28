using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal sealed class LocalAppDataConfigurationStoragePath : IConfigurationStoragePath
{
    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopShift");
}
