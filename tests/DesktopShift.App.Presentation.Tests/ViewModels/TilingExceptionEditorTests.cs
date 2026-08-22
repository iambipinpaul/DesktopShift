using System.Collections.Immutable;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Configuration;

namespace DesktopShift.App.Presentation.Tests.ViewModels;

[TestClass]
public sealed class TilingExceptionEditorTests
{
    [TestMethod]
    public void Present_ShowsBuiltInPoliciesAsReadOnlyAndUserRulesAsRemovable()
    {
        TilingSettings settings = new(
            FloatRules:
            [
                Rule("float-terminal", "Terminal", "WindowsTerminal.exe"),
            ],
            IgnoreRules:
            [
                Rule("ignore-widget", "Widget", "Widget.exe"),
            ]);

        IReadOnlyList<TilingExceptionPresentation> items =
            TilingExceptionEditor.Present(settings);

        TilingExceptionPresentation taskManager = items.Single(
            static item => item.Id == "built-in-float-task-manager");
        Assert.AreEqual(TilingExceptionDisposition.Float, taskManager.Disposition);
        Assert.IsTrue(taskManager.IsBuiltIn);
        Assert.IsFalse(taskManager.CanRemove);

        TilingExceptionPresentation recorder = items.Single(
            static item => item.Id == "built-in-ignore-snipping-overlays");
        Assert.AreEqual(TilingExceptionDisposition.Ignore, recorder.Disposition);
        Assert.IsTrue(recorder.IsBuiltIn);
        Assert.IsFalse(recorder.CanRemove);

        TilingExceptionPresentation userRule = items.Single(
            static item => item.Id == "ignore-widget");
        Assert.IsFalse(userRule.IsBuiltIn);
        Assert.IsTrue(userRule.CanRemove);
    }

    [TestMethod]
    public void Add_CreatesApplicationRuleAndPreservesAllOtherTilingSettings()
    {
        TilingSettings source = new(
            IsEnabled: true,
            OuterGap: 17,
            InnerGap: 23,
            MinimumTileWidth: 410,
            MinimumTileHeight: 290,
            FloatRules: [Rule("existing", "Existing", "existing.exe")],
            DisabledManagedDesktopKeys: ["remote"]);
        RunningApplicationCandidate candidate = new(
            "Widget.exe",
            @"C:\Tools\Widget.exe",
            "Widget.Package_abc",
            "Widget.App",
            ImmutableArray.Create("WidgetClass"),
            2,
            null);

        TilingSettings changed = TilingExceptionEditor.Add(
            source,
            candidate,
            TilingExceptionDisposition.Ignore);

        Assert.AreEqual(source with { IgnoreRules = changed.IgnoreRules }, changed);
        Assert.HasCount(1, changed.IgnoreRules);
        TilingIdentityRule added = changed.IgnoreRules[0];
        Assert.AreEqual("Widget", added.DisplayName);
        CollectionAssert.AreEqual(new[] { "Widget.exe" }, added.ProcessNames.ToArray());
        CollectionAssert.AreEqual(
            new[] { "Widget.Package_abc" },
            added.PackageFamilyNames.ToArray());
        CollectionAssert.AreEqual(new[] { "Widget.App" }, added.AppUserModelIds.ToArray());
        Assert.IsEmpty(added.ExecutablePaths);
        Assert.IsEmpty(added.WindowClasses);
    }

    [TestMethod]
    public void Add_UsesAUniqueStableIdAndDoesNotDuplicateTheSameApplication()
    {
        RunningApplicationCandidate candidate = Candidate("Widget.exe");
        TilingSettings source = new(
            FloatRules: [Rule("float-widget", "Old Widget", "other.exe")]);

        TilingSettings once = TilingExceptionEditor.Add(
            source,
            candidate,
            TilingExceptionDisposition.Float);
        TilingSettings twice = TilingExceptionEditor.Add(
            once,
            candidate,
            TilingExceptionDisposition.Float);

        Assert.HasCount(2, once.FloatRules);
        Assert.AreEqual("float-widget-2", once.FloatRules[1].Id);
        Assert.AreEqual(once, twice);
    }

    [TestMethod]
    public void Remove_DeletesOnlyTheRequestedUserRule()
    {
        TilingSettings source = new(
            FloatRules: [Rule("float-one", "One", "one.exe")],
            IgnoreRules:
            [
                Rule("ignore-two", "Two", "two.exe"),
                Rule("ignore-three", "Three", "three.exe"),
            ]);

        TilingSettings changed = TilingExceptionEditor.Remove(
            source,
            "ignore-two");

        CollectionAssert.AreEqual(
            source.FloatRules.ToArray(),
            changed.FloatRules.ToArray());
        CollectionAssert.AreEqual(
            new[] { "ignore-three" },
            changed.IgnoreRules.Select(static rule => rule.Id).ToArray());
    }

    private static RunningApplicationCandidate Candidate(string processName) =>
        new(processName, null, null, null, [], 1, null);

    private static TilingIdentityRule Rule(
        string id,
        string name,
        string processName) =>
        new(id, name, true, ProcessNames: [processName]);
}
