using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// Decides which hotkey bindings could never work, before anything is claimed
/// system-wide.
/// </summary>
/// <remarks>
/// <para>
/// Pure on purpose. <c>RegisterHotKey</c> cannot be reached from a test, so
/// every rule that can be decided without Windows is decided here, and the only
/// thing left for the live registrar to report is the one answer Windows alone
/// owns: whether another application already holds the combination.
/// </para>
/// <para>
/// A binding is judged on its own <see cref="HotkeyBinding.IsEnabled"/> flag,
/// not on the master switch. A row the user turned on is a row they intend to
/// press, and telling them it is broken only after they flip the master switch
/// would hide the mistake behind an unrelated toggle.
/// </para>
/// </remarks>
public static class HotkeyValidation
{
    /// <summary>The JSON path prefix every hotkey issue is reported under.</summary>
    public const string PathPrefix = "$.behavior.hotkeys";

    /// <summary>
    /// Reports every problem a chord set carries.
    /// </summary>
    /// <param name="bindings">The bindings as the document carries them.</param>
    /// <returns>One issue per problem, in document order.</returns>
    public static ImmutableArray<ConfigurationValidationIssue> Validate(
        ImmutableArray<HotkeyBinding> bindings,
        bool requireComplete = false)
    {
        if (bindings.IsDefaultOrEmpty && !requireComplete)
        {
            return [];
        }

        ImmutableArray<ConfigurationValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();
        HashSet<HotkeyAction> seenActions = [];
        Dictionary<HotkeyChord, HotkeyAction> claimedChords = [];

        for (int index = 0; index < bindings.Length; index++)
        {
            HotkeyBinding binding = bindings[index];
            string path = $"{PathPrefix}[{index}]";
            string action = binding.Action.ToString();

            if (!Enum.IsDefined(binding.Action))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.InvalidHotkeyValue,
                    $"Shortcut action value '{(int)binding.Action}' is not supported.",
                    $"{path}.action",
                    ConfigurationEntryKind.Hotkey,
                    action));
                continue;
            }

            // A duplicated action is broken however the switches are set: two
            // rows would fight over the same command and only one could win.
            if (!seenActions.Add(binding.Action))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.DuplicateHotkeyAction,
                    $"Shortcut action '{HotkeyDefaults.Describe(binding.Action)}' is bound more than once.",
                    $"{path}.action",
                    ConfigurationEntryKind.Hotkey,
                    action));
            }

            if (!binding.IsEnabled)
            {
                continue;
            }

            const HotkeyModifiers supportedModifiers =
                HotkeyModifiers.Alt |
                HotkeyModifiers.Control |
                HotkeyModifiers.Shift |
                HotkeyModifiers.Windows;
            if ((binding.Modifiers & ~supportedModifiers) != 0)
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.InvalidHotkeyValue,
                    $"Shortcut '{HotkeyDefaults.Describe(binding.Action)}' contains unsupported modifier bits.",
                    $"{path}.modifiers",
                    ConfigurationEntryKind.Hotkey,
                    action));
                continue;
            }

            if (!Enum.IsDefined(binding.Key))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.InvalidHotkeyValue,
                    $"Shortcut '{HotkeyDefaults.Describe(binding.Action)}' contains unsupported key value '{(int)binding.Key}'.",
                    $"{path}.key",
                    ConfigurationEntryKind.Hotkey,
                    action));
                continue;
            }

            if (!binding.Chord.IsAssigned)
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.HotkeyUnassigned,
                    $"Shortcut '{HotkeyDefaults.Describe(binding.Action)}' is turned on but has no key assigned.",
                    $"{path}.key",
                    ConfigurationEntryKind.Hotkey,
                    action));
                continue;
            }

            // A bare key would be taken away from every application on the
            // machine, so typing it would stop working everywhere. Windows will
            // happily register it, which is exactly why it is refused here.
            if (binding.Modifiers == HotkeyModifiers.None)
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.HotkeyMissingModifier,
                    $"Shortcut '{HotkeyDefaults.Describe(binding.Action)}' needs at least one of Ctrl, Alt, Shift, or Win. A key on its own would stop working in every other application.",
                    $"{path}.modifiers",
                    ConfigurationEntryKind.Hotkey,
                    action));
                continue;
            }

            if (claimedChords.TryGetValue(binding.Chord, out HotkeyAction owner))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.HotkeyConflict,
                    $"Shortcut '{binding.Chord.Describe()}' is already assigned to '{HotkeyDefaults.Describe(owner)}'.",
                    path,
                    ConfigurationEntryKind.Hotkey,
                    action));
                continue;
            }

            claimedChords.Add(binding.Chord, binding.Action);
        }

        if (requireComplete)
        {
            foreach (HotkeyAction action in HotkeyDefaults.Actions)
            {
                if (seenActions.Contains(action))
                {
                    continue;
                }

                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.MissingHotkeyAction,
                    $"Global shortcuts are enabled, but '{HotkeyDefaults.Describe(action)}' has no binding.",
                    PathPrefix,
                    ConfigurationEntryKind.Hotkey,
                    action.ToString()));
            }
        }

        return issues.ToImmutable();
    }

    /// <summary>
    /// The bindings that survived validation and may therefore be handed to the
    /// registrar.
    /// </summary>
    /// <remarks>
    /// Registering a chord that already failed validation would claim the
    /// combination twice and leave the second attempt reporting a conflict that
    /// DesktopShift caused itself.
    /// </remarks>
    /// <param name="bindings">The bindings as the document carries them.</param>
    /// <returns>The enabled, unambiguous, fully specified bindings.</returns>
    public static ImmutableArray<HotkeyBinding> Registrable(
        ImmutableArray<HotkeyBinding> bindings)
    {
        if (bindings.IsDefaultOrEmpty)
        {
            return [];
        }

        ImmutableArray<ConfigurationValidationIssue> issues = Validate(bindings);
        HashSet<string> rejected =
        [
            .. issues
                .Where(static issue => issue.EntryId is not null)
                .Select(static issue => issue.EntryId!),
        ];

        return
        [
            .. bindings.Where(binding =>
                binding.IsEnabled &&
                !rejected.Contains(binding.Action.ToString())),
        ];
    }
}
