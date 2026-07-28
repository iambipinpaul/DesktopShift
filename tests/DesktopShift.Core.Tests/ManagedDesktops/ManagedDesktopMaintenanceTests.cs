using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

[TestClass]
public sealed class ManagedDesktopMaintenanceTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Add_AppendsTheDefinitionInPreferredOrderAndReconciles()
    {
        Guid codeId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(codeId, "Code", 0, true));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result = await service.AddAsync(
            new ManagedDesktopDraft("web", "Web", RecreateWhenMissing: false));

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        ImmutableArray<ManagedDesktopDefinition> definitions =
            configuration.CurrentState.Active!.ManagedDesktops;
        CollectionAssert.AreEqual(
            new[] { "code", "web" },
            definitions.Select(static item => item.SemanticKey).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            definitions.Select(static item => item.PreferredOrder).ToArray());
        Assert.IsFalse(definitions[1].RecreateWhenMissing);

        // Recreation is off for the new definition, so nothing was created.
        Assert.AreEqual(0, provider.CreateCallCount);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Missing,
            reconciliation.Current.Mappings.Single(
                static mapping => mapping.SemanticKey == "web").Status);
    }

    [TestMethod]
    public async Task Add_RejectsADuplicateSemanticKeyAndWritesNothing()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result = await service.AddAsync(
            new ManagedDesktopDraft("CODE", "Second Code", RecreateWhenMissing: true));

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Rejected, result.Outcome);
        Assert.IsFalse(result.IsAccepted);
        Assert.AreEqual(0, configuration.SaveCallCount);
        Assert.HasCount(
            1,
            configuration.CurrentState.Active!.ManagedDesktops);

        ManagedDesktopValidationMessage message = result.Messages.Single();
        Assert.AreEqual("managed_desktops.edit.duplicate_key", message.Code);
        Assert.AreEqual(
            ManagedDesktopValidationScope.Configuration,
            message.Scope);
        Assert.Contains("Choose a different key", message.Remedy);
    }

    [TestMethod]
    public async Task Rename_ChangesTheDisplayNameAndKeepsTheSemanticKeyAndRules()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(
                [new ManagedDesktopDefinition("code", "Code", 1, false)],
                [CreateRule("vscode", "Visual Studio Code", "code")]));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result =
            await service.RenameAsync("code", "  Development  ");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        ManagedDesktopDefinition definition =
            configuration.CurrentState.Active!.ManagedDesktops.Single();
        Assert.AreEqual("Development", definition.DisplayName);
        Assert.AreEqual("code", definition.SemanticKey);
        Assert.AreEqual(
            "code",
            configuration.CurrentState.Active.ApplicationRules.Single().TargetDesktopKey);
    }

    [TestMethod]
    public async Task Rename_ToTheSameNameIsANoChangeAndSavesNothing()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result =
            await service.RenameAsync("code", "Code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.NoChange, result.Outcome);
        Assert.IsTrue(result.IsAccepted);
        Assert.AreEqual(0, configuration.SaveCallCount);
    }

    [TestMethod]
    public async Task Move_RenumbersPreferredOrderWithoutReorderingWindowsDesktops()
    {
        Guid codeId = Guid.NewGuid();
        Guid webId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(codeId, "Code", 0, true),
            new VirtualDesktopDescriptor(webId, "Web", 1, false));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(
                new ManagedDesktopDefinition("code", "Code", 1, false),
                new ManagedDesktopDefinition("web", "Web", 2, false),
                new ManagedDesktopDefinition("terminal", "Terminal", 3, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);
        (Guid Id, int Position)[] before =
            [.. provider.Desktops.Select(static desktop => (desktop.Id, desktop.Position))];

        ManagedDesktopMaintenanceResult result = await service.MoveAsync(
            "terminal",
            ManagedDesktopMoveDirection.Earlier);

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        ImmutableArray<ManagedDesktopDefinition> definitions =
            configuration.CurrentState.Active!.ManagedDesktops;
        CollectionAssert.AreEqual(
            new[] { "code", "terminal", "web" },
            definitions.Select(static item => item.SemanticKey).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3 },
            definitions.Select(static item => item.PreferredOrder).ToArray());

        // Preferred order is a configuration preference. The user's real
        // desktops keep their own order and their own identity.
        CollectionAssert.AreEqual(
            before,
            provider.Desktops
                .Select(static desktop => (desktop.Id, desktop.Position))
                .ToArray());
    }

    [TestMethod]
    public async Task Move_PastTheEndIsANoChange()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(
                new ManagedDesktopDefinition("code", "Code", 1, false),
                new ManagedDesktopDefinition("web", "Web", 2, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result = await service.MoveAsync(
            "code",
            ManagedDesktopMoveDirection.Earlier);

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.NoChange, result.Outcome);
        Assert.AreEqual(0, configuration.SaveCallCount);
    }

    [TestMethod]
    public async Task SetRecreationPolicy_TurnsRecreationOffAndOnAgain()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult disabled =
            await service.SetRecreationPolicyAsync("code", recreateWhenMissing: false);
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, disabled.Outcome);
        Assert.IsFalse(
            configuration.CurrentState.Active!.ManagedDesktops.Single().RecreateWhenMissing);

        ManagedDesktopMaintenanceResult unchanged =
            await service.SetRecreationPolicyAsync("code", recreateWhenMissing: false);
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.NoChange, unchanged.Outcome);

        ManagedDesktopMaintenanceResult enabled =
            await service.SetRecreationPolicyAsync("code", recreateWhenMissing: true);
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, enabled.Outcome);
        Assert.IsTrue(
            configuration.CurrentState.Active!.ManagedDesktops.Single().RecreateWhenMissing);
    }

    [TestMethod]
    public async Task RemoveFromManagement_LeavesTheRealWindowsDesktopUntouched()
    {
        Guid codeId = Guid.NewGuid();
        Guid webId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(codeId, "Code", 0, true),
            new VirtualDesktopDescriptor(webId, "Web", 1, false));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(
                new ManagedDesktopDefinition("code", "Code", 1, true),
                new ManagedDesktopDefinition("web", "Web", 2, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);
        _ = await service.ReconcileAllAsync();

        ManagedDesktopMaintenanceResult result =
            await service.RemoveFromManagementAsync("web");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        Assert.AreEqual(
            "code",
            configuration.CurrentState.Active!.ManagedDesktops.Single().SemanticKey);

        // The safety line of this feature: management stopped, the desktop did
        // not. It is still in the inventory, at its position, and no mutating
        // provider call was made at any point.
        Assert.HasCount(2, provider.Desktops);
        Assert.IsTrue(provider.Desktops.Any(desktop => desktop.Id == webId));
        Assert.AreEqual(
            1,
            provider.Desktops.Single(desktop => desktop.Id == webId).Position);
        Assert.AreEqual(0, provider.MutatingCallCount);

        // The runtime binding is dropped, so the desktop is no longer claimed.
        Assert.AreEqual("code", store.Current.Single().SemanticKey);
        Assert.Contains("was left in place", result.Summary);
    }

    [TestMethod]
    public async Task RemoveFromManagement_RefusesWhileApplicationRulesStillTargetIt()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(
                [new ManagedDesktopDefinition("code", "Code", 1, true)],
                [
                    CreateRule("vscode", "Visual Studio Code", "code"),
                    CreateRule("rider", "JetBrains Rider", "code"),
                ]));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result =
            await service.RemoveFromManagementAsync("code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Rejected, result.Outcome);
        Assert.AreEqual(0, configuration.SaveCallCount);
        Assert.HasCount(1, configuration.CurrentState.Active!.ManagedDesktops);

        ManagedDesktopValidationMessage message = result.Messages.Single();
        Assert.AreEqual("managed_desktops.edit.desktop_in_use", message.Code);
        Assert.Contains("2 Application Rules", message.Message);
        Assert.Contains("Visual Studio Code", message.Message);
        Assert.Contains("Rules page", message.Remedy);
        Assert.AreEqual(0, provider.MutatingCallCount);
    }

    [TestMethod]
    public async Task Recreate_CreatesTheMissingDesktopEvenWhenRecreationIsDisabled()
    {
        Guid unrelatedId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(unrelatedId, "Personal", 0, true));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult missing = await service.ReconcileAllAsync();
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Partial, missing.Outcome);
        Assert.AreEqual(1, missing.Report!.UnresolvedCount);

        ManagedDesktopMaintenanceResult result = await service.RecreateAsync("code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        Assert.AreEqual(1, provider.CreateCallCount);
        Assert.AreEqual(1, result.Report!.CreatedCount);
        Assert.Contains("Created 1 desktop", result.Report.Summary);

        ManagedDesktopRuntimeMapping mapping = reconciliation.Current.Mappings.Single();
        Assert.IsTrue(mapping.IsBound);
        Assert.AreEqual(provider.CreatedIds.Single(), mapping.RuntimeDesktopId);
        Assert.AreEqual(
            provider.CreatedIds.Single(),
            store.Current.Single(binding => binding.SemanticKey == "code").RuntimeDesktopId);

        // The desktop that was already there is untouched.
        Assert.IsTrue(provider.Desktops.Any(desktop => desktop.Id == unrelatedId));
    }

    [TestMethod]
    public async Task Recreate_IsIdempotentOnceTheDesktopIsBound()
    {
        Guid codeId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(codeId, "Code", 0, true));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result = await service.RecreateAsync("code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.NoChange, result.Outcome);
        Assert.AreEqual(0, provider.CreateCallCount);
        Assert.Contains("already mapped", result.Summary);
    }

    [TestMethod]
    public async Task Recreate_AfterTheBoundDesktopWasDeletedCreatesExactlyOneReplacement()
    {
        Guid codeId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(codeId, "Code", 0, true));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);
        _ = await service.ReconcileAllAsync();

        provider.SimulateUserDeleted(codeId);
        ManagedDesktopMaintenanceResult result = await service.RecreateAsync("code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        Assert.AreEqual(1, provider.CreateCallCount);
        Assert.AreEqual(
            provider.CreatedIds.Single(),
            reconciliation.Current.Mappings.Single().RuntimeDesktopId);
        Assert.HasCount(1, provider.Desktops);
    }

    [TestMethod]
    public async Task Recreate_RefusesWhenMoreThanOneWindowsDesktopAlreadyMatches()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(Guid.NewGuid(), "Code", 0, true),
            new VirtualDesktopDescriptor(Guid.NewGuid(), "code", 1, false));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, false)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result = await service.RecreateAsync("code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Rejected, result.Outcome);
        Assert.AreEqual(0, provider.CreateCallCount);

        ManagedDesktopValidationMessage message = result.Messages.Single(
            static item => item.Scope == ManagedDesktopValidationScope.Runtime);
        Assert.AreEqual("managed_desktops.compatible_match_ambiguous", message.Code);
        Assert.Contains("2 Windows desktops are named 'Code'", message.Message);
        Assert.Contains("Task View", message.Remedy);
    }

    [TestMethod]
    public async Task ReconcileAll_ReportsWhatItDidAndIsIdempotent()
    {
        Guid codeId = Guid.NewGuid();
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full(
            new VirtualDesktopDescriptor(codeId, "Code", 0, true));
        MaintenanceConfigurationService configuration = new(
            CreateDocument(
                new ManagedDesktopDefinition("code", "Code", 1, true),
                new ManagedDesktopDefinition("web", "Web", 2, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult first = await service.ReconcileAllAsync();

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, first.Outcome);
        Assert.AreEqual(1, first.Report!.MatchedCount);
        Assert.AreEqual(1, first.Report.CreatedCount);
        Assert.AreEqual(0, first.Report.UnresolvedCount);

        ManagedDesktopMaintenanceResult second = await service.ReconcileAllAsync();

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.NoChange, second.Outcome);
        Assert.IsFalse(second.Report!.ChangedAnything);
        Assert.AreEqual(2, second.Report.LeftAloneCount);
        Assert.AreEqual("Left 2 already-mapped desktops alone.", second.Report.Summary);
        Assert.AreEqual(1, provider.CreateCallCount);
    }

    [TestMethod]
    public async Task LimitedMode_DisablesRecreateAndReconcileWithAStatedReason()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Limited();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopCatalog catalog = service.GetCatalog(provider.CreateState());

        Assert.IsFalse(catalog.Availability.CanRecreate);
        Assert.IsFalse(catalog.Availability.CanReconcile);
        Assert.Contains(
            "cannot enumerate Windows desktops",
            catalog.Availability.ReconcileUnavailableReason);
        Assert.Contains(
            "cannot create Windows desktops",
            catalog.Availability.RecreateUnavailableReason);
        Assert.IsFalse(catalog.Entries.Single().CanRecreate);

        ManagedDesktopMaintenanceResult recreate = await service.RecreateAsync("code");
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Limited, recreate.Outcome);
        Assert.AreEqual(0, provider.CreateCallCount);
        Assert.Contains("Settings page", recreate.Messages.Single().Remedy);

        ManagedDesktopMaintenanceResult reconcile = await service.ReconcileAllAsync();
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Limited, reconcile.Outcome);
        Assert.AreEqual(0, provider.MutatingCallCount);
    }

    [TestMethod]
    public async Task LimitedMode_StillAcceptsDefinitionEdits()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Limited();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, true)));
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult added = await service.AddAsync(
            new ManagedDesktopDraft("web", "Web", RecreateWhenMissing: true));
        ManagedDesktopMaintenanceResult renamed =
            await service.RenameAsync("code", "Development");
        ManagedDesktopMaintenanceResult removed =
            await service.RemoveFromManagementAsync("web");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, added.Outcome);
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, renamed.Outcome);
        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, removed.Outcome);
        Assert.AreEqual(
            "Development",
            configuration.CurrentState.Active!.ManagedDesktops.Single().DisplayName);
        Assert.AreEqual(0, provider.MutatingCallCount);
    }

    [TestMethod]
    public async Task RejectedSave_SurfacesTheConfigurationValidatorsFindings()
    {
        MaintenanceTopologyProvider provider = MaintenanceTopologyProvider.Full();
        MaintenanceConfigurationService configuration = new(
            CreateDocument(new ManagedDesktopDefinition("code", "Code", 1, true)))
        {
            Validate = static _ =>
            [
                new ConfigurationValidationIssue(
                    ConfigurationValidationCode.DuplicateDesktopSemanticKey,
                    "Managed Desktop semantic key 'code' is duplicated.",
                    "$.managedDesktops[1].semanticKey",
                    ConfigurationEntryKind.ManagedDesktop,
                    "code"),
            ],
        };
        using ManagedDesktopReconciliationService reconciliation =
            CreateReconciliation(configuration, provider, out MaintenanceBindingStore store);
        ManagedDesktopMaintenanceService service =
            new(configuration, reconciliation, provider, store);

        ManagedDesktopMaintenanceResult result =
            await service.RenameAsync("code", "Development");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Rejected, result.Outcome);

        ManagedDesktopValidationMessage message = result.Messages.Single();
        Assert.AreEqual("managed_desktops.duplicate_semantic_key", message.Code);
        Assert.AreEqual(ManagedDesktopValidationScope.Configuration, message.Scope);
        Assert.Contains("ambiguous", message.Remedy);
    }

    private static ManagedDesktopReconciliationService CreateReconciliation(
        MaintenanceConfigurationService configuration,
        MaintenanceTopologyProvider provider,
        out MaintenanceBindingStore store)
    {
        store = new MaintenanceBindingStore();
        return new ManagedDesktopReconciliationService(
            configuration,
            provider,
            store,
            new MaintenanceTimeProvider(ObservedAt));
    }

    private static ConfigurationDocument CreateDocument(
        params ManagedDesktopDefinition[] definitions) =>
        CreateDocument(definitions, []);

    private static ConfigurationDocument CreateDocument(
        IEnumerable<ManagedDesktopDefinition> definitions,
        IEnumerable<ApplicationRule> rules) =>
        new(
            ConfigurationDefaults.CurrentSchemaVersion,
            [.. definitions],
            [.. rules],
            new BehaviorSettings(false, false, false));

    private static ApplicationRule CreateRule(
        string id,
        string displayName,
        string targetDesktopKey) =>
        new(
            id,
            displayName,
            IsEnabled: true,
            targetDesktopKey,
            [$"{id}.exe"],
            [ApplicationRuleTrigger.WindowCreated],
            DesktopSwitchPolicy.Never);
}
