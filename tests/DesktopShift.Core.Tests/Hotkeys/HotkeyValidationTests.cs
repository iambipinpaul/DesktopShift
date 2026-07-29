using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Core.Tests.Hotkeys;

[TestClass]
public sealed class HotkeyValidationTests
{
    [TestMethod]
    public void Validate_ReportsEveryProblemAgainstTheEditableRow()
    {
        HotkeyBinding first = Binding(
            HotkeyAction.ReassignAllWindows,
            HotkeyModifiers.Control,
            HotkeyKey.R);
        HotkeyBinding conflict = Binding(
            HotkeyAction.ReassignForegroundWindow,
            HotkeyModifiers.Control,
            HotkeyKey.R);
        HotkeyBinding bare = Binding(
            HotkeyAction.TogglePause,
            HotkeyModifiers.None,
            HotkeyKey.P);
        HotkeyBinding unassigned = Binding(
            HotkeyAction.OpenDesktopShift,
            HotkeyModifiers.Control,
            HotkeyKey.None);
        HotkeyBinding duplicateAction = Binding(
            HotkeyAction.OpenDesktopShift,
            HotkeyModifiers.Alt,
            HotkeyKey.D,
            isEnabled: false);

        ConfigurationValidationIssue[] issues =
            HotkeyValidation.Validate(
                [first, conflict, bare, unassigned, duplicateAction])
            .ToArray();

        AssertIssue(
            issues,
            ConfigurationValidationCode.HotkeyConflict,
            "$.behavior.hotkeys[1]");
        AssertIssue(
            issues,
            ConfigurationValidationCode.HotkeyMissingModifier,
            "$.behavior.hotkeys[2].modifiers");
        AssertIssue(
            issues,
            ConfigurationValidationCode.HotkeyUnassigned,
            "$.behavior.hotkeys[3].key");
        AssertIssue(
            issues,
            ConfigurationValidationCode.DuplicateHotkeyAction,
            "$.behavior.hotkeys[4].action");
        Assert.IsTrue(
            issues.All(static issue => issue.EntryKind == ConfigurationEntryKind.Hotkey));
    }

    [TestMethod]
    public void Validate_DisabledRowsDoNotClaimChordsButDuplicateActionsStillFail()
    {
        HotkeyBinding enabled = Binding(
            HotkeyAction.ReassignAllWindows,
            HotkeyModifiers.Control,
            HotkeyKey.R);
        HotkeyBinding disabledSameChord = Binding(
            HotkeyAction.ReassignForegroundWindow,
            HotkeyModifiers.Control,
            HotkeyKey.R,
            isEnabled: false);
        HotkeyBinding disabledDuplicateAction = enabled with { IsEnabled = false };

        ConfigurationValidationIssue[] chordIssues =
            HotkeyValidation.Validate([enabled, disabledSameChord]).ToArray();
        Assert.IsEmpty(chordIssues);

        ConfigurationValidationIssue[] actionIssues =
            HotkeyValidation.Validate([enabled, disabledDuplicateAction]).ToArray();
        Assert.HasCount(1, actionIssues);
        Assert.AreEqual(
            ConfigurationValidationCode.DuplicateHotkeyAction,
            actionIssues[0].Code);
    }

    [TestMethod]
    public void Registrable_ReturnsOnlyEnabledRowsThatSurviveValidation()
    {
        HotkeyBinding valid = Binding(
            HotkeyAction.ReassignAllWindows,
            HotkeyModifiers.Control,
            HotkeyKey.R);
        HotkeyBinding disabled = Binding(
            HotkeyAction.ReassignForegroundWindow,
            HotkeyModifiers.Alt,
            HotkeyKey.F,
            isEnabled: false);
        HotkeyBinding invalid = Binding(
            HotkeyAction.TogglePause,
            HotkeyModifiers.None,
            HotkeyKey.P);

        HotkeyBinding[] result =
            HotkeyValidation.Registrable([valid, disabled, invalid]).ToArray();

        CollectionAssert.AreEqual(new[] { valid }, result);
    }

    [TestMethod]
    public void Defaults_DescribeFourDistinctBindingsWithoutEnablingTheMasterSwitch()
    {
        Assert.IsFalse(HotkeySettings.Disabled.IsEnabled);
        Assert.HasCount(4, HotkeyDefaults.Actions);
        Assert.HasCount(4, HotkeyDefaults.Bindings);
        CollectionAssert.AreEqual(
            HotkeyDefaults.Actions.ToArray(),
            HotkeyDefaults.Bindings
                .Select(static binding => binding.Action)
                .ToArray());
        Assert.AreEqual(
            4,
            HotkeyDefaults.Bindings.Select(static binding => binding.Chord).Distinct().Count());
        Assert.IsTrue(
            HotkeyDefaults.Bindings.All(static binding =>
                binding.IsEnabled &&
                binding.Modifiers != HotkeyModifiers.None &&
                !binding.Modifiers.HasFlag(HotkeyModifiers.Windows)));
    }

    [TestMethod]
    public void Complete_PreservesProvidedRowsAndFillsEveryMissingActionInOrder()
    {
        HotkeyBinding custom = Binding(
            HotkeyAction.TogglePause,
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyKey.F12,
            isEnabled: false);

        HotkeyBinding[] completed = HotkeyDefaults.Complete([custom]).ToArray();

        CollectionAssert.AreEqual(
            HotkeyDefaults.Actions.ToArray(),
            completed.Select(static binding => binding.Action).ToArray());
        Assert.AreEqual(custom, completed[2]);
    }

    [TestMethod]
    public void ChordDescription_UsesReadableModifierAndKeyNames()
    {
        Assert.AreEqual(
            "Ctrl + Alt + Shift + Win + Page Up",
            new HotkeyChord(
                HotkeyModifiers.Control |
                HotkeyModifiers.Alt |
                HotkeyModifiers.Shift |
                HotkeyModifiers.Windows,
                HotkeyKey.PageUp)
            .Describe());
        Assert.AreEqual("Ctrl + 7", new HotkeyChord(HotkeyModifiers.Control, HotkeyKey.D7).Describe());
        Assert.AreEqual("Not assigned", HotkeyChord.Unassigned.Describe());
    }

    private static HotkeyBinding Binding(
        HotkeyAction action,
        HotkeyModifiers modifiers,
        HotkeyKey key,
        bool isEnabled = true) =>
        new(action, modifiers, key, isEnabled);

    private static void AssertIssue(
        IEnumerable<ConfigurationValidationIssue> issues,
        ConfigurationValidationCode code,
        string path)
    {
        ConfigurationValidationIssue issue = issues.Single(value => value.Code == code);
        Assert.AreEqual(path, issue.Path);
    }
}
