using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class ConfigurationAcceptanceTests
{
    // A schema version 1 document written before packageFamilyNames,
    // appUserModelIds, executablePaths and windowClasses existed. Every one of
    // them is absent on purpose.
    private const string LegacyDocumentJson = """
        {
          "schemaVersion": 1,
          "managedDesktops": [
            {
              "semanticKey": "terminal",
              "displayName": "Terminal",
              "preferredOrder": 1,
              "recreateWhenMissing": true
            }
          ],
          "applicationRules": [
            {
              "id": "windows-terminal",
              "displayName": "Windows Terminal",
              "isEnabled": true,
              "targetDesktopKey": "terminal",
              "processNames": [ "WindowsTerminal.exe" ],
              "triggers": [ "windowCreated", "foregroundActivated" ],
              "switchPolicy": "onForegroundActivation"
            }
          ],
          "behavior": {
            "startWithWindows": true,
            "startMinimized": true,
            "closeToTray": true
          }
        }
        """;

    [TestMethod]
    public void Defaults_ContainTheExactInitialMappingsAndBehavior()
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        Assert.AreEqual(1, defaults.SchemaVersion);
        CollectionAssert.AreEqual(
            new[]
            {
                "ide-development",
                "run-observe",
                "agent-development",
                "infrastructure",
                "remote",
            },
            defaults.ManagedDesktops.Select(static desktop => desktop.SemanticKey).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "IDE Development",
                "Run & Observe",
                "Agent Development",
                "Infrastructure",
                "Remote",
            },
            defaults.ManagedDesktops.Select(static desktop => desktop.DisplayName).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3, 4, 5 },
            defaults.ManagedDesktops.Select(static desktop => desktop.PreferredOrder).ToArray());
        Assert.IsTrue(defaults.ManagedDesktops.All(static desktop => desktop.RecreateWhenMissing));

        AssertRule(
            defaults.ApplicationRules[0],
            "ide-development",
            "IDE Development",
            "ide-development",
            ["Code.exe", "devenv.exe"]);
        AssertRule(
            defaults.ApplicationRules[1],
            "run-observe",
            "Run & Observe",
            "run-observe",
            ["msedge.exe"]);
        AssertRule(
            defaults.ApplicationRules[2],
            "agent-development",
            "Agent Development",
            "agent-development",
            ["claude.exe", "ChatGPT.exe", "codex.exe"]);
        AssertRule(
            defaults.ApplicationRules[3],
            "infrastructure",
            "Infrastructure",
            "infrastructure",
            ["WindowsTerminal.exe"],
            [
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
            ],
            [
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe!App",
            ]);
        AssertRule(
            defaults.ApplicationRules[4],
            "remote",
            "Remote",
            "remote",
            ["mstsc.exe"]);

        Assert.IsTrue(defaults.Behavior.StartWithWindows);
        Assert.IsTrue(defaults.Behavior.StartMinimized);
        Assert.IsTrue(defaults.Behavior.CloseToTray);

        // Desktop switching still ships off. Ten global combinations must not be
        // claimed before the user asks for them.
        Assert.IsFalse(defaults.Behavior.AreDesktopSwitchShortcutsEnabled);
    }

    [TestMethod]
    public void Defaults_ExemptTheShippedSystemUtilitiesFromTheSweep()
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ImmutableArray<ApplicationRule> anywhere =
        [
            .. defaults.ApplicationRules.Where(static rule => rule.AllowsAnywhere),
        ];

        // Two, and only two. Every shipped Anywhere rule unmanages an
        // application for everybody who installs DesktopShift, so the list is
        // kept to the ones opened from wherever the user already is and expected
        // to stay there.
        CollectionAssert.AreEqual(
            new[] { "file-explorer", "notepad" },
            anywhere.Select(static rule => rule.Id).ToArray());
        Assert.IsTrue(anywhere.All(static rule => rule.IsEnabled));

        Assert.Contains(
            "explorer.exe",
            GetDefaultRule("file-explorer").ProcessNames);
        Assert.Contains(
            @"C:\Windows\explorer.exe",
            GetDefaultRule("file-explorer").ExecutablePaths);
        Assert.Contains(
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe",
            GetDefaultRule("notepad").PackageFamilyNames);
        Assert.Contains("Notepad.exe", GetDefaultRule("notepad").ProcessNames);
    }

    [TestMethod]
    public void Defaults_ShipNoExemptionTheUserDidNotAskFor()
    {
        // Calculator, Task Manager, Settings, Paint, and Photos were all
        // verified to exist and none is shipped. An exemption the user added
        // unmanages an application for them; one shipped in the defaults
        // unmanages it for everybody, and none of these five is opened often
        // enough from an arbitrary desktop to earn that.
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        foreach (string identity in new[]
        {
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            "Microsoft.Paint_8wekyb3d8bbwe",
            "Microsoft.Windows.Photos_8wekyb3d8bbwe",
        })
        {
            Assert.IsFalse(
                defaults.ApplicationRules.Any(
                    rule => rule.PackageFamilyNames.Contains(identity)),
                $"'{identity}' is exempted for every user by default.");
        }

        foreach (string path in new[]
        {
            @"C:\Windows\System32\Taskmgr.exe",
            @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe",
        })
        {
            Assert.IsFalse(
                defaults.ApplicationRules.Any(
                    rule => rule.ExecutablePaths.Contains(path)),
                $"'{path}' is exempted for every user by default.");
        }
    }

    [TestMethod]
    public void DefaultMoveRules_CarryEveryTriggerIncludingManualReassignment()
    {
        foreach (ApplicationRule rule in ConfigurationDefaults.Create()
            .ApplicationRules
            .Where(static rule => !rule.AllowsAnywhere))
        {
            CollectionAssert.AreEquivalent(
                ConfigurationDefaults.DefaultTriggers.ToArray(),
                rule.Triggers.ToArray(),
                $"'{rule.Id}' does not declare every trigger.");
        }
    }

    [TestMethod]
    public void DefaultInfrastructureRule_DropsTheWindowlessConsoleHosts()
    {
        ApplicationRule rule = GetDefaultRule("infrastructure");

        // Neither owns a top-level window: wt.exe forwards its command line to
        // the running host and exits, and OpenConsole.exe is the windowless
        // ConPTY host. Both could only ever be dead configuration.
        Assert.DoesNotContain("wt.exe", rule.ProcessNames);
        Assert.DoesNotContain("OpenConsole.exe", rule.ProcessNames);
    }

    [TestMethod]
    [DataRow("Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [DataRow("Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe")]
    public void DefaultTerminalRule_IsIdentifiedByPackagedIdentity(
        string packageFamilyName)
    {
        ApplicationRule rule = GetDefaultRule("infrastructure");

        Assert.Contains(packageFamilyName, rule.PackageFamilyNames);
        Assert.Contains($"{packageFamilyName}!App", rule.AppUserModelIds);
    }

    [TestMethod]
    public void DefaultTerminalRule_TreatsTheLauncherStubAsALaunchSignalOnly()
    {
        ApplicationRule rule = GetDefaultRule("infrastructure");

        Assert.DoesNotContain("wt.exe", rule.ProcessNames);
        Assert.HasCount(1, rule.ProcessNames);
        Assert.Contains("WindowsTerminal.exe", rule.ProcessNames);
    }

    [TestMethod]
    public async Task PackagedIdentities_SurviveAPersistAndReloadRoundTrip()
    {
        using ConfigurationTestDirectory storage = new();

        await using (ServiceProvider provider = CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            Assert.IsTrue((await service.SaveCandidateAsync(
                ConfigurationDefaults.Create())).Accepted);
        }

        string json = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"));
        Assert.Contains(
            "\"packageFamilyNames\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            json,
            StringComparison.Ordinal);

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        ConfigurationState reloaded = await reloadedProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(reloaded.Issues);
        ApplicationRule terminal = reloaded.Active!.ApplicationRules.Single(
            static rule => rule.Id == "infrastructure");
        CollectionAssert.AreEqual(
            new[]
            {
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
            },
            terminal.PackageFamilyNames.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe!App",
            },
            terminal.AppUserModelIds.ToArray());
        ApplicationRule vscode = reloaded.Active.ApplicationRules.Single(
            static rule => rule.Id == "ide-development");
        Assert.IsEmpty(vscode.PackageFamilyNames);
        Assert.IsEmpty(vscode.AppUserModelIds);
    }

    [TestMethod]
    public async Task ExistingDocumentWithoutPackagedIdentities_StillLoads()
    {
        using ConfigurationTestDirectory storage = new();
        Directory.CreateDirectory(storage.DirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"),
            LegacyDocumentJson);

        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationState state = await service.LoadAsync();

        Assert.IsEmpty(state.Issues);
        Assert.IsNotNull(state.Active);
        ApplicationRule rule = state.Active.ApplicationRules[0];
        Assert.AreEqual("windows-terminal", rule.Id);
        CollectionAssert.AreEqual(
            new[] { "WindowsTerminal.exe" },
            rule.ProcessNames.ToArray());
        Assert.IsEmpty(rule.PackageFamilyNames);
        Assert.IsEmpty(rule.AppUserModelIds);
        Assert.IsEmpty(rule.ExecutablePaths);
        Assert.IsEmpty(rule.WindowClasses);

        // An absent action reads as moveToDesktop, which is exactly how such a
        // document behaved before the member existed — so the schema version did
        // not have to move.
        Assert.AreEqual(ApplicationRuleAction.MoveToDesktop, rule.Action);
        Assert.IsFalse(rule.AllowsAnywhere);
        // The document declared version 1 and is current as it stands: adding
        // action did not move the schema version, so nothing was migrated.
        Assert.AreEqual(1, state.Active.SchemaVersion);
        Assert.AreEqual(
            ConfigurationDefaults.CurrentSchemaVersion,
            state.Active.SchemaVersion);
        Assert.DoesNotContain(
            "action",
            LegacyDocumentJson,
            StringComparison.Ordinal);

        // The absent collections must read as empty, not as default arrays: a
        // default array cannot be enumerated and would fail to serialize.
        Assert.IsTrue(
            (await service.SaveCandidateAsync(state.Candidate)).Accepted);
    }

    [TestMethod]
    public async Task AnAnywhereRule_SurvivesAPersistAndReloadRoundTrip()
    {
        using ConfigurationTestDirectory storage = new();

        await using (ServiceProvider provider = CreateProvider(storage))
        {
            Assert.IsTrue((await provider
                .GetRequiredService<IConfigurationService>()
                .SaveCandidateAsync(ConfigurationDefaults.Create())).Accepted);
        }

        string json = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"));
        Assert.Contains("\"allowAnywhere\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        ConfigurationState reloaded = await reloadedProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(reloaded.Issues);
        ApplicationRule explorer = reloaded.Active!.ApplicationRules.Single(
            static rule => rule.Id == "file-explorer");
        Assert.AreEqual(ApplicationRuleAction.AllowAnywhere, explorer.Action);
        Assert.IsTrue(explorer.AllowsAnywhere);

        // Its ignored destination survives untouched rather than being
        // normalized to a sentinel that could collide with a real key.
        Assert.IsEmpty(explorer.TargetDesktopKey);
        Assert.AreEqual(
            ApplicationRuleAction.MoveToDesktop,
            reloaded.Active.ApplicationRules
                .Single(static rule => rule.Id == "ide-development")
                .Action);
    }

    [TestMethod]
    public async Task AnAnywhereRuleWithNoDestinationOrTrigger_IsStillAccepted()
    {
        // Its target key, triggers, and switch policy are never read, so none of
        // them may be reported against it.
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        ConfigurationSaveResult result = await provider
            .GetRequiredService<IConfigurationService>()
            .SaveCandidateAsync(defaults with
            {
                ApplicationRules = defaults.ApplicationRules.Add(
                    new ApplicationRule(
                        "paint",
                        "Paint",
                        IsEnabled: true,
                        TargetDesktopKey: string.Empty,
                        ["mspaint.exe"],
                        Triggers: [],
                        DesktopSwitchPolicy.OnNewWindowActivation,
                        Action: ApplicationRuleAction.AllowAnywhere)),
            });

        Assert.IsTrue(result.Accepted);
        Assert.IsEmpty(result.State.Issues);
    }

    [TestMethod]
    public async Task AMoveRuleWithNoDestination_IsStillReported()
    {
        // The exemption is for Anywhere rules only. A rule that says it moves
        // windows still has to say where.
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        ConfigurationSaveResult result = await provider
            .GetRequiredService<IConfigurationService>()
            .SaveCandidateAsync(defaults with
            {
                ApplicationRules = defaults.ApplicationRules.SetItem(
                    0,
                    defaults.ApplicationRules[0] with
                    {
                        TargetDesktopKey = string.Empty,
                    }),
            });

        Assert.IsFalse(result.Accepted);
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.RequiredValue,
            "ide-development");
    }

    [TestMethod]
    public void DefaultRemoteDesktopRule_IsIdentifiedByTheClassicClientOnly()
    {
        ApplicationRule rule = GetDefaultRule("remote");

        Assert.AreEqual("remote", rule.TargetDesktopKey);
        CollectionAssert.AreEqual(
            new[] { "mstsc.exe" },
            rule.ProcessNames.ToArray());

        // msrdc.exe is both the Azure Virtual Desktop client and the WSL client
        // that hosts every WSLg Linux window, so naming it would claim windows
        // that are not remote sessions.
        Assert.DoesNotContain("msrdc.exe", rule.ProcessNames);

        // No window class is declared, so every surface mstsc.exe shows — the
        // connection dialog, the session frame, the connection bar — is claimed
        // rather than only the one shape a class would name.
        Assert.IsEmpty(rule.WindowClasses);

        // No packaged identity is claimed for the Store or Windows App clients:
        // an unverified package family name is dead configuration.
        Assert.IsEmpty(rule.PackageFamilyNames);
        Assert.IsEmpty(rule.AppUserModelIds);
        Assert.IsEmpty(rule.ExecutablePaths);
    }

    [TestMethod]
    public async Task WindowClassAndExecutablePathRefinements_SurviveARoundTrip()
    {
        // Before these members existed a document could not narrow a rule to a
        // window shape at all. The narrowing has to survive being written and
        // read back, or the rule silently widens on the next launch.
        using ConfigurationTestDirectory storage = new();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule remoteDesktop = defaults.ApplicationRules.Single(
            static rule => rule.Id == "remote");
        ConfigurationDocument refined = defaults with
        {
            ApplicationRules = defaults.ApplicationRules.Replace(
                remoteDesktop,
                remoteDesktop with
                {
                    WindowClasses = ["TscShellContainerClass"],
                    ExecutablePaths = [@"C:\Windows\System32\mstsc.exe"],
                }),
        };

        await using (ServiceProvider provider = CreateProvider(storage))
        {
            Assert.IsTrue((await provider
                .GetRequiredService<IConfigurationService>()
                .SaveCandidateAsync(refined)).Accepted);
        }

        string json = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"));
        Assert.Contains("\"windowClasses\"", json, StringComparison.Ordinal);
        Assert.Contains("\"executablePaths\"", json, StringComparison.Ordinal);
        Assert.Contains(
            "TscShellContainerClass",
            json,
            StringComparison.Ordinal);

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        ConfigurationState reloaded = await reloadedProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(reloaded.Issues);
        ApplicationRule reloadedRule = reloaded.Active!.ApplicationRules.Single(
            static rule => rule.Id == "remote");
        CollectionAssert.AreEqual(
            new[] { "TscShellContainerClass" },
            reloadedRule.WindowClasses.ToArray());
        CollectionAssert.AreEqual(
            new[] { @"C:\Windows\System32\mstsc.exe" },
            reloadedRule.ExecutablePaths.ToArray());

        // A rule that declares neither still reads as empty, not as a default
        // array that cannot be enumerated.
        ApplicationRule vscode = reloaded.Active.ApplicationRules.Single(
            static rule => rule.Id == "ide-development");
        Assert.IsEmpty(vscode.WindowClasses);
        Assert.IsEmpty(vscode.ExecutablePaths);
    }

    [TestMethod]
    public async Task RuleIdentifiedOnlyByExecutablePath_IsAccepted()
    {
        // A path is a primary identity: it says which application the window
        // belongs to, and it distinguishes two installations that share a name.
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule remoteDesktop = defaults.ApplicationRules.Single(
            static rule => rule.Id == "remote");

        ConfigurationSaveResult result = await provider
            .GetRequiredService<IConfigurationService>()
            .SaveCandidateAsync(defaults with
            {
                ApplicationRules = defaults.ApplicationRules.Replace(
                    remoteDesktop,
                    remoteDesktop with
                    {
                        ProcessNames = [],
                        ExecutablePaths = [@"C:\Windows\System32\mstsc.exe"],
                    }),
            });

        Assert.IsTrue(result.Accepted);
        Assert.IsEmpty(result.State.Issues);
    }

    [TestMethod]
    public async Task RuleIdentifiedOnlyByWindowClass_IsRejected()
    {
        // A window class names a shape, never an application. A rule carrying
        // only classes would claim that shape from every process that draws it.
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule remoteDesktop = defaults.ApplicationRules.Single(
            static rule => rule.Id == "remote");

        ConfigurationSaveResult result = await provider
            .GetRequiredService<IConfigurationService>()
            .SaveCandidateAsync(defaults with
            {
                ApplicationRules = defaults.ApplicationRules.Replace(
                    remoteDesktop,
                    remoteDesktop with
                    {
                        ProcessNames = [],
                        WindowClasses = ["TscShellContainerClass"],
                    }),
            });

        Assert.IsFalse(result.Accepted);
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.MissingApplicationIdentity,
            "remote");
    }

    [TestMethod]
    public async Task RuleIdentifiedOnlyByPackageFamilyName_IsAccepted()
    {
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        ConfigurationDocument packagedOnly = defaults with
        {
            ApplicationRules = defaults.ApplicationRules.SetItem(
                3,
                defaults.ApplicationRules[3] with
                {
                    ProcessNames = [],
                    AppUserModelIds = [],
                }),
        };

        ConfigurationSaveResult result =
            await service.SaveCandidateAsync(packagedOnly);

        Assert.IsTrue(result.Accepted);
        Assert.IsEmpty(result.State.Issues);
        Assert.IsEmpty(result.State.Active!.ApplicationRules[3].ProcessNames);
        Assert.HasCount(
            2,
            result.State.Active.ApplicationRules[3].PackageFamilyNames);
    }

    [TestMethod]
    public async Task RuleWithoutAnyIdentity_IsRejected()
    {
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        ConfigurationDocument unidentified = defaults with
        {
            ApplicationRules = defaults.ApplicationRules.SetItem(
                3,
                defaults.ApplicationRules[3] with
                {
                    ProcessNames = [],
                    PackageFamilyNames = [],
                    AppUserModelIds = [],
                }),
        };

        ConfigurationSaveResult result =
            await service.SaveCandidateAsync(unidentified);

        Assert.IsFalse(result.Accepted);
        Assert.IsNull(result.State.Active);
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.MissingApplicationIdentity,
            "infrastructure");
    }

    [TestMethod]
    [DataRow("processNames")]
    [DataRow("packageFamilyNames")]
    [DataRow("appUserModelIds")]
    [DataRow("executablePaths")]
    [DataRow("windowClasses")]
    public async Task RuleWithABlankIdentityEntry_IsRejectedAtThatList(
        string listName)
    {
        using ConfigurationTestDirectory storage = new();
        await using ServiceProvider provider = CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule original = defaults.ApplicationRules[2];
        ApplicationRule blanked = listName switch
        {
            "processNames" => original with
            {
                ProcessNames = original.ProcessNames.Add("   "),
            },
            "packageFamilyNames" => original with
            {
                PackageFamilyNames = original.PackageFamilyNames.Add("   "),
            },
            "executablePaths" => original with
            {
                ExecutablePaths = original.ExecutablePaths.Add("   "),
            },
            "windowClasses" => original with
            {
                WindowClasses = original.WindowClasses.Add("   "),
            },
            _ => original with
            {
                AppUserModelIds = original.AppUserModelIds.Add("   "),
            },
        };

        ConfigurationSaveResult result = await service.SaveCandidateAsync(
            defaults with
            {
                ApplicationRules =
                    defaults.ApplicationRules.SetItem(2, blanked),
            });

        Assert.IsFalse(result.Accepted);
        Assert.IsTrue(
            result.State.Issues.Any(issue =>
                issue.Code ==
                    ConfigurationValidationCode.MissingApplicationIdentity &&
                issue.Path == $"$.applicationRules[2].{listName}"),
            $"Expected a missing identity issue at '$.applicationRules[2].{listName}'.");
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
                new ManagedDesktopDefinition(
                    "IDE-DEVELOPMENT",
                    "Duplicate Code",
                    4,
                    true)),
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
        Assert.AreEqual(6, result.State.Candidate.ManagedDesktops.Length);
        Assert.AreEqual(8, result.State.Candidate.ApplicationRules.Length);
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.DuplicateDesktopSemanticKey,
            "IDE-DEVELOPMENT");
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.DuplicateRuleId,
            "ide-development");
        AssertHasIssue(
            result.State.Issues,
            ConfigurationValidationCode.UnknownDesktopReference,
            "ide-development");

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
            Assert.HasCount(5, initial.Candidate.ManagedDesktops);
            Assert.HasCount(7, initial.Candidate.ApplicationRules);

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
            Assert.AreEqual(5, overview.ManagedDesktopCount);
            Assert.AreEqual(6, overview.EnabledRuleCount);
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
            Assert.HasCount(8, rejected.State.Candidate.ApplicationRules);
            Assert.HasCount(7, rejected.State.Active!.ApplicationRules);
            Assert.AreEqual(
                "Candidate duplicate",
                rejected.State.Candidate.ApplicationRules[7].DisplayName);
            AssertHasIssue(
                rejected.State.Issues,
                ConfigurationValidationCode.DuplicateRuleId,
                "ide-development");
            AssertHasIssue(
                rejected.State.Issues,
                ConfigurationValidationCode.UnknownDesktopReference,
                "ide-development");
        }

        await using ServiceProvider reloadedProvider = CreateProvider(storage);
        ConfigurationState reloaded = await reloadedProvider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.HasCount(8, reloaded.Candidate.ApplicationRules);
        Assert.HasCount(7, reloaded.Active!.ApplicationRules);
        Assert.AreEqual(
            "Candidate duplicate",
            reloaded.Candidate.ApplicationRules[7].DisplayName);
        AssertHasIssue(
            reloaded.Issues,
            ConfigurationValidationCode.UnknownDesktopReference,
            "ide-development");
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
        Assert.AreEqual("IDE Development", recovered.Active.ManagedDesktops[0].DisplayName);
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
        Assert.HasCount(7, state.Candidate.ApplicationRules);
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
            "IDE Development",
            service.CurrentState.Active!.ManagedDesktops[0].DisplayName);
    }

    private static ApplicationRule GetDefaultRule(string id)
    {
        return ConfigurationDefaults.Create().ApplicationRules.Single(
            rule => rule.Id == id);
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
        string[] processNames,
        string[]? packageFamilyNames = null,
        string[]? appUserModelIds = null)
    {
        Assert.AreEqual(id, rule.Id);
        Assert.AreEqual(displayName, rule.DisplayName);
        Assert.IsTrue(rule.IsEnabled);
        Assert.AreEqual(targetDesktopKey, rule.TargetDesktopKey);
        CollectionAssert.AreEqual(processNames, rule.ProcessNames.ToArray());
        CollectionAssert.AreEqual(
            packageFamilyNames ?? [],
            rule.PackageFamilyNames.ToArray());
        CollectionAssert.AreEqual(
            appUserModelIds ?? [],
            rule.AppUserModelIds.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ApplicationRuleTrigger.WindowCreated,
                ApplicationRuleTrigger.WindowShown,
                ApplicationRuleTrigger.ForegroundActivated,
                ApplicationRuleTrigger.StartupReconciliation,
                ApplicationRuleTrigger.ManualReassignment,
            },
            rule.Triggers.ToArray());
        Assert.AreEqual(
            DesktopSwitchPolicy.OnForegroundActivation,
            rule.SwitchPolicy);
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
