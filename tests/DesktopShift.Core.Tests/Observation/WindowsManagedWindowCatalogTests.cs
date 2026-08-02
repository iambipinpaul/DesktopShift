using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Observation;

[TestClass]
public sealed class WindowsManagedWindowCatalogTests
{
    [TestMethod]
    public void Entries_ExplainTheImmutableWindowsManagedGroups()
    {
        WindowsManagedWindowEntry credentials =
            WindowsManagedWindowCatalog.Entries.Single(
                static entry => entry.Id == "credentials-and-passkeys");

        Assert.AreEqual("Credentials and passkeys", credentials.DisplayName);
        Assert.Contains("CredentialUIBroker.exe", credentials.ProcessNames);
        Assert.Contains("stay where Windows opens", credentials.Description);
        Assert.IsTrue(WindowsManagedWindowCatalog.IsManagedProcessName(
            "CREDENTIALUIBROKER.EXE"));
        WindowsManagedWindowEntry builtIn =
            WindowsManagedWindowCatalog.Entries.Single(
                static entry => entry.Id == "built-in-windows-tools");
        Assert.AreEqual("Built-in Windows tools", builtIn.DisplayName);
        Assert.Contains("SystemSettings.exe", builtIn.ProcessNames);
        Assert.IsTrue(WindowsManagedWindowCatalog.IsManagedProcessName(
            "systemsettings.exe"));
        Assert.IsTrue(WindowsManagedWindowCatalog.Entries.All(
            static entry =>
                !string.IsNullOrWhiteSpace(entry.DisplayName) &&
                !string.IsNullOrWhiteSpace(entry.Description) &&
                !entry.ProcessNames.IsDefaultOrEmpty));
    }
}
