using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

/// <summary>
/// The complete life cycle of an Application Rule managed from the UI: created,
/// edited, duplicated, disabled, re-enabled, and deleted, with every step
/// surviving a reload, and every refusal keeping the entries that caused it.
/// </summary>
[TestClass]
public sealed class ApplicationRuleEditingTests
{
    [TestMethod]
    public async Task RuleLifecycle_SurvivesEveryStepAndAReload()
    {
        using RuleEditingTestDirectory storage = new();
        ConfigurationDocument document;

        await using (ServiceProvider provider = CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            Assert.IsTrue((await service.SaveCandidateAsync(
                ConfigurationDefaults.Create())).Accepted);
            document = service.CurrentState.Candidate;

            // Created from a draft the editor would produce.
            ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
            {
                Id = "paint",
                DisplayName = "Paint",
                TargetDesktopKey = "ide-development",
                ProcessNames = "mspaint.exe",
            };
            Assert.IsEmpty(draft.Validate(document));
            document = draft.Apply(document);
            Assert.IsTrue((await service.SaveCandidateAsync(document)).Accepted);
            Assert.HasCount(8, document.ApplicationRules);
            Assert.AreEqual("paint", document.ApplicationRules[7].Id);

            // Edited in place: the rule keeps its position, which is the
            // matcher's tie breaker.
            ApplicationRuleDraft edit = ApplicationRuleDraft.ForExistingRule(
                ApplicationRuleCatalog.Find(document, "ide-development")!) with
            {
                DisplayName = "Visual Studio Code Insiders",
                ProcessNames = $"Code.exe{Environment.NewLine}Code - Insiders.exe",
            };
            Assert.IsEmpty(edit.Validate(document));
            document = edit.Apply(document);
            Assert.IsTrue((await service.SaveCandidateAsync(document)).Accepted);
            Assert.AreEqual("ide-development", document.ApplicationRules[0].Id);
            CollectionAssert.AreEqual(
                new[] { "Code.exe", "Code - Insiders.exe" },
                document.ApplicationRules[0].ProcessNames.ToArray());

            // Duplicated: a free identifier, directly after the original, and
            // disabled so it claims nothing before it is narrowed.
            (document, ApplicationRule? duplicate) =
                ApplicationRuleCatalog.Duplicate(document, "ide-development");
            Assert.IsNotNull(duplicate);
            Assert.AreEqual("ide-development-copy", duplicate.Id);
            Assert.IsFalse(duplicate.IsEnabled);
            Assert.AreEqual("ide-development-copy", document.ApplicationRules[1].Id);
            Assert.IsTrue((await service.SaveCandidateAsync(document)).Accepted);

            // Disabled, then enabled again, without losing an identity.
            document = ApplicationRuleCatalog.SetEnabled(document, "run-observe", false);
            Assert.IsTrue((await service.SaveCandidateAsync(document)).Accepted);
            Assert.IsFalse(ApplicationRuleCatalog.Find(document, "run-observe")!.IsEnabled);

            document = ApplicationRuleCatalog.SetEnabled(document, "run-observe", true);
            Assert.IsTrue((await service.SaveCandidateAsync(document)).Accepted);
            ApplicationRule browsers =
                ApplicationRuleCatalog.Find(document, "run-observe")!;
            Assert.IsTrue(browsers.IsEnabled);
            CollectionAssert.AreEqual(
                new[] { "msedge.exe" },
                browsers.ProcessNames.ToArray());

            // Deleted.
            document = ApplicationRuleCatalog.Remove(document, "paint");
            Assert.IsTrue((await service.SaveCandidateAsync(document)).Accepted);
            Assert.IsNull(ApplicationRuleCatalog.Find(document, "paint"));
        }

        await using ServiceProvider reloaded = CreateProvider(storage);
        ConfigurationState state = await reloaded
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(state.Issues);
        Assert.IsNotNull(state.Active);
        CollectionAssert.AreEqual(
            new[]
            {
                "ide-development",
                "ide-development-copy",
                "run-observe",
                "agent-development",
                "infrastructure",
                "remote",
                "file-explorer",
                "notepad",
            },
            state.Active.ApplicationRules.Select(static rule => rule.Id).ToArray());
        Assert.AreEqual(
            "Visual Studio Code Insiders",
            state.Active.ApplicationRules[0].DisplayName);
        Assert.IsFalse(state.Active.ApplicationRules[1].IsEnabled);
    }

