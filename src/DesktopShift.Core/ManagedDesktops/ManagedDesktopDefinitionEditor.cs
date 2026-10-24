using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// The result of one attempted edit to the Managed Desktop definitions.
/// </summary>
/// <param name="Document">
/// The edited document, or the unchanged source document when the edit was
/// rejected or would have changed nothing. Reference equality with the source
/// is what distinguishes "no change" from "changed".
/// </param>
public sealed record ManagedDesktopEditResult(
    bool IsAccepted,
    ConfigurationDocument Document,
    string Summary,
    ImmutableArray<ManagedDesktopValidationMessage> Messages);

/// <summary>
/// Edits the Managed Desktop definitions of a configuration document.
/// </summary>
/// <remarks>
/// <para>
/// Every operation is a pure function of the document it is given, so the
/// decision to accept or reject an edit is testable without a file, a virtual
/// desktop, or a UI.
/// </para>
/// <para>
/// What is checked here are the preconditions of the operation itself: you
/// cannot add a key that is already managed, and you cannot rename a definition
/// that does not exist. Document-level validation stays where it already lives,
/// in the configuration validator, and is surfaced rather than repeated. The
/// distinction matters because a rejected precondition means nothing was
/// written at all, so a failed edit can never leave a broken document behind.
/// </para>
/// <para>
/// Removing a definition is the one operation with a safety consequence. It
/// removes the definition and nothing else: no virtual-desktop call is made,
/// and the Windows desktop the definition was mapped to keeps existing with all
/// of its windows. Deleting a real desktop is a separate destructive action
/// that this editor deliberately cannot express.
/// </para>
/// </remarks>
public static class ManagedDesktopDefinitionEditor
{
    public static ManagedDesktopEditResult Add(
        ConfigurationDocument document,
        ManagedDesktopDraft draft)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(draft);

        string semanticKey = draft.SemanticKey?.Trim() ?? string.Empty;
        string displayName = draft.DisplayName?.Trim() ?? string.Empty;

        if (semanticKey.Length == 0)
        {
            return Reject(
                document,
                "managed_desktops.edit.key_required",
                "A semantic key is required",
                "A Managed Desktop was not added because no semantic key was given.",
                "Enter a short, stable key such as 'code'. Application Rules name their destination by this key, so it cannot be blank.");
        }

        if (displayName.Length == 0)
        {
            return Reject(
                document,
                "managed_desktops.edit.name_required",
                "A display name is required",
                $"'{semanticKey}' was not added because no display name was given.",
                "Enter the name the desktop should carry in Task View, such as 'Code'.",
                semanticKey);
        }

        ManagedDesktopDefinition? existing = Find(document, semanticKey);
        if (existing is not null)
        {
            return Reject(
                document,
                "managed_desktops.edit.duplicate_key",
                "That semantic key is already managed",
                $"'{existing.DisplayName}' already uses the semantic key '{existing.SemanticKey}'.",
                "Choose a different key. Two Managed Desktops sharing one key would make every rule that targets it ambiguous.",
                semanticKey);
        }

        List<ManagedDesktopDefinition> ordered = [.. Normalize(document.ManagedDesktops)];
        ordered.Add(new ManagedDesktopDefinition(
            semanticKey,
            displayName,
            ordered.Count + 1,
            draft.RecreateWhenMissing));

