using System.Collections.Immutable;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.App.Presentation.Tests.ViewModels;

[TestClass]
public sealed class ManagedDesktopMappingPresentationProjectionTests
{
    [TestMethod]
    public void Project_OrdersMappingsAndKeepsRuntimeIdInAdvancedDetails()
    {
        Guid codeId = Guid.Parse("10a0d345-a25a-44fc-9740-4191cc049f9e");
        Guid webId = Guid.Parse("474a3ed6-7aae-4b91-9675-a6df9cbd7d7c");
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Succeeded,
            [
                CreateMapping(
                    "run-observe",
                    "Run & Observe",
                    preferredOrder: 2,
                    webId,
                    "Browsing",
                    runtimePosition: 3,
                    ManagedDesktopMappingStatus.ReusedCompatibleDesktop),
                CreateMapping(
                    "ide-development",
                    "IDE Development",
                    preferredOrder: 1,
                    codeId,
                    "Development",
                    runtimePosition: 0,
                    ManagedDesktopMappingStatus.ReusedPersistedBinding),
            ]);

        ManagedDesktopMappingPresentationSnapshot presentation =
            ManagedDesktopMappingPresentationProjection.Project(
                snapshot,
                ConfigurationDefaults.Create());

        Assert.AreEqual("Mapped", presentation.Outcome);
        Assert.AreEqual(2, presentation.MappedCount);
        Assert.AreEqual(0, presentation.AttentionCount);
        Assert.AreEqual("ide-development", presentation.Mappings[0].SemanticKey);
        Assert.AreEqual("Mapped to Development at position 1.", presentation.Mappings[0].RuntimeSummary);
        Assert.DoesNotContain(codeId.ToString("D"), presentation.Mappings[0].RuntimeSummary);
        Assert.Contains(codeId.ToString("D"), presentation.Mappings[0].AdvancedDetails);
        Assert.AreEqual("1 application rule", presentation.Mappings[0].RuleSummary);
        Assert.AreEqual("Recreate when missing", presentation.Mappings[0].RecreationPolicy);
    }

    [TestMethod]
    public void Project_AmbiguityRemainsUnboundAndListsCandidatesOnlyInAdvancedDetails()
    {
        Guid firstCandidate = Guid.Parse("f97be4ee-c028-4c13-a3ef-90d2b8764b97");
        Guid secondCandidate = Guid.Parse("49c6cd19-666b-4194-8b60-0689f9d4fa6a");
        ManagedDesktopRuntimeMapping mapping = new(
            "web",
            "Web",
            2,
            RecreateWhenMissing: true,
            RuntimeDesktopId: null,
            RuntimeDisplayName: null,
            RuntimePosition: null,
            ManagedDesktopMappingStatus.Ambiguous,
            [firstCandidate, secondCandidate],
            "managed_desktops.compatible_match_ambiguous",
            "More than one runtime desktop matched.");
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Partial,
            [mapping],
            [new ManagedDesktopReconciliationIssue(
                "managed_desktops.compatible_match_ambiguous",
                "The Web destination needs attention.")]);

        ManagedDesktopMappingPresentationSnapshot presentation =
            ManagedDesktopMappingPresentationProjection.Project(
                snapshot,
                ConfigurationDefaults.Create());

        Assert.AreEqual("Needs attention", presentation.Outcome);
        Assert.AreEqual(0, presentation.MappedCount);
        Assert.AreEqual(1, presentation.AttentionCount);
        Assert.AreEqual("Ambiguous", presentation.Mappings[0].Status);
        Assert.DoesNotContain(firstCandidate.ToString("D"), presentation.Mappings[0].RuntimeSummary);
        Assert.Contains(firstCandidate.ToString("D"), presentation.Mappings[0].AdvancedDetails);
        Assert.Contains(secondCandidate.ToString("D"), presentation.Mappings[0].AdvancedDetails);
        Assert.Contains("The Web destination needs attention.", presentation.IssueSummary);
    }

    [TestMethod]
    public void Project_LimitedMappingDoesNotInventRuntimeState()
    {
        ManagedDesktopRuntimeMapping mapping = new(
            "remote",
            "Remote",
            4,
            RecreateWhenMissing: false,
            RuntimeDesktopId: null,
            RuntimeDisplayName: null,
            RuntimePosition: null,
            ManagedDesktopMappingStatus.Limited,
            [],
            "managed_desktops.creation_unavailable",
            "The selected provider cannot create a missing desktop.");
        ManagedDesktopReconciliationSnapshot snapshot = CreateSnapshot(
            ManagedDesktopReconciliationOutcome.Limited,
            [mapping]);

        ManagedDesktopMappingPresentationSnapshot presentation =
            ManagedDesktopMappingPresentationProjection.Project(
                snapshot,
                configuration: null);

        Assert.AreEqual("Limited", presentation.Outcome);
        Assert.AreEqual("Limited", presentation.Mappings[0].Status);
        Assert.AreEqual("No runtime desktop is currently bound.", presentation.Mappings[0].RuntimeSummary);
        Assert.AreEqual("Rule count unavailable", presentation.Mappings[0].RuleSummary);
        Assert.AreEqual("Do not recreate when missing", presentation.Mappings[0].RecreationPolicy);
        Assert.IsTrue(presentation.Mappings[0].NeedsAttention);
        Assert.IsFalse(presentation.Mappings[0].IsMapped);
    }

    private static ManagedDesktopReconciliationSnapshot CreateSnapshot(
        ManagedDesktopReconciliationOutcome outcome,
        ImmutableArray<ManagedDesktopRuntimeMapping> mappings,
        ImmutableArray<ManagedDesktopReconciliationIssue> issues = default) =>
        new(
            new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero),
            ManagedDesktopReconciliationTrigger.Startup,
            "test-provider",
            DesktopTopologyProviderMode.Full,
            outcome,
            mappings,
            issues.IsDefault ? [] : issues);

    private static ManagedDesktopRuntimeMapping CreateMapping(
        string semanticKey,
        string displayName,
        int preferredOrder,
        Guid runtimeDesktopId,
        string runtimeDisplayName,
        int runtimePosition,
        ManagedDesktopMappingStatus status) =>
        new(
            semanticKey,
            displayName,
            preferredOrder,
            RecreateWhenMissing: true,
            runtimeDesktopId,
            runtimeDisplayName,
            runtimePosition,
            status,
            [],
            "managed_desktops.test",
            "Resolved by the reconciliation service.");
}
