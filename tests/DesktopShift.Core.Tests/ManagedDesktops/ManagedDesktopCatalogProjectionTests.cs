using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

[TestClass]
public sealed class ManagedDesktopCatalogProjectionTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void Project_ShowsPositionNameKeyRuntimeStateRuleCountAndRecreationPolicy()
    {
        Guid codeId = Guid.NewGuid();
        ConfigurationDocument configuration = new(
            ConfigurationDefaults.CurrentSchemaVersion,
            [
                new ManagedDesktopDefinition("web", "Web", 2, false),
                new ManagedDesktopDefinition("code", "Code", 1, true),
            ],
            [
                CreateRule("vscode", "code", isEnabled: true),
                CreateRule("rider", "CODE", isEnabled: false),
                CreateRule("edge", "web", isEnabled: true),
            ],
            new BehaviorSettings(false, false, false));
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Partial,
            Bound("code", "Code", 1, true, codeId, "Code", 0),
            Unbound(
                "web",
                "Web",
                2,
                false,
                ManagedDesktopMappingStatus.Missing,
                "managed_desktops.missing_recreation_disabled",
                "No compatible runtime desktop exists and recreation is disabled."));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            snapshot,
            [],
            CreateProviderState());

        CollectionAssert.AreEqual(
            new[] { "code", "web" },
            catalog.Entries.Select(static entry => entry.SemanticKey).ToArray());

        ManagedDesktopCatalogEntry code = catalog.Entries[0];
        Assert.AreEqual(1, code.PreferredOrder);
        Assert.AreEqual("Code", code.DisplayName);
        Assert.IsTrue(code.RecreateWhenMissing);
        Assert.AreEqual(2, code.AssignedRuleCount);
        Assert.AreEqual(1, code.EnabledRuleCount);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.ReusedPersistedBinding,
            code.RuntimeStatus);
        Assert.AreEqual(codeId, code.RuntimeDesktopId);
        Assert.AreEqual(0, code.RuntimePosition);
        Assert.IsTrue(code.IsBound);
        Assert.IsFalse(code.CanMoveEarlier);
        Assert.IsTrue(code.CanMoveLater);

        ManagedDesktopCatalogEntry web = catalog.Entries[1];
        Assert.AreEqual(1, web.AssignedRuleCount);
        Assert.IsFalse(web.RecreateWhenMissing);
        Assert.AreEqual(ManagedDesktopMappingStatus.Missing, web.RuntimeStatus);
        Assert.IsTrue(web.NeedsAttention);
        Assert.IsTrue(web.CanMoveEarlier);
        Assert.IsFalse(web.CanMoveLater);

        Assert.AreEqual(1, catalog.BoundCount);
        Assert.AreEqual(1, catalog.AttentionCount);
        Assert.Contains("2 managed desktops", catalog.Summary);
    }

    [TestMethod]
    public void Project_OffersRecreateOnlyForDefinitionsThatAreNotBound()
    {
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("code", "Code", 1, true),
            new ManagedDesktopDefinition("web", "Web", 2, false));
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Partial,
            Bound("code", "Code", 1, true, Guid.NewGuid(), "Code", 0),
            Unbound(
                "web",
                "Web",
                2,
                false,
                ManagedDesktopMappingStatus.Missing,
                "managed_desktops.missing_recreation_disabled",
                "No compatible runtime desktop exists and recreation is disabled."));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            snapshot,
            [],
            CreateProviderState());

        Assert.IsFalse(catalog.Entries[0].CanRecreate);
        Assert.Contains("already mapped", catalog.Entries[0].RecreateUnavailableReason);
        Assert.IsTrue(catalog.Entries[1].CanRecreate);
        Assert.AreEqual(string.Empty, catalog.Entries[1].RecreateUnavailableReason);
    }

    [TestMethod]
    public void Project_ReportsADuplicateKeyAndARuntimeAmbiguityAsSeparateProblems()
    {
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("code", "Code", 1, true));
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Partial,
            Unbound(
                "code",
                "Code",
                1,
                true,
                ManagedDesktopMappingStatus.Ambiguous,
                "managed_desktops.compatible_match_ambiguous",
                "More than one unclaimed runtime desktop has the configured display name; no desktop was selected or created.",
                [Guid.NewGuid(), Guid.NewGuid()]));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            snapshot,
            [
                new ConfigurationValidationIssue(
                    ConfigurationValidationCode.DuplicateDesktopSemanticKey,
                    "Managed Desktop semantic key 'code' is duplicated.",
                    "$.managedDesktops[1].semanticKey",
                    ConfigurationEntryKind.ManagedDesktop,
                    "code"),
            ],
            CreateProviderState());

        ManagedDesktopValidationMessage duplicate = catalog.Messages.Single(
            static message => message.Scope == ManagedDesktopValidationScope.Configuration);
        Assert.AreEqual("managed_desktops.duplicate_semantic_key", duplicate.Code);
        Assert.Contains("different key", duplicate.Remedy);

        ManagedDesktopValidationMessage ambiguity = catalog.Messages.Single(
            static message => message.Scope == ManagedDesktopValidationScope.Runtime);
        Assert.AreEqual("managed_desktops.compatible_match_ambiguous", ambiguity.Code);
        Assert.Contains("2 Windows desktops are named 'Code'", ambiguity.Message);
        Assert.Contains("Task View", ambiguity.Remedy);
        Assert.AreEqual("code", ambiguity.SemanticKey);
    }

    [TestMethod]
    public void Project_ReportsARuleTargetingAMissingManagedDesktop()
    {
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("code", "Code", 1, true));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            ManagedDesktopReconciliationSnapshot.NotRun,
            [
                new ConfigurationValidationIssue(
                    ConfigurationValidationCode.UnknownDesktopReference,
                    "Application Rule 'edge' references unknown Managed Desktop 'web'.",
                    "$.applicationRules[0].targetDesktopKey",
                    ConfigurationEntryKind.ApplicationRule,
                    "edge"),
            ],
            CreateProviderState());

        ManagedDesktopValidationMessage message = catalog.Messages.First(
            static item => item.Code == "managed_desktops.unknown_desktop_reference");
        Assert.AreEqual(ManagedDesktopValidationScope.Configuration, message.Scope);
        Assert.Contains("Rules page", message.Remedy);
    }

    [TestMethod]
    public void Project_WithoutConfigurationFallsBackToTheSnapshotAndHidesRuleCounts()
    {
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Succeeded,
            Bound("code", "Code", 1, true, Guid.NewGuid(), "Code", 0));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration: null,
            snapshot,
            [],
            CreateProviderState());

        ManagedDesktopCatalogEntry entry = catalog.Entries.Single();
        Assert.AreEqual("code", entry.SemanticKey);
        Assert.IsNull(entry.AssignedRuleCount);
        Assert.IsNull(entry.EnabledRuleCount);
        Assert.IsTrue(entry.IsBound);
    }

    [TestMethod]
    public void Project_BeforeTheCompatibilityCheckHoldsRuntimeOperationsWithAReason()
    {
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("code", "Code", 1, true));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            ManagedDesktopReconciliationSnapshot.NotRun,
            [],
            providerState: null);

        Assert.IsFalse(catalog.Availability.CanRecreate);
        Assert.IsFalse(catalog.Availability.CanReconcile);
        Assert.Contains(
            "compatibility check",
            catalog.Availability.ReconcileUnavailableReason);
        Assert.IsFalse(catalog.Entries.Single().CanRecreate);
        Assert.AreEqual("Not reconciled yet", catalog.ObservedAt);
        Assert.Contains(
            "Definitions can still be added",
            catalog.Messages.Single(
                static message =>
                    message.Code == "managed_desktops.runtime_operations_unavailable").Remedy);
    }

    [TestMethod]
    public void Project_InLimitedModeKeepsEveryDefinitionVisible()
    {
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("code", "Code", 1, true),
            new ManagedDesktopDefinition("web", "Web", 2, true));
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Limited,
            Unbound(
                "code",
                "Code",
                1,
                true,
                ManagedDesktopMappingStatus.Limited,
                "managed_desktops.enumeration_unavailable",
                "The selected provider cannot enumerate virtual desktops, so semantic bindings were not changed."),
            Unbound(
                "web",
                "Web",
                2,
                true,
                ManagedDesktopMappingStatus.Limited,
                "managed_desktops.enumeration_unavailable",
                "The selected provider cannot enumerate virtual desktops, so semantic bindings were not changed."));
        DesktopTopologyProviderState limited = new(
            new DesktopTopologyProviderIdentity(
                "windows.documented.limited",
                "Windows documented API",
                "1",
                DesktopTopologyProviderMode.Limited,
                UsesPrivateApis: false),
            VirtualDesktopCapabilities.DocumentedLimited,
            DesktopTopologyProviderAvailability.Ready,
            "Limited Mode is active.");

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            snapshot,
            [],
            limited);

        Assert.HasCount(2, catalog.Entries);
        Assert.IsTrue(catalog.Entries.All(static entry => !entry.CanRecreate));
        Assert.IsTrue(catalog.Entries.All(static entry => entry.NeedsAttention));
        Assert.AreEqual("Limited", catalog.Outcome);
        Assert.AreEqual(0, catalog.BoundCount);
    }

    private static DesktopTopologyProviderState CreateProviderState() =>
        new(
            new DesktopTopologyProviderIdentity(
                "test.full",
                "Test Full",
                "1",
                DesktopTopologyProviderMode.Full,
                UsesPrivateApis: false),
            new VirtualDesktopCapabilities(
                CanGetWindowDesktopId: true,
                CanMoveWindowToDesktop: true,
                CanEnumerateDesktops: true,
                CanGetCurrentDesktop: true,
                CanCreateDesktop: true,
                CanSwitchDesktop: true,
                CanObserveTopologyChanges: true),
            DesktopTopologyProviderAvailability.Ready,
            "Ready.");

    [TestMethod]
    public void Project_DoesNotCountRulesThatNeverMoveAWindow()
    {
        // A rule that pins its application, or exempts it, still stores whatever
        // key it had — but nothing is sent to that desktop, so counting it would
        // tell a user a desktop is where an application lives when nothing puts
        // it there.
        Guid codeId = Guid.NewGuid();
        ConfigurationDocument configuration = new(
            ConfigurationDefaults.CurrentSchemaVersion,
            [new ManagedDesktopDefinition("code", "Code", 1, true)],
            [
                CreateRule("vscode", "code", isEnabled: true),
                CreateRule("music", "code", isEnabled: true) with
                {
                    Action = ApplicationRuleAction.ShowOnAllDesktops,
                },
                CreateRule("paint", "code", isEnabled: true) with
                {
                    Action = ApplicationRuleAction.AllowAnywhere,
                },
            ],
            new BehaviorSettings(false, false, false));

        ManagedDesktopCatalog catalog = ManagedDesktopCatalogProjection.Project(
            configuration,
            CreateSnapshot(
                ManagedDesktopReconciliationOutcome.Succeeded,
                Bound("code", "Code", 1, true, codeId, "Code", 0)),
            [],
            CreateProviderState());

        ManagedDesktopCatalogEntry code = catalog.Entries.Single();
        Assert.AreEqual(1, code.AssignedRuleCount);
        Assert.AreEqual(1, code.EnabledRuleCount);
    }

    private static ConfigurationDocument CreateConfiguration(
        params ManagedDesktopDefinition[] definitions) =>
        new(
            ConfigurationDefaults.CurrentSchemaVersion,
            [.. definitions],
            [],
            new BehaviorSettings(false, false, false));

    private static ApplicationRule CreateRule(
        string id,
        string targetDesktopKey,
        bool isEnabled) =>
        new(
            id,
            id,
            isEnabled,
            targetDesktopKey,
            [$"{id}.exe"],
            [ApplicationRuleTrigger.WindowCreated],
            DesktopSwitchPolicy.Never);

    private static ManagedDesktopReconciliationSnapshot CreateSnapshot(
        ManagedDesktopReconciliationOutcome outcome,
        params ManagedDesktopRuntimeMapping[] mappings) =>
        new(
            ObservedAt,
            ManagedDesktopReconciliationTrigger.Manual,
            "test.full",
            DesktopTopologyProviderMode.Full,
            outcome,
            [.. mappings],
            []);

    private static ManagedDesktopRuntimeMapping Bound(
        string semanticKey,
        string displayName,
        int preferredOrder,
        bool recreateWhenMissing,
        Guid runtimeDesktopId,
        string runtimeDisplayName,
        int runtimePosition) =>
        new(
            semanticKey,
            displayName,
            preferredOrder,
            recreateWhenMissing,
            runtimeDesktopId,
            runtimeDisplayName,
            runtimePosition,
            ManagedDesktopMappingStatus.ReusedPersistedBinding,
            [],
            "managed_desktops.persisted_binding_reused",
            "The durable semantic key was resolved through local app-owned binding metadata.");

    private static ManagedDesktopRuntimeMapping Unbound(
        string semanticKey,
        string displayName,
        int preferredOrder,
        bool recreateWhenMissing,
        ManagedDesktopMappingStatus status,
        string code,
        string explanation,
        ImmutableArray<Guid> candidateDesktopIds = default) =>
        new(
            semanticKey,
            displayName,
            preferredOrder,
            recreateWhenMissing,
            RuntimeDesktopId: null,
            RuntimeDisplayName: null,
            RuntimePosition: null,
            status,
            candidateDesktopIds.IsDefault ? [] : candidateDesktopIds,
            code,
            explanation);
}