        return Accept(
            document,
            Renumber(ordered),
            $"'{displayName}' is now managed as '{semanticKey}' at preferred position {ordered.Count}.");
    }

    public static ManagedDesktopEditResult Rename(
        ConfigurationDocument document,
        string semanticKey,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(document);

        ManagedDesktopDefinition? definition = Find(document, semanticKey);
        if (definition is null)
        {
            return RejectUnknownKey(document, semanticKey);
        }

        string trimmed = displayName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Reject(
                document,
                "managed_desktops.edit.name_required",
                "A display name is required",
                $"'{definition.SemanticKey}' was not renamed because the new name was blank.",
                "Enter the name the desktop should carry in Task View.",
                definition.SemanticKey);
        }

        if (string.Equals(definition.DisplayName, trimmed, StringComparison.Ordinal))
        {
            return NoChange(document, $"'{definition.DisplayName}' already has that name.");
        }

        return Accept(
            document,
            Replace(document.ManagedDesktops, definition with { DisplayName = trimmed }),
            $"'{definition.DisplayName}' is now called '{trimmed}'. Its semantic key '{definition.SemanticKey}' is unchanged, so every rule that targets it still applies.");
    }

    public static ManagedDesktopEditResult SetRecreationPolicy(
        ConfigurationDocument document,
        string semanticKey,
        bool recreateWhenMissing)
    {
        ArgumentNullException.ThrowIfNull(document);

        ManagedDesktopDefinition? definition = Find(document, semanticKey);
        if (definition is null)
        {
            return RejectUnknownKey(document, semanticKey);
        }

        if (definition.RecreateWhenMissing == recreateWhenMissing)
        {
            return NoChange(
                document,
                recreateWhenMissing
                    ? $"'{definition.DisplayName}' is already recreated when missing."
                    : $"'{definition.DisplayName}' is already left alone when missing.");
        }

        return Accept(
            document,
            Replace(
                document.ManagedDesktops,
                definition with { RecreateWhenMissing = recreateWhenMissing }),
            recreateWhenMissing
                ? $"'{definition.DisplayName}' will be recreated when its Windows desktop is missing."
                : $"'{definition.DisplayName}' will no longer be recreated when its Windows desktop is missing.");
    }

    /// <summary>
    /// Moves a definition one place earlier or later in preferred order.
    /// </summary>
    /// <remarks>
    /// This changes the configured preference. The maintenance service follows
    /// an accepted edit by moving the bound Windows desktop when the validated
    /// provider supports Task View reordering.
    /// </remarks>
    public static ManagedDesktopEditResult Move(
        ConfigurationDocument document,
        string semanticKey,
        ManagedDesktopMoveDirection direction)
    {
        ArgumentNullException.ThrowIfNull(document);

        ManagedDesktopDefinition? definition = Find(document, semanticKey);
        if (definition is null)
        {
            return RejectUnknownKey(document, semanticKey);
        }

        ImmutableArray<ManagedDesktopDefinition> ordered = Normalize(
            document.ManagedDesktops);
        int index = IndexOf(ordered, definition.SemanticKey);
        int target = direction == ManagedDesktopMoveDirection.Earlier
            ? index - 1
            : index + 1;

        if (target < 0 || target >= ordered.Length)
        {
            return NoChange(
                document,
                direction == ManagedDesktopMoveDirection.Earlier
                    ? $"'{definition.DisplayName}' is already first."
                    : $"'{definition.DisplayName}' is already last.");
        }

        List<ManagedDesktopDefinition> moved = [.. ordered];
        (moved[index], moved[target]) = (moved[target], moved[index]);

        return Accept(
            document,
            Renumber(moved),
            $"'{definition.DisplayName}' moved to preferred position {target + 1}.");
    }

    /// <summary>
    /// Stops managing a definition, leaving the real Windows desktop in place.
    /// </summary>
    /// <remarks>
    /// A definition that Application Rules still target is not removed. Removing
    /// it would leave those rules pointing at a destination that no longer
    /// exists, which the configuration validator rejects — so the whole document
    /// would stop being accepted and the removal would silently fail to take
    /// effect. Refusing up front, and naming the rules, is the actionable form
    /// of the same answer.
    /// </remarks>
    public static ManagedDesktopEditResult Remove(
        ConfigurationDocument document,
        string semanticKey)
    {
        ArgumentNullException.ThrowIfNull(document);

        ManagedDesktopDefinition? definition = Find(document, semanticKey);
        if (definition is null)
        {
            return RejectUnknownKey(document, semanticKey);
        }

        ImmutableArray<ApplicationRule> dependents = [.. document.ApplicationRules
            .Where(rule =>
                rule.MovesWindows &&
                KeysEqual(rule.TargetDesktopKey, definition.SemanticKey))];
        if (!dependents.IsEmpty)
        {
            string names = string.Join(
                ", ",
                dependents.Select(static rule => $"'{rule.DisplayName}'"));
            string count = dependents.Length == 1
                ? "1 Application Rule"
                : $"{dependents.Length} Application Rules";
            return Reject(
                document,
                "managed_desktops.edit.desktop_in_use",
                "That Managed Desktop is still targeted by rules",
                $"{count} still send windows to '{definition.DisplayName}': {names}.",
                "Repoint or remove those rules on the Rules page first. Nothing was changed, and the Windows desktop was not touched.",
                definition.SemanticKey);
        }

        List<ManagedDesktopDefinition> remaining = [.. Normalize(document.ManagedDesktops)
            .Where(item => !KeysEqual(item.SemanticKey, definition.SemanticKey))];

        return Accept(
            document,
            Renumber(remaining),
            $"'{definition.DisplayName}' is no longer managed by DesktopShift. The Windows desktop itself was left in place with all of its windows; deleting a real desktop is a separate action DesktopShift does not perform.");
    }

    private static ManagedDesktopDefinition? Find(
        ConfigurationDocument document,
        string? semanticKey)
    {
        if (string.IsNullOrWhiteSpace(semanticKey))
        {
            return null;
        }

        string key = semanticKey.Trim();
        return document.ManagedDesktops.FirstOrDefault(
            definition => KeysEqual(definition.SemanticKey, key));
    }

    private static int IndexOf(
        ImmutableArray<ManagedDesktopDefinition> definitions,
        string semanticKey)
    {
        for (int index = 0; index < definitions.Length; index++)
        {
            if (KeysEqual(definitions[index].SemanticKey, semanticKey))
            {
                return index;
            }
        }

        return -1;
    }

    private static ImmutableArray<ManagedDesktopDefinition> Replace(
        ImmutableArray<ManagedDesktopDefinition> definitions,
        ManagedDesktopDefinition replacement) =>
        [.. definitions.Select(definition =>
            KeysEqual(definition.SemanticKey, replacement.SemanticKey)
                ? replacement
                : definition)];

    /// <summary>
    /// Sorts definitions into preferred order without changing their numbers.
    /// </summary>
    private static ImmutableArray<ManagedDesktopDefinition> Normalize(
        ImmutableArray<ManagedDesktopDefinition> definitions) =>
        [.. definitions
            .OrderBy(static definition => definition.PreferredOrder)
            .ThenBy(
                static definition => definition.SemanticKey,
                StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Renumbers an already-ordered sequence to a dense 1-based order.
    /// </summary>
    /// <remarks>
    /// Dense numbering is what makes "move up" mean one place rather than an
    /// unpredictable jump, and it repairs gaps and ties left by a hand-edited
    /// document.
    /// </remarks>
    private static ImmutableArray<ManagedDesktopDefinition> Renumber(
        IReadOnlyList<ManagedDesktopDefinition> ordered)
    {
        ImmutableArray<ManagedDesktopDefinition>.Builder builder =
            ImmutableArray.CreateBuilder<ManagedDesktopDefinition>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            builder.Add(ordered[index] with { PreferredOrder = index + 1 });
        }

        return builder.ToImmutable();
    }

    private static ManagedDesktopEditResult Accept(
        ConfigurationDocument document,
        ImmutableArray<ManagedDesktopDefinition> definitions,
        string summary) =>
        new(
            IsAccepted: true,
            document with { ManagedDesktops = definitions },
            summary,
            []);

    private static ManagedDesktopEditResult NoChange(
        ConfigurationDocument document,
        string summary) =>
        new(IsAccepted: true, document, summary, []);

    private static ManagedDesktopEditResult RejectUnknownKey(
        ConfigurationDocument document,
        string? semanticKey) =>
        Reject(
            document,
            "managed_desktops.edit.unknown_key",
            "That Managed Desktop no longer exists",
            $"No Managed Desktop is defined with the semantic key '{semanticKey}'.",
            "Refresh the page. Another change may have removed it since this view was drawn.",
            semanticKey);

    private static ManagedDesktopEditResult Reject(
        ConfigurationDocument document,
        string code,
        string title,
        string message,
        string remedy,
        string? semanticKey = null) =>
        new(
            IsAccepted: false,
            document,
            message,
            [new ManagedDesktopValidationMessage(
                ManagedDesktopValidationScope.Configuration,
                ManagedDesktopValidationSeverity.Error,
                code,
                title,
                message,
                remedy,
                semanticKey)]);

    private static bool KeysEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
