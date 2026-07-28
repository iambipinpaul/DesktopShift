using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class ConfigurationAcceptanceTests
{
    [TestMethod]
    public void Defaults_ContainTheExactInitialMappingsAndBehavior()
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        Assert.AreEqual(1, defaults.SchemaVersion);
        CollectionAssert.AreEqual(
            new[] { "code", "web", "terminal", "remote" },
            defaults.ManagedDesktops.Select(static desktop => desktop.SemanticKey).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Code", "Web", "Terminal", "Remote" },
            defaults.ManagedDesktops.Select(static desktop => desktop.DisplayName).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3, 4 },
            defaults.ManagedDesktops.Select(static desktop => desktop.PreferredOrder).ToArray());
        Assert.IsTrue(defaults.ManagedDesktops.All(static desktop => desktop.RecreateWhenMissing));

        AssertRule(
            defaults.ApplicationRules[0],
            "vscode",
            "Visual Studio Code",
            "code",
            ["Code.exe"]);
        AssertRule(
            defaults.ApplicationRules[1],
            "browsers",
            "Microsoft Edge and Google Chrome",
            "web",
            ["msedge.exe", "chrome.exe"]);
        AssertRule(
            defaults.ApplicationRules[2],
            "windows-terminal",
            "Windows Terminal",
            "terminal",
            ["WindowsTerminal.exe", "wt.exe"]);
        AssertRule(
            defaults.ApplicationRules[3],
            "remote-desktop",
            "Remote Desktop Connection",
            "remote",
            ["mstsc.exe"]);

        Assert.IsTrue(defaults.Behavior.StartWithWindows);
        Assert.IsTrue(defaults.Behavior.StartMinimized);
        Assert.IsTrue(defaults.Behavior.CloseToTray);
    }

    [TestMethod]
    public async Task Validation_ReportsDuplicatesAndUnknownReferencesWithoutDiscardingEntries()
    {
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        ConfigurationDocument invalid = defaults with
        {
            ManagedDesktops = defaults.ManagedDesktops.Add(
                new ManagedDesktopDefinition("CODE", "Duplicate Code", 4, true)),
            ApplicationRules = defaults.ApplicationRules.Add(
                defaults.ApplicationRules[0] with
                {
                    TargetDesktopKey = "missing",
                }),
        };

        ConfigurationSaveResult result =
            await service.SaveCandidateAsync(invalid);

        Assert.IsFalse(result.Accepted);
        Assert.IsNull(result.State.Active);
        Assert.AreEqual(5, result.State.Candidate.ManagedDesktops.Length);
        Assert.AreEqual(5, result.State.Candidate.ApplicationRules.Length);
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.DuplicateDesktopSemanticKey,
            "CODE");
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.DuplicateRuleId,
            "vscode");
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.UnknownDesktopReference,
            "vscode");

        string candidateJson = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.candidate.json"));
        Assert.Contains("Duplicate Code", candidateJson, StringComparison.Ordinal);
        Assert.Contains("\"missing\"", candidateJson, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SuccessfulFirstRun_PersistsChoicesUpdatesCountsAndDoesNotRepeat()
    {
        using ConfigurationTestDirectory storage = new();
        DateTimeOffset expectedTime =
            new(2026, 7, 28, 7, 30, 0, TimeSpan.Zero);

        await using (ServiceProvider provider =
            CreateProvider(storage, new FixedTimeProvider(expectedTime)))
        {
            IFirstRunService firstRun =
                provider.GetRequiredService<IFirstRunService>();
            FirstRunState initial = await firstRun.GetStateAsync();

            Assert.IsFalse(initial.IsCompleted);
            Assert.HasCount(4, initial.Candidate.ManagedDesktops);
            Assert.HasCount(4, initial.Candidate.ApplicationRules);

            ImmutableArray<ApplicationRule> editedRules =
                initial.Candidate.ApplicationRules.SetItem(
                    1,
                    initial.Candidate.ApplicationRules[1] with
                    {
                        IsEnabled = false,
                    });
            ConfigurationDocument edited =
                initial.Candidate with
                {
                    ManagedDesktops = initial.Candidate.ManagedDesktops.SetItem(
                        1,
                        initial.Candidate.ManagedDesktops[1] with
                        {
                            DisplayName = "Browsing",
                        }),
                    ApplicationRules = editedRules,
                };

            FirstRunCompletionResult completed =
                await firstRun.CompleteAsync(edited, startWithWindows: true);

            Assert.IsTrue(completed.Accepted);
            Assert.IsTrue(completed.State.IsCompleted);
            Assert.IsTrue(completed.State.StartWithWindows);
            Assert.AreEqual(expectedTime, provider
                .GetRequiredService<IConfigurationService>()
                .CurrentState
                .ObservedAtUtc);

            ConfigurationOverview overview = provider
                .GetRequiredService<IOverviewConfigurationProjection>()
                .GetSnapshot();
            Assert.AreEqual(4, overview.ManagedDesktopCount);
            Assert.AreEqual(3, overview.EnabledRuleCount);
        }

        string json = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"));
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"startWithWindows\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"onForegroundActivation\"", json, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, json, StringComparison.Ordinal);

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        IFirstRunService reloadedFirstRun =
            reloadedProvider.GetRequiredService<IFirstRunService>();
        FirstRunState reloaded = await reloadedFirstRun.GetStateAsync();

        Assert.IsTrue(reloaded.IsCompleted);
        Assert.IsTrue(reloaded.StartWithWindows);
        Assert.AreEqual(
            "Browsing",
            reloaded.Candidate.ManagedDesktops[1].DisplayName);
        Assert.IsFalse(reloaded.Candidate.ApplicationRules[1].IsEnabled);
        Assert.IsEmpty(reloaded.Issues);
    }

    [TestMethod]
    public async Task InvalidCandidate_RemainsEditableWhileLastValidConfigurationStaysActive()
    {
        using ConfigurationTestDirectory storage = new();
        await using (ServiceProvider provider = CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            ConfigurationSaveResult accepted =
                await service.SaveCandidateAsync(ConfigurationDefaults.Create());
            Assert.IsTrue(accepted.Accepted);

            ConfigurationDocument invalid =
                accepted.State.Candidate with
                {
                    ApplicationRules =
                        accepted.State.Candidate.ApplicationRules.Add(
                            accepted.State.Candidate.ApplicationRules[0] with
                            {
                                DisplayName = "Candidate duplicate",
                                TargetDesktopKey = "does-not-exist",
                            }),
                };

            ConfigurationSaveResult rejected =
                await service.SaveCandidateAsync(invalid);

            Assert.IsFalse(rejected.Accepted);
            Assert.HasCount(5, rejected.State.Candidate.ApplicationRules);
            Assert.HasCount(4, rejected.State.Active!.ApplicationRules);
            Assert.AreEqual(
                "Candidate duplicate",
                rejected.State.Candidate.ApplicationRules[4].DisplayName);
            AssertHasIssue(
                rejected.State.Issues,
                ConfigurationValidationCode.DuplicateRuleId,
                "vscode");
            AssertHasIssue(
                rejected.State.Issues,
                ConfigurationValidationCode.UnknownDesktopReference,
                "vscode");
        }

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        ConfigurationState reloaded = await reloadedProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.HasCount(5, reloaded.Candidate.ApplicationRules);
        Assert.HasCount(4, reloaded.Active!.ApplicationRules);
        Assert.AreEqual(
            "Candidate duplicate",
            reloaded.Candidate.ApplicationRules[4].DisplayName);
        AssertHasIssue(
            reloaded.Issues,
            ConfigurationValidationCode.UnknownDesktopReference,
            "vscode");
    }

    [TestMethod]
    public async Task CorruptActiveAndInterruptedTemporaryWrite_RecoverLastValidSnapshot()
    {
        using ConfigurationTestDirectory storage = new();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ConfigurationDocument newer = defaults with
        {
            ManagedDesktops = defaults.ManagedDesktops.SetItem(
                0,
                defaults.ManagedDesktops[0] with
                {
                    DisplayName = "Code Workspace",
                }),
        };

        await using (ServiceProvider provider = CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            Assert.IsTrue((await service.SaveCandidateAsync(defaults)).Accepted);
            Assert.IsTrue((await service.SaveCandidateAsync(newer)).Accepted);
        }

        string activePath =
            Path.Combine(storage.DirectoryPath, "configuration.json");
        await File.WriteAllTextAsync(activePath, "{ not valid JSON");
        await File.WriteAllTextAsync(
            $"{activePath}.tmp",
            "{\"schemaVersion\":1");

        await using ServiceProvider recoveredProvider = CreateProvider(storage);
        ConfigurationState recovered = await recoveredProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsNotNull(recovered.Active);
        Assert.AreEqual("Code", recovered.Active.ManagedDesktops[0].DisplayName);
        Assert.AreEqual(
            "Code Workspace",
            recovered.Candidate.ManagedDesktops[0].DisplayName);
        AssertHasIssue(
            recovered.Issues,
            ConfigurationValidationCode.ActiveSnapshotUnreadable);
        AssertHasIssue(
            recovered.Issues,
            ConfigurationValidationCode.RecoveredLastValidSnapshot);
    }

    [TestMethod]
    public async Task CorruptCandidate_FallsBackToActiveWithoutRepeatingFirstRun()
    {
        using ConfigurationTestDirectory storage = new();

        await using (ServiceProvider provider = CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            Assert.IsTrue((await service.SaveCandidateAsync(
                ConfigurationDefaults.Create())).Accepted);
        }

        await File.WriteAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.candidate.json"),
            "{ interrupted");

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        ConfigurationState state = await reloadedProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsFalse(state.IsFirstRun);
        Assert.IsNotNull(state.Active);
        Assert.HasCount(4, state.Candidate.ApplicationRules);
        AssertHasIssue(
            state.Issues,
            ConfigurationValidationCode.CandidateUnreadable);
    }

    [TestMethod]
    public async Task CanceledSave_DoesNotReplaceActiveConfiguration()
    {
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        Assert.IsTrue((await service.SaveCandidateAsync(defaults)).Accepted);

        ConfigurationDocument edited = defaults with
        {
            ManagedDesktops = defaults.ManagedDesktops.SetItem(
                0,
                defaults.ManagedDesktops[0] with
                {
                    DisplayName = "Canceled edit",
                }),
        };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SaveCandidateAsync(edited, cancellation.Token));

        Assert.AreEqual(
            "Code",
            service.CurrentState.Active!.ManagedDesktops[0].DisplayName);
    }

    private static ServiceProvider CreateProvider(
        IConfigurationStoragePath storagePath,
        TimeProvider? timeProvider = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(storagePath);
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddDesktopShiftFoundation();
        return services.BuildServiceProvider();
    }

    private static void AssertRule(
        ApplicationRule rule,
        string id,
        string displayName,
        string targetDesktopKey,
        string[] processNames)
    {
        Assert.AreEqual(id, rule.Id);
        Assert.AreEqual(displayName, rule.DisplayName);
        Assert.IsTrue(rule.IsEnabled);
        Assert.AreEqual(targetDesktopKey, rule.TargetDesktopKey);
        CollectionAssert.AreEqual(processNames, rule.ProcessNames.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ApplicationRuleTrigger.WindowCreated,
                ApplicationRuleTrigger.WindowShown,
                ApplicationRuleTrigger.ForegroundActivated,
                ApplicationRuleTrigger.StartupReconciliation,
            },
            rule.Triggers.ToArray());
        Assert.AreEqual(
            DesktopSwitchPolicy.OnForegroundActivation,
            rule.SwitchPolicy);
        Assert.DoesNotContain(
            ApplicationRuleTrigger.ManualReassignment,
            rule.Triggers);
    }

    private static void AssertHasIssue(
        ImmutableArray<ConfigurationValidationIssue> issues,
        ConfigurationValidationCode code,
        string? entryId = null)
    {
        Assert.IsTrue(
            issues.Any(issue =>
                issue.Code == code &&
                (entryId is null || issue.EntryId == entryId)),
            $"Expected issue '{code}' for entry '{entryId ?? "(any)"}'.");
    }

    private sealed class ConfigurationTestDirectory :
        IConfigurationStoragePath,
        IDisposable
    {
        public ConfigurationTestDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "DesktopShift.Tests",
                Guid.NewGuid().ToString("N"));
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