    [TestMethod]
    public async Task DraftEditedThroughTheUi_KeepsEveryPackagedIdentityItStartedWith()
    {
        // Loading a rule into the editor and saving it back unchanged must not
        // quietly drop the identities the editor does not put in its main field.
        using RuleEditingTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument document = ConfigurationDefaults.Create();

        ApplicationRule terminal =
            ApplicationRuleCatalog.Find(document, "infrastructure")!;
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForExistingRule(terminal);

        Assert.Contains(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            draft.PackageFamilyNames,
            StringComparison.Ordinal);
        Assert.IsEmpty(draft.Validate(document));

        ConfigurationSaveResult result =
            await service.SaveCandidateAsync(draft.Apply(document));

        Assert.IsTrue(result.Accepted);
        ApplicationRule saved = ApplicationRuleCatalog.Find(
            result.State.Active!,
            "infrastructure")!;
        CollectionAssert.AreEqual(
            terminal.PackageFamilyNames.ToArray(),
            saved.PackageFamilyNames.ToArray());
        CollectionAssert.AreEqual(
            terminal.AppUserModelIds.ToArray(),
            saved.AppUserModelIds.ToArray());
    }

    [TestMethod]
    public void DuplicateIdentifier_IsReportedAgainstTheIdentifierField()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "IDE-DEVELOPMENT",
            DisplayName = "Another editor",
            TargetDesktopKey = "ide-development",
            ProcessNames = "code.exe",
        };

        AssertIssue(
            draft.Validate(document),
            ApplicationRuleField.Id,
            ConfigurationValidationCode.DuplicateRuleId);
    }

    [TestMethod]
    public void EditedRule_MayKeepItsOwnIdentifier()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForExistingRule(
            ApplicationRuleCatalog.Find(document, "ide-development")!);

        Assert.IsEmpty(draft.Validate(document));
    }

    [TestMethod]
    public void UnknownManagedDesktop_IsReportedAgainstTheTargetField()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "paint",
            DisplayName = "Paint",
            TargetDesktopKey = "does-not-exist",
            ProcessNames = "mspaint.exe",
        };

        AssertIssue(
            draft.Validate(document),
            ApplicationRuleField.TargetDesktopKey,
            ConfigurationValidationCode.UnknownDesktopReference);
    }

    [TestMethod]
    [DataRow(
        "ProcessNames",
        @"C:\Windows\notepad.exe",
        ApplicationRuleField.ProcessNames)]
    [DataRow("ProcessNames", "note*.exe", ApplicationRuleField.ProcessNames)]
    [DataRow(
        "PackageFamilyNames",
        "Microsoft.WindowsTerminal",
        ApplicationRuleField.PackageFamilyNames)]
    [DataRow(
        "ExecutablePaths",
        @"Windows\notepad.exe",
        ApplicationRuleField.ExecutablePaths)]
    [DataRow(
        "AppUserModelIds",
        @"C:\Windows\notepad.exe",
        ApplicationRuleField.AppUserModelIds)]
    [DataRow(
        "WindowClasses",
        "Notepad*",
        ApplicationRuleField.WindowClasses)]
    public void InvalidPattern_IsReportedAgainstItsOwnFieldAndKeepsTheText(
        string fieldName,
        string value,
        ApplicationRuleField expectedField)
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = WithField(
            ApplicationRuleDraft.ForNewRule(document) with
            {
                Id = "paint",
                DisplayName = "Paint",
                TargetDesktopKey = "ide-development",
                ProcessNames = "mspaint.exe",
            },
            fieldName,
            value);

        ImmutableArray<ApplicationRuleValidationIssue> issues =
            draft.Validate(document);

        AssertIssue(
            issues,
            expectedField,
            ConfigurationValidationCode.InvalidIdentityPattern);
        Assert.Contains(
            value,
            ApplicationRuleValidation.DescribeField(issues, expectedField),
            StringComparison.Ordinal);

        // Nothing the user typed is discarded by the refusal.
        Assert.Contains(
            value,
            ReadField(draft, fieldName),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void RuleCarryingOnlyAWindowClass_IsRefusedWithAnExplanation()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "session-frames",
            DisplayName = "Session frames",
            TargetDesktopKey = "remote",
            WindowClasses = "TscShellContainerClass",
        };

        ImmutableArray<ApplicationRuleValidationIssue> issues =
            draft.Validate(document);

        AssertIssue(
            issues,
            ApplicationRuleField.ProcessNames,
            ConfigurationValidationCode.MissingApplicationIdentity);
        Assert.Contains(
            "window class names a window shape",
            ApplicationRuleValidation.DescribeField(
                issues,
                ApplicationRuleField.ProcessNames),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void RuleWithoutTriggers_IsRefusedAgainstTheTriggerField()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "paint",
            DisplayName = "Paint",
            TargetDesktopKey = "ide-development",
            ProcessNames = "mspaint.exe",
            Triggers = [],
        };

        AssertIssue(
            draft.Validate(document),
            ApplicationRuleField.Triggers,
            ConfigurationValidationCode.MissingTrigger);
    }

    [TestMethod]
    [DataRow(DesktopSwitchPolicy.OnForegroundActivation)]
    [DataRow(DesktopSwitchPolicy.OnNewWindowActivation)]
    public void SwitchPolicyTheTriggersCannotReach_IsRefused(
        DesktopSwitchPolicy policy)
    {
        // A switch is only ever considered on a foreground activation, so a rule
        // that never observes one carries a policy that can never take effect.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "paint",
            DisplayName = "Paint",
            TargetDesktopKey = "ide-development",
            ProcessNames = "mspaint.exe",
            Triggers = [ApplicationRuleTrigger.WindowCreated],
            SwitchPolicy = policy,
        };

        AssertIssue(
            draft.Validate(document),
            ApplicationRuleField.SwitchPolicy,
            ConfigurationValidationCode.UnreachableSwitchPolicy);
    }

    [TestMethod]
    public void NewWindowSwitchPolicy_NeedsAForegroundAndACreationTrigger()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft foregroundOnly =
            ApplicationRuleDraft.ForNewRule(document) with
            {
                Id = "paint",
                DisplayName = "Paint",
                TargetDesktopKey = "ide-development",
                ProcessNames = "mspaint.exe",
                Triggers = [ApplicationRuleTrigger.ForegroundActivated],
                SwitchPolicy = DesktopSwitchPolicy.OnNewWindowActivation,
            };

        AssertIssue(
            foregroundOnly.Validate(document),
            ApplicationRuleField.SwitchPolicy,
            ConfigurationValidationCode.UnreachableSwitchPolicy);

        Assert.IsEmpty(
            (foregroundOnly with
            {
                Triggers =
                [
                    ApplicationRuleTrigger.WindowCreated,
                    ApplicationRuleTrigger.ForegroundActivated,
                ],
            }).Validate(document));
    }

    [TestMethod]
    public async Task RejectedCandidate_IsStillPersistedSoNothingTypedIsLost()
    {
        using RuleEditingTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        Assert.IsTrue((await service.SaveCandidateAsync(
            ConfigurationDefaults.Create())).Accepted);

        ConfigurationDocument document = service.CurrentState.Candidate;
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "half-typed",
            DisplayName = "Half typed",
            TargetDesktopKey = "ide-development",
            ProcessNames = "mspaint.exe",
            PackageFamilyNames = "Microsoft.WindowsTerminal",
        };

        ConfigurationSaveResult result =
            await service.SaveCandidateAsync(draft.Apply(document));

        Assert.IsFalse(result.Accepted);
        Assert.HasCount(8, result.State.Candidate.ApplicationRules);
        Assert.HasCount(7, result.State.Active!.ApplicationRules);
        Assert.IsTrue(result.State.Issues.Any(static issue =>
            issue.Code == ConfigurationValidationCode.InvalidIdentityPattern));

        string candidateJson = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.candidate.json"));
        Assert.Contains(
            "Microsoft.WindowsTerminal",
            candidateJson,
            StringComparison.Ordinal);
        Assert.Contains("mspaint.exe", candidateJson, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RunningApplicationSelection_AdoptsThePackagedIdentityAndNamesTheRule()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RunningApplicationCandidate candidate = new(
            "WindowsTerminal.exe",
            @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            ["CASCADIA_HOSTING_WINDOW_CLASS"],
            2,
            Icon: null);

        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document)
            .WithRunningApplication(candidate);

        Assert.AreEqual("WindowsTerminal", draft.DisplayName);
        Assert.AreEqual("WindowsTerminal.exe", draft.ProcessNames);
        Assert.AreEqual(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            draft.PackageFamilyNames);
        Assert.AreEqual(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            draft.AppUserModelIds);

        // The narrowing signals are opt in, because narrowing is a decision.
        Assert.AreEqual(string.Empty, draft.ExecutablePaths);
        Assert.AreEqual(string.Empty, draft.WindowClasses);

        ApplicationRuleDraft narrowed = ApplicationRuleDraft.ForNewRule(document)
            .WithRunningApplication(
                candidate,
                includeExecutablePath: true,
                includeWindowClasses: true);
        Assert.AreEqual(candidate.ExecutablePath, narrowed.ExecutablePaths);
        Assert.AreEqual("CASCADIA_HOSTING_WINDOW_CLASS", narrowed.WindowClasses);
    }

    [TestMethod]
    public void RunningApplicationSelectedTwice_DoesNotDuplicateAnIdentity()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RunningApplicationCandidate candidate = new(
            "Code.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            PackageFamilyName: null,
            AppUserModelId: null,
            [],
            1,
            Icon: null);

        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document)
            .WithRunningApplication(candidate)
            .WithRunningApplication(candidate);

        Assert.AreEqual("Code.exe", draft.ProcessNames);
    }

    [TestMethod]
    public void BrowsedExecutable_AddsBothThePathAndItsFileName()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();

        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document)
            .WithExecutablePath(@"C:\Program Files\Notepad++\notepad++.exe");

        Assert.AreEqual("notepad++", draft.DisplayName);
        Assert.AreEqual(@"C:\Program Files\Notepad++\notepad++.exe", draft.ExecutablePaths);
        Assert.AreEqual("notepad++.exe", draft.ProcessNames);
    }

    [TestMethod]
    public void TriggerToggling_KeepsTheDeclaredOrder()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = (ApplicationRuleDraft.ForNewRule(document) with
        {
            Triggers = [],
        })
            .WithTrigger(ApplicationRuleTrigger.ManualReassignment, true)
            .WithTrigger(ApplicationRuleTrigger.WindowCreated, true);

        CollectionAssert.AreEqual(
            new[]
            {
                ApplicationRuleTrigger.WindowCreated,
                ApplicationRuleTrigger.ManualReassignment,
            },
            draft.Triggers.ToArray());

        Assert.IsEmpty(
            draft
                .WithTrigger(ApplicationRuleTrigger.WindowCreated, false)
                .WithTrigger(ApplicationRuleTrigger.ManualReassignment, false)
                .Triggers);
    }

    [TestMethod]
    public void DuplicatingTwice_ProducesTwoFreeIdentifiers()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();

        (document, ApplicationRule? first) =
            ApplicationRuleCatalog.Duplicate(document, "ide-development");
        (document, ApplicationRule? second) =
            ApplicationRuleCatalog.Duplicate(document, "ide-development");

        Assert.AreEqual("ide-development-copy", first!.Id);
        Assert.AreEqual("ide-development-copy-2", second!.Id);
        Assert.HasCount(9, document.ApplicationRules);
    }

    [TestMethod]
    public void OperationsOnAnUnknownRule_ChangeNothing()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();

        Assert.AreSame(document, ApplicationRuleCatalog.Remove(document, "absent"));
        Assert.AreSame(
            document,
            ApplicationRuleCatalog.SetEnabled(document, "absent", false));
        Assert.AreSame(
            document,
            ApplicationRuleCatalog.Duplicate(document, "absent").Document);
        Assert.IsNull(ApplicationRuleCatalog.Find(document, "absent"));
    }

    private static ApplicationRuleDraft WithField(
        ApplicationRuleDraft draft,
        string fieldName,
        string value) =>
        fieldName switch
        {
            "ProcessNames" => draft with { ProcessNames = value },
            "PackageFamilyNames" => draft with { PackageFamilyNames = value },
            "AppUserModelIds" => draft with { AppUserModelIds = value },
            "ExecutablePaths" => draft with { ExecutablePaths = value },
            _ => draft with { WindowClasses = value },
        };

    private static string ReadField(
        ApplicationRuleDraft draft,
        string fieldName) =>
        fieldName switch
        {
            "ProcessNames" => draft.ProcessNames,
            "PackageFamilyNames" => draft.PackageFamilyNames,
            "AppUserModelIds" => draft.AppUserModelIds,
            "ExecutablePaths" => draft.ExecutablePaths,
            _ => draft.WindowClasses,
        };

    private static void AssertIssue(
        ImmutableArray<ApplicationRuleValidationIssue> issues,
        ApplicationRuleField field,
        ConfigurationValidationCode code)
    {
        Assert.IsTrue(
            issues.Any(issue => issue.Field == field && issue.Code == code),
            $"Expected a '{code}' issue on '{field}'. Reported: {string.Join(", ", issues.Select(issue => $"{issue.Field}/{issue.Code}"))}.");
    }

    private static ServiceProvider CreateProvider(
        IConfigurationStoragePath storagePath)
    {
        ServiceCollection services = new();
        services.AddSingleton(storagePath);
        services.AddDesktopShiftFoundation();
        return services.BuildServiceProvider();
    }

    private sealed class RuleEditingTestDirectory :
        IConfigurationStoragePath,
        IDisposable
    {
        public RuleEditingTestDirectory()
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
}
