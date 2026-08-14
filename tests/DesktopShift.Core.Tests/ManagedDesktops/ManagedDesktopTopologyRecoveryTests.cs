using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

/// <summary>
/// What DesktopShift does when the user edits their own virtual desktops.
/// </summary>
/// <remarks>
/// Every assertion about "nothing else was touched" is a call-count or argument
/// assertion on a fake. No test here creates, deletes, renames, reorders, or
/// switches a real Windows virtual desktop, and none moves a real window.
/// </remarks>
[TestClass]
public sealed class ManagedDesktopTopologyRecoveryTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ATopologyNotification_ReconcilesThenRunsOneWindowReconciliation()
    {
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(),
            [Definition("code", recreateWhenMissing: true)]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        Assert.AreEqual(
            ManagedDesktopTopologyRecoveryOutcome.Reconciled,
            result.Outcome);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Created,
            result.Snapshot.Mappings.Single().Status);
        Assert.AreEqual(1, result.Report.CreatedCount);
        Assert.IsTrue(result.WindowReconciliationRan);
        Assert.AreEqual(7, result.WindowsInspected);
        Assert.AreEqual(1, harness.Windows.ReassignAllCallCount);
        Assert.AreEqual(1, harness.Provider.CreateCallCount);
        Assert.AreEqual(0, harness.Provider.SwitchCallCount);
    }

    [TestMethod]
    public async Task ANotificationThatChangesNothing_RunsNoWindowReconciliation()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("CurrentChanged");

        Assert.AreEqual(
            ManagedDesktopTopologyRecoveryOutcome.NoChange,
            result.Outcome);
        Assert.IsFalse(result.Report.ChangedAnything);
        Assert.IsFalse(result.WindowReconciliationRan);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
    }

    [TestMethod]
    public async Task ABurstOfNotifications_NeverProducesABurstOfWindowPasses()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)]);

        // Deleting one desktop in Task View raises several notifications. Only
        // the one that actually loses the mapping may cost a window pass.
        harness.Provider.SimulateUserDeleted(codeId);
        _ = await harness.Service.HandleTopologyChangedAsync("Destroyed");
        _ = await harness.Service.HandleTopologyChangedAsync("Created");
        _ = await harness.Service.HandleTopologyChangedAsync("CurrentChanged");
        _ = await harness.Service.HandleTopologyChangedAsync("Switched");

        Assert.AreEqual(1, harness.Provider.CreateCallCount);
        Assert.AreEqual(1, harness.Windows.ReassignAllCallCount);
    }

    [TestMethod]
    public async Task ADeletedManagedDesktop_IsRecreatedWhenPolicyAndCapabilitiesPermit()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)]);
        harness.Provider.SimulateUserDeleted(codeId);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        ManagedDesktopRuntimeMapping mapping = result.Snapshot.Mappings.Single();
        Assert.AreEqual(ManagedDesktopMappingStatus.Created, mapping.Status);
        Assert.AreNotEqual(codeId, mapping.RuntimeDesktopId);
        Assert.AreEqual(1, harness.Provider.CreateCallCount);
        Assert.AreEqual(
            mapping.RuntimeDesktopId,
            harness.Bindings.Current.Single().RuntimeDesktopId);
        Assert.IsEmpty(result.Suppressions);
    }

    [TestMethod]
    public async Task ADeletedManagedDesktop_IsLeftAloneWhenThePolicyForbidsRecreation()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: false)],
            [new ManagedDesktopBinding("code", codeId)]);
        harness.Provider.SimulateUserDeleted(codeId);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        ManagedDesktopRuntimeMapping mapping = result.Snapshot.Mappings.Single();
        Assert.AreEqual(ManagedDesktopMappingStatus.Missing, mapping.Status);
        Assert.AreEqual("managed_desktops.missing_recreation_disabled", mapping.Code);
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
    }

    [TestMethod]
    public async Task ADeletedManagedDesktop_IsNotRecreatedInLimitedMode()
    {
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Limited(),
            [Definition("code", recreateWhenMissing: true)]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        Assert.AreEqual(
            ManagedDesktopTopologyRecoveryOutcome.Limited,
            result.Outcome);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Limited,
            result.Snapshot.Mappings.Single().Status);
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
        Assert.IsFalse(result.WindowReconciliationRan);
    }

    [TestMethod]
    public async Task ADeletedManagedDesktop_IsNotRecreatedWhenTheProviderCannotCreateOne()
    {
        // A provider that enumerates but cannot create is the honest shape of a
        // partly validated adapter. Recreation must decline rather than call an
        // operation the capability check never cleared.
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.EnumerateOnly(),
            [Definition("code", recreateWhenMissing: true)]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        ManagedDesktopRuntimeMapping mapping = result.Snapshot.Mappings.Single();
        Assert.AreEqual(ManagedDesktopMappingStatus.Limited, mapping.Status);
        Assert.AreEqual("managed_desktops.creation_unavailable", mapping.Code);
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
    }

    [TestMethod]
    public async Task TwoDesktopsThatBothMatchOneDefinition_AreNeverGuessedBetween()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(first, "Code", 0, true),
                new VirtualDesktopDescriptor(second, "Code", 1, false)),
            [Definition("code", recreateWhenMissing: true)]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("NameChanged");

        ManagedDesktopRuntimeMapping mapping = result.Snapshot.Mappings.Single();
        Assert.AreEqual(ManagedDesktopMappingStatus.Ambiguous, mapping.Status);
        CollectionAssert.AreEquivalent(
            new[] { first, second },
            mapping.CandidateDesktopIds.ToArray());

        // Neither candidate was claimed, created over, or switched to.
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
        CollectionAssert.AreEquivalent(
            new[] { first, second },
            harness.Provider.Desktops.Select(static desktop => desktop.Id).ToArray());
    }

    [TestMethod]
    public async Task RepeatedDeletion_StopsRecreatingAfterABoundedNumberOfAttempts()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)],
            new ManagedDesktopRecreationCooldownOptions(
                MaxRecreations: 2,
                RecreationWindowTopologyEvents: 64,
                CooldownTopologyEvents: 8));

        ManagedDesktopTopologyRecoveryResult? last = null;
        for (int deletion = 0; deletion < 6; deletion++)
        {
            DeleteEveryDesktop(harness.Provider);
            last = await harness.Service.HandleTopologyChangedAsync("Destroyed");
        }

        Assert.AreEqual(2, harness.Provider.CreateCallCount);
        Assert.AreEqual(2, harness.Windows.ReassignAllCallCount);
        Assert.IsNotNull(last);
        Assert.HasCount(1, last.Suppressions);
        Assert.AreEqual("code", last.Suppressions[0].SemanticKey);
        Assert.AreEqual(
            ManagedDesktopRecreationCooldown.SuppressedCode,
            last.Suppressions[0].Code);
        Assert.AreEqual(
            ManagedDesktopRecreationCooldown.SuppressedCode,
            last.Snapshot.Mappings.Single().Code);
    }

    [TestMethod]
    public async Task TheCooldown_IsBoundedAndRecreationResumesAfterIt()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)],
            new ManagedDesktopRecreationCooldownOptions(
                MaxRecreations: 1,
                RecreationWindowTopologyEvents: 64,
                CooldownTopologyEvents: 3));

        DeleteEveryDesktop(harness.Provider);
        _ = await harness.Service.HandleTopologyChangedAsync("Destroyed");
        Assert.AreEqual(1, harness.Provider.CreateCallCount);

        // The cooldown holds for exactly the configured number of
        // notifications, and not one more.
        for (int notification = 0; notification < 2; notification++)
        {
            DeleteEveryDesktop(harness.Provider);
            _ = await harness.Service.HandleTopologyChangedAsync("Destroyed");
            Assert.AreEqual(1, harness.Provider.CreateCallCount);
        }

        DeleteEveryDesktop(harness.Provider);
        ManagedDesktopTopologyRecoveryResult resumed =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        Assert.AreEqual(2, harness.Provider.CreateCallCount);
        Assert.IsEmpty(resumed.Suppressions);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Created,
            resumed.Snapshot.Mappings.Single().Status);
    }

    [TestMethod]
    public async Task AnExplicitRecreation_IsNeverHeldBackByTheCooldown()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)],
            new ManagedDesktopRecreationCooldownOptions(
                MaxRecreations: 1,
                RecreationWindowTopologyEvents: 64,
                CooldownTopologyEvents: 64));

        DeleteEveryDesktop(harness.Provider);
        _ = await harness.Service.HandleTopologyChangedAsync("Destroyed");
        DeleteEveryDesktop(harness.Provider);
        _ = await harness.Service.HandleTopologyChangedAsync("Destroyed");
        Assert.AreEqual(1, harness.Provider.CreateCallCount);

        ManagedDesktopMaintenanceService maintenance = new(
            harness.Configuration,
            harness.Reconciliation,
            harness.Provider,
            harness.Bindings);
        ManagedDesktopMaintenanceResult result =
            await maintenance.RecreateAsync("code");

        Assert.AreEqual(ManagedDesktopMaintenanceOutcome.Applied, result.Outcome);
        Assert.AreEqual(2, harness.Provider.CreateCallCount);
    }

    [TestMethod]
    public async Task ReorderingDesktops_UpdatesTheRuntimeMappingAndChangesNoRule()
    {
        Guid codeId = Guid.NewGuid();
        Guid webId = Guid.NewGuid();
        Guid personalId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true),
                new VirtualDesktopDescriptor(webId, "Web", 1, false),
                new VirtualDesktopDescriptor(personalId, "Personal", 2, false)),
            [
                Definition("code", recreateWhenMissing: true),
                Definition("web", recreateWhenMissing: true, order: 2, name: "Web"),
            ],
            [
                new ManagedDesktopBinding("code", codeId),
                new ManagedDesktopBinding("web", webId),
            ]);

        _ = await harness.Service.HandleTopologyChangedAsync("CurrentChanged");
        harness.Provider.SimulateUserReordered(webId, personalId, codeId);
        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Moved");

        Assert.AreEqual(
            ManagedDesktopTopologyRecoveryOutcome.Reconciled,
            result.Outcome);

        ManagedDesktopRuntimeMapping code = result.Snapshot.Mappings
            .Single(static mapping => mapping.SemanticKey == "code");
        ManagedDesktopRuntimeMapping web = result.Snapshot.Mappings
            .Single(static mapping => mapping.SemanticKey == "web");

        // Same semantic keys, same runtime desktops, new positions.
        Assert.AreEqual(codeId, code.RuntimeDesktopId);
        Assert.AreEqual(2, code.RuntimePosition);
        Assert.AreEqual(webId, web.RuntimeDesktopId);
        Assert.AreEqual(0, web.RuntimePosition);
        Assert.HasCount(2, result.Remaps);
        Assert.IsTrue(
            result.Remaps.Any(static remap =>
                remap.SemanticKey == "code" &&
                remap.PreviousPosition == 0 &&
                remap.Position == 2));

        // The app's preferred order follows the managed desktops' relative
        // order in Task View. The unmanaged Personal desktop remains outside
        // the configured list.
        CollectionAssert.AreEqual(
            new[] { "web", "code" },
            harness.Configuration.CurrentState.Active!.ManagedDesktops
                .Select(static desktop => desktop.SemanticKey)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            harness.Configuration.CurrentState.Active.ManagedDesktops
                .Select(static desktop => desktop.PreferredOrder)
                .ToArray());

        // Application Rules target semantic keys, so the reorder repoints
        // nothing even though the preferred order is saved.
        CollectionAssert.AreEqual(
            new[] { "code", "web" },
            harness.Configuration.CurrentState.Active!.ApplicationRules
                .Select(static rule => rule.TargetDesktopKey)
                .ToArray());
        Assert.AreEqual(1, harness.Configuration.SaveCallCount);

        // Nothing was created, switched, or moved for a reorder.
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
        Assert.HasCount(3, harness.Provider.Desktops);
    }

    [TestMethod]
    public async Task FirstMovedNotification_SynchronizesPreferredOrderWithoutABaseline()
    {
        Guid codeId = Guid.NewGuid();
        Guid webId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(webId, "Web", 0, true),
                new VirtualDesktopDescriptor(codeId, "Code", 1, false)),
            [
                Definition("code", recreateWhenMissing: true),
                Definition("web", recreateWhenMissing: true, order: 2, name: "Web"),
            ],
            [
                new ManagedDesktopBinding("code", codeId),
                new ManagedDesktopBinding("web", webId),
            ]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Moved");

        Assert.IsEmpty(result.Remaps);
        Assert.AreEqual(1, harness.Configuration.SaveCallCount);
        CollectionAssert.AreEqual(
            new[] { "web", "code" },
            harness.Configuration.CurrentState.Active!.ManagedDesktops
                .Select(static desktop => desktop.SemanticKey)
                .ToArray());
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
        Assert.AreEqual(0, harness.Windows.ReassignAllCallCount);
    }

    [TestMethod]
    public async Task RecoveringOneDesktop_TouchesNoUnrelatedDesktop()
    {
        Guid codeId = Guid.NewGuid();
        Guid personalId = Guid.NewGuid();
        Guid gamesId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true),
                new VirtualDesktopDescriptor(personalId, "Personal", 1, false),
                new VirtualDesktopDescriptor(gamesId, "Games", 2, false)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)]);
        harness.Provider.SimulateUserDeleted(codeId);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        // Exactly one create, for the one managed key, and no switch at all.
        Assert.AreEqual(1, harness.Provider.CreateCallCount);
        Assert.AreEqual(0, harness.Provider.SwitchCallCount);
        Assert.HasCount(1, harness.Provider.CreatedIds);

        // The two desktops DesktopShift does not manage are still there, still
        // themselves, and no binding claims either of them.
        Assert.IsTrue(
            harness.Provider.Desktops.Any(desktop =>
                desktop.Id == personalId && desktop.DisplayName == "Personal"));
        Assert.IsTrue(
            harness.Provider.Desktops.Any(desktop =>
                desktop.Id == gamesId && desktop.DisplayName == "Games"));
        Assert.AreEqual(
            harness.Provider.CreatedIds.Single(),
            harness.Bindings.Current.Single().RuntimeDesktopId);
        Assert.AreEqual("code", result.Snapshot.Mappings.Single().SemanticKey);
    }

    [TestMethod]
    public async Task TheReconciliationDecision_IsVisibleInStructuredActivity()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)]);
        harness.Provider.SimulateUserDeleted(codeId);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        IReadOnlyList<ActivityRecord> records = harness.Journal.Snapshot;
        Assert.IsTrue(
            records.All(static record =>
                record.Source == ActivityEventSource.Topology));
        Assert.IsTrue(
            records.All(record => record.CorrelationId == result.CorrelationId));
        Assert.IsTrue(
            records.All(static record => record.TopologyReason == "Destroyed"));
        Assert.HasCount(
            1,
            records.Where(static record =>
                record.ResultCode == "topology.reconciled").ToArray());
        Assert.HasCount(
            1,
            records.Where(static record =>
                record.ResultCode == "topology.window_reconciliation_ran").ToArray());

        // A topology decision is about desktops, so no window identity travels
        // with it.
        Assert.IsTrue(records.All(static record => record.Identity is null));
        Assert.IsTrue(records.All(static record => record.Application is null));
    }

    [TestMethod]
    public async Task ASuppressedRecreation_IsVisibleInStructuredActivity()
    {
        Guid codeId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)],
            new ManagedDesktopRecreationCooldownOptions(
                MaxRecreations: 1,
                RecreationWindowTopologyEvents: 64,
                CooldownTopologyEvents: 8));

        DeleteEveryDesktop(harness.Provider);
        _ = await harness.Service.HandleTopologyChangedAsync("Destroyed");
        harness.Journal.Clear();
        DeleteEveryDesktop(harness.Provider);
        ManagedDesktopTopologyRecoveryResult suppressed =
            await harness.Service.HandleTopologyChangedAsync("Destroyed");

        ActivityRecord record = harness.Journal.Snapshot.Single(
            static item => item.ResultCode == "topology.recreation_suppressed");
        Assert.AreEqual(ActivityEventSource.Topology, record.Source);
        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual("code", record.TargetDesktopKey);
        Assert.AreEqual(suppressed.CorrelationId, record.CorrelationId);
        Assert.AreEqual(
            ManagedDesktopRecreationCooldown.SuppressedCode,
            record.Error?.Code);
        Assert.HasCount(
            1,
            harness.Journal.Snapshot.Where(static item =>
                item.ResultCode == "topology.window_reconciliation_skipped")
                .ToArray());
    }

    [TestMethod]
    public async Task AReorder_IsVisibleInStructuredActivity()
    {
        Guid codeId = Guid.NewGuid();
        Guid personalId = Guid.NewGuid();
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(
                new VirtualDesktopDescriptor(codeId, "Code", 0, true),
                new VirtualDesktopDescriptor(personalId, "Personal", 1, false)),
            [Definition("code", recreateWhenMissing: true)],
            [new ManagedDesktopBinding("code", codeId)]);

        _ = await harness.Service.HandleTopologyChangedAsync("CurrentChanged");
        harness.Journal.Clear();
        harness.Provider.SimulateUserReordered(personalId, codeId);
        _ = await harness.Service.HandleTopologyChangedAsync("Moved");

        ActivityRecord record = harness.Journal.Snapshot.Single(
            static item => item.ResultCode == "topology.runtime_position_changed");
        Assert.AreEqual("code", record.TargetDesktopKey);
        Assert.AreEqual("Moved", record.TopologyReason);
        Assert.AreEqual(ActivityResult.Succeeded, record.Result);
    }

    [TestMethod]
    public async Task AnUnknownNotificationReason_IsRecordedAndStillReconciled()
    {
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(),
            [Definition("code", recreateWhenMissing: true)]);

        ManagedDesktopTopologyRecoveryResult result =
            await harness.Service.HandleTopologyChangedAsync("SomethingNewInWindows");

        Assert.AreEqual("SomethingNewInWindows", result.Reason);
        Assert.AreEqual(1, harness.Provider.CreateCallCount);
    }

    [TestMethod]
    public async Task ANotificationWithoutAReason_IsRejectedRatherThanGuessed()
    {
        Harness harness = Harness.Create(
            MaintenanceTopologyProvider.Full(),
            [Definition("code", recreateWhenMissing: true)]);

        _ = await Assert.ThrowsAsync<ArgumentException>(
            async () => await harness.Service.HandleTopologyChangedAsync("  "));
        Assert.AreEqual(0, harness.Provider.MutatingCallCount);
    }

    private static void DeleteEveryDesktop(MaintenanceTopologyProvider provider)
    {
        foreach (Guid desktopId in provider.Desktops
            .Select(static desktop => desktop.Id)
            .ToArray())
        {
            provider.SimulateUserDeleted(desktopId);
        }
    }

    private static ManagedDesktopDefinition Definition(
        string semanticKey,
        bool recreateWhenMissing,
        int order = 1,
        string? name = null) =>
        new(semanticKey, name ?? "Code", order, recreateWhenMissing);

    private sealed record Harness(
        MaintenanceTopologyProvider Provider,
        MaintenanceConfigurationService Configuration,
        MaintenanceBindingStore Bindings,
        ManagedDesktopReconciliationService Reconciliation,
        RecordingWindowReassignmentService Windows,
        BoundedActivityJournal Journal,
        ManagedDesktopTopologyRecoveryService Service)
    {
        public static Harness Create(
            MaintenanceTopologyProvider provider,
            IEnumerable<ManagedDesktopDefinition> definitions,
            IEnumerable<ManagedDesktopBinding>? bindings = null,
            ManagedDesktopRecreationCooldownOptions? options = null)
        {
            ImmutableArray<ManagedDesktopDefinition> managedDesktops = [.. definitions];
            MaintenanceConfigurationService configuration = new(
                new ConfigurationDocument(
                    ConfigurationDefaults.CurrentSchemaVersion,
                    managedDesktops,
                    [.. managedDesktops.Select(CreateRule)],
                    new BehaviorSettings(false, false, false)));
            MaintenanceBindingStore store = new([.. bindings ?? []]);
            MaintenanceTimeProvider time = new(ObservedAt);
            ManagedDesktopRecreationCooldown cooldown = new(
                options ?? ManagedDesktopRecreationCooldownOptions.Default);
            ManagedDesktopReconciliationService reconciliation = new(
                configuration,
                provider,
                store,
                time,
                cooldown);
            RecordingWindowReassignmentService windows = new(enumeratedWindowCount: 7);
            BoundedActivityJournal journal = new(time);

            return new Harness(
                provider,
                configuration,
                store,
                reconciliation,
                windows,
                journal,
                new ManagedDesktopTopologyRecoveryService(
                    reconciliation,
                    configuration,
                    cooldown,
                    time,
                    windows,
                    journal));
        }

        private static ApplicationRule CreateRule(
            ManagedDesktopDefinition definition) =>
            new(
                definition.SemanticKey,
                definition.DisplayName,
                IsEnabled: true,
                definition.SemanticKey,
                [$"{definition.SemanticKey}.exe"],
                [ApplicationRuleTrigger.WindowCreated],
                DesktopSwitchPolicy.Never);
    }
}
