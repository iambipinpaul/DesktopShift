using System.Text;
using System.Text.Json.Nodes;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class ConfigurationExportTests
{
    [TestMethod]
    public async Task Export_WritesTheActivePortableHumanReadableDocument()
    {
        using TestConfigurationDirectory storage = new();
        DateTimeOffset exportedAt =
            new(2026, 7, 29, 8, 15, 0, TimeSpan.Zero);
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(
                storage,
                timeProvider: new Issue17FixedTimeProvider(exportedAt));
        IConfigurationService configuration =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule vscode = defaults.ApplicationRules.Single(
            static rule => rule.Id == "ide-development");
        ConfigurationDocument active = defaults with
        {
            ApplicationRules = defaults.ApplicationRules.Replace(
                vscode,
                vscode with
                {
                    ExecutablePaths =
                    [
                        @"C:\Users\Alice\Apps\Code.exe",
                        @"D:\Portable\Code.exe",
                    ],
                }),
            Behavior = defaults.Behavior with
            {
                Theme = AppTheme.Dark,
                AreHotkeysEnabled = true,
                Hotkeys = HotkeyDefaults.Bindings,
            },
        };
        Assert.IsTrue(
            (await configuration.SaveCandidateAsync(active)).Accepted);

        // Leave an invalid candidate on screen. Export must still describe what
        // the application is actually running.
        ConfigurationDocument invalidCandidate = active with
        {
            ManagedDesktops =
            [
                active.ManagedDesktops[0],
                active.ManagedDesktops[0],
                .. active.ManagedDesktops.Skip(2),
            ],
        };
        Assert.IsFalse(
            (await configuration.SaveCandidateAsync(invalidCandidate)).Accepted);

        await using MemoryStream destination = new();
        ConfigurationExportResult result = await provider
            .GetRequiredService<IConfigurationExchangeService>()
            .ExportAsync(destination);
        string json = Encoding.UTF8.GetString(destination.ToArray());
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonObject document = root["configuration"]!.AsObject();

        Assert.AreEqual(ConfigurationPortability.FormatVersion, root["formatVersion"]!.GetValue<int>());
        Assert.AreEqual("DesktopShift", root["application"]!.GetValue<string>());
        Assert.AreEqual(exportedAt, root["exportedAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.Contains(Environment.NewLine, json);
        Assert.Contains("\"theme\": \"dark\"", json, StringComparison.Ordinal);
        Assert.Contains(ConfigurationPortability.UserProfileToken, json);
        Assert.DoesNotContain(@"C:\Users\Alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"D:\\Portable\\Code.exe", json, StringComparison.Ordinal);
        Assert.AreEqual(active.ManagedDesktops.Length, result.ManagedDesktopCount);
        Assert.AreEqual(active.ApplicationRules.Length, result.ApplicationRuleCount);
        Assert.AreEqual(active.Behavior.Hotkeys.Length, result.HotkeyCount);
        Assert.AreEqual(destination.Length, result.ByteCount);

        // The duplicate candidate never leaks into the export.
        Assert.AreEqual(
            active.ManagedDesktops.Length,
            document["managedDesktops"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task Export_ContainsNoRuntimeDesktopOrReconciliationMetadata()
    {
        using TestConfigurationDirectory storage = new();
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService configuration =
            provider.GetRequiredService<IConfigurationService>();
        Assert.IsTrue(
            (await configuration.SaveCandidateAsync(ConfigurationDefaults.Create()))
            .Accepted);

        await using MemoryStream destination = new();
        await provider.GetRequiredService<IConfigurationExchangeService>()
            .ExportAsync(destination);
        string json = Encoding.UTF8.GetString(destination.ToArray());

        Assert.DoesNotContain("resolvedDesktopId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("desktopGuid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("topologyReason", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machineName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windowHandle", json, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Export_FallsBackToTheCandidateBeforeFirstRunIsAccepted()
    {
        using TestConfigurationDirectory storage = new();
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService configuration =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationState firstRun = await configuration.LoadAsync();
        Assert.IsNull(firstRun.Active);

        await using MemoryStream destination = new();
        ConfigurationExportResult result = await provider
            .GetRequiredService<IConfigurationExchangeService>()
            .ExportAsync(destination);

        Assert.AreEqual(firstRun.Candidate.ManagedDesktops.Length, result.ManagedDesktopCount);
        Assert.AreEqual(firstRun.Candidate.ApplicationRules.Length, result.ApplicationRuleCount);
        Assert.IsGreaterThan(0, result.ByteCount);
    }
}
