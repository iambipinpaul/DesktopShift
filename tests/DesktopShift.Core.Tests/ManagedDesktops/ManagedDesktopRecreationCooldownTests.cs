using System.Collections.Immutable;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

[TestClass]
public sealed class ManagedDesktopRecreationCooldownTests
{
    [TestMethod]
    public void AKeyNobodyHasFoughtOver_IsAllowed()
    {
        ManagedDesktopRecreationCooldown cooldown = new(Bounds());

        ManagedDesktopRecreationDecision decision = cooldown.Evaluate("code");

        Assert.IsTrue(decision.IsAllowed);
        Assert.IsEmpty(cooldown.DrainSuppressions());
        Assert.AreEqual(0, cooldown.GetRemainingCooldown("code"));
    }

    [TestMethod]
    public void RecreationsBelowTheBound_StayAllowed()
    {
        ManagedDesktopRecreationCooldown cooldown = new(Bounds(maxRecreations: 3));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");
        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");

        Assert.IsTrue(cooldown.Evaluate("code").IsAllowed);
    }

    [TestMethod]
    public void RepeatedRecreation_SuppressesTheKeyOnceTheBoundIsReached()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 2, cooldownEvents: 3));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");
        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");
        cooldown.NoteTopologyEvent();
        ManagedDesktopRecreationDecision decision = cooldown.Evaluate("code");

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(
            ManagedDesktopRecreationCooldown.SuppressedCode,
            decision.Code);
        Assert.Contains("code", decision.Reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ASuppressedKey_IsDrainedOnceAndCarriesItsRemainingBudget()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 1, cooldownEvents: 4));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");
        cooldown.NoteTopologyEvent();
        _ = cooldown.Evaluate("code");

        ImmutableArray<ManagedDesktopRecreationSuppression> first =
            cooldown.DrainSuppressions();
        ImmutableArray<ManagedDesktopRecreationSuppression> second =
            cooldown.DrainSuppressions();

        Assert.HasCount(1, first);
        Assert.AreEqual("code", first[0].SemanticKey);
        Assert.AreEqual(
            ManagedDesktopRecreationCooldown.SuppressedCode,
            first[0].Code);
        Assert.AreEqual(3, first[0].RemainingTopologyEvents);
        Assert.IsEmpty(second);
    }

    [TestMethod]
    public void TheCooldown_IsBoundedAndLiftsAfterTheConfiguredTopologyEvents()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 1, cooldownEvents: 3));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");

        // Three notifications spend the whole budget. The third is the one that
        // lifts it, so recreation is allowed again on the pass that follows.
        cooldown.NoteTopologyEvent();
        Assert.IsFalse(cooldown.Evaluate("code").IsAllowed);
        cooldown.NoteTopologyEvent();
        Assert.IsFalse(cooldown.Evaluate("code").IsAllowed);
        cooldown.NoteTopologyEvent();

        Assert.IsTrue(cooldown.Evaluate("code").IsAllowed);
        Assert.AreEqual(0, cooldown.GetRemainingCooldown("code"));
    }

    [TestMethod]
    public void TheCooldown_IsDrivenByTopologyEventsAndNeverByTime()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 1, cooldownEvents: 2));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");

        // No notifications arrive, so no budget is spent no matter how many
        // times the gate is asked. A timer-based cooldown would have expired.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            Assert.IsFalse(cooldown.Evaluate("code").IsAllowed);
        }

        Assert.AreEqual(2, cooldown.GetRemainingCooldown("code"));
    }

    [TestMethod]
    public void AQuietSpell_ResetsTheRecreationCountBeforeTheBoundIsReached()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 2, recreationWindowEvents: 3));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");

        // Three notifications with no recreation mean the user stopped deleting
        // it, so the next deletion starts from a clean count.
        cooldown.NoteTopologyEvent();
        cooldown.NoteTopologyEvent();
        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");

        Assert.IsTrue(cooldown.Evaluate("code").IsAllowed);
        Assert.AreEqual(0, cooldown.GetRemainingCooldown("code"));
    }

    [TestMethod]
    public void SuppressingOneKey_NeverSuppressesAnother()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 1, cooldownEvents: 5));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("code");
        cooldown.NoteTopologyEvent();

        Assert.IsFalse(cooldown.Evaluate("code").IsAllowed);
        Assert.IsTrue(cooldown.Evaluate("web").IsAllowed);
        Assert.AreEqual(0, cooldown.GetRemainingCooldown("web"));
        Assert.HasCount(1, cooldown.DrainSuppressions());
    }

    [TestMethod]
    public void SemanticKeys_AreMatchedTheWayTheRestOfTheAppMatchesThem()
    {
        ManagedDesktopRecreationCooldown cooldown = new(
            Bounds(maxRecreations: 1, cooldownEvents: 5));

        cooldown.NoteTopologyEvent();
        cooldown.NoteRecreated("Code");
        cooldown.NoteTopologyEvent();

        Assert.IsFalse(cooldown.Evaluate("code").IsAllowed);
    }

    [TestMethod]
    public void EveryBound_MustBePositive()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            static () => _ = new ManagedDesktopRecreationCooldownOptions(
                MaxRecreations: 0));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            static () => _ = new ManagedDesktopRecreationCooldownOptions(
                RecreationWindowTopologyEvents: 0));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            static () => _ = new ManagedDesktopRecreationCooldownOptions(
                CooldownTopologyEvents: -1));
    }

    [TestMethod]
    public void TheShippedDefaults_BoundRecreationWithoutBeingTwitchy()
    {
        ManagedDesktopRecreationCooldownOptions options =
            ManagedDesktopRecreationCooldownOptions.Default;

        Assert.IsGreaterThan(1, options.MaxRecreations);
        Assert.IsGreaterThan(
            options.MaxRecreations,
            options.RecreationWindowTopologyEvents);
        Assert.IsGreaterThan(
            options.RecreationWindowTopologyEvents,
            options.CooldownTopologyEvents);
    }

    private static ManagedDesktopRecreationCooldownOptions Bounds(
        int maxRecreations = 2,
        int recreationWindowEvents = 64,
        int cooldownEvents = 4) =>
        new(maxRecreations, recreationWindowEvents, cooldownEvents);
}
