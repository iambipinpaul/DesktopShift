using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// Turns the two independent sources of Managed Desktop trouble into messages a
/// user can act on.
/// </summary>
/// <remarks>
/// <para>
/// The configuration validator already decides what is wrong with a saved
/// document — a duplicated semantic key, a rule naming a desktop that does not
/// exist. That judgement is not repeated here; its issues are translated into
/// the words and the remedy the Managed Desktops page can show.
/// </para>
/// <para>
/// A runtime ambiguity is a different failure with a different fix. Two live
/// Windows desktops can both plausibly answer to one definition — most often
/// because they carry the same name — and no amount of configuration validation
/// can see that, because the document itself is perfectly valid. The two are
/// reported separately so a user is never told to fix their configuration when
/// the thing to fix is their desktops, or the reverse.
/// </para>
/// </remarks>
public static class ManagedDesktopValidationProjection
{
    public static ImmutableArray<ManagedDesktopValidationMessage> Project(
        ImmutableArray<ConfigurationValidationIssue> configurationIssues,
        ManagedDesktopReconciliationSnapshot snapshot,
        ManagedDesktopMaintenanceAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(availability);

        ImmutableArray<ManagedDesktopValidationMessage>.Builder messages =
            ImmutableArray.CreateBuilder<ManagedDesktopValidationMessage>();
        messages.AddRange(FromConfiguration(configurationIssues));
        messages.AddRange(FromRuntime(snapshot));

        if (!availability.CanReconcile &&
            !string.IsNullOrWhiteSpace(availability.ReconcileUnavailableReason) &&
            snapshot.Mappings.IsEmpty)
        {
            messages.Add(new ManagedDesktopValidationMessage(
                ManagedDesktopValidationScope.Runtime,
                ManagedDesktopValidationSeverity.Warning,
                "managed_desktops.runtime_operations_unavailable",
                "Runtime desktop operations are unavailable",
                availability.ReconcileUnavailableReason,
                "Definitions can still be added, renamed, reordered and removed; only the live mapping is on hold."));
        }

        return messages.ToImmutable();
    }

    /// <summary>
    /// Translates the configuration validator's Managed Desktop findings.
    /// </summary>
    public static ImmutableArray<ManagedDesktopValidationMessage> FromConfiguration(
        ImmutableArray<ConfigurationValidationIssue> issues)
    {
        if (issues.IsDefaultOrEmpty)
        {
            return [];
        }

        ImmutableArray<ManagedDesktopValidationMessage>.Builder messages =
            ImmutableArray.CreateBuilder<ManagedDesktopValidationMessage>();

        foreach (ConfigurationValidationIssue issue in issues)
        {
            ManagedDesktopValidationMessage? message = issue.Code switch
            {
                ConfigurationValidationCode.DuplicateDesktopSemanticKey => new(
                    ManagedDesktopValidationScope.Configuration,
                    ManagedDesktopValidationSeverity.Error,
                    "managed_desktops.duplicate_semantic_key",
                    "Two Managed Desktops share one semantic key",
                    issue.Message,
                    "Every Application Rule names its destination by semantic key, so a duplicated key makes the destination ambiguous. Give one of them a different key.",
                    issue.EntryId),
                ConfigurationValidationCode.UnknownDesktopReference => new(
                    ManagedDesktopValidationScope.Configuration,
                    ManagedDesktopValidationSeverity.Error,
                    "managed_desktops.unknown_desktop_reference",
                    "An Application Rule targets a Managed Desktop that does not exist",
                    issue.Message,
                    "Add a Managed Desktop with that semantic key, or repoint the rule on the Rules page."),
                ConfigurationValidationCode.RequiredValue when
                    issue.EntryKind == ConfigurationEntryKind.ManagedDesktop => new(
                    ManagedDesktopValidationScope.Configuration,
                    ManagedDesktopValidationSeverity.Error,
                    "managed_desktops.required_value",
                    "A Managed Desktop is missing a required value",
                    issue.Message,
                    "A Managed Desktop needs both a semantic key and a display name before it can be saved.",
                    issue.EntryId),
                _ => null,
            };

            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages.ToImmutable();
    }

    /// <summary>
    /// Reports the runtime resolution problems in a reconciliation snapshot.
    /// </summary>
    /// <remarks>
    /// A missing desktop is deliberately not reported here. Every definition
    /// already shows its own runtime state and its own recreate action, so
    /// repeating "missing" as a page-level message would only add noise to the
    /// case the list already explains.
    /// </remarks>
    public static ImmutableArray<ManagedDesktopValidationMessage> FromRuntime(
        ManagedDesktopReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ImmutableArray<ManagedDesktopValidationMessage>.Builder messages =
            ImmutableArray.CreateBuilder<ManagedDesktopValidationMessage>();

        foreach (ManagedDesktopRuntimeMapping mapping in snapshot.Mappings)
        {
            switch (mapping.Status)
            {
                case ManagedDesktopMappingStatus.Ambiguous:
                    messages.Add(CreateAmbiguityMessage(mapping));
                    break;
                case ManagedDesktopMappingStatus.Failed:
                    messages.Add(new ManagedDesktopValidationMessage(
                        ManagedDesktopValidationScope.Runtime,
                        ManagedDesktopValidationSeverity.Error,
                        mapping.Code,
                        $"'{mapping.DisplayName}' could not be resolved",
                        mapping.Explanation,
                        "Reconcile all to try again. If it keeps failing, the diagnostic code identifies the step that failed.",
                        mapping.SemanticKey));
                    break;
                default:
                    break;
            }
        }

        foreach (ManagedDesktopReconciliationIssue issue in snapshot.Issues)
        {
            messages.Add(new ManagedDesktopValidationMessage(
                ManagedDesktopValidationScope.Runtime,
                snapshot.Outcome == ManagedDesktopReconciliationOutcome.Failed
                    ? ManagedDesktopValidationSeverity.Error
                    : ManagedDesktopValidationSeverity.Warning,
                issue.Code,
                "Managed desktop reconciliation reported a problem",
                issue.Message,
                "Definitions can still be edited. Reconcile all once the reported condition is resolved."));
        }

        return messages.ToImmutable();
    }

    private static ManagedDesktopValidationMessage CreateAmbiguityMessage(
        ManagedDesktopRuntimeMapping mapping)
    {
        bool isNameCollision = mapping.CandidateDesktopIds.Length > 1;
        string message = isNameCollision
            ? $"{mapping.CandidateDesktopIds.Length} Windows desktops are named '{mapping.DisplayName}', so DesktopShift will not guess which one '{mapping.SemanticKey}' means."
            : mapping.Explanation;
        string remedy = isNameCollision
            ? "Rename all but one of them in Task View, or give this Managed Desktop a display name no other desktop uses, then reconcile all."
            : "Two Managed Desktops claim the same Windows desktop. Recreate one of them onto a desktop of its own, or remove the definition you no longer need.";

        return new ManagedDesktopValidationMessage(
            ManagedDesktopValidationScope.Runtime,
            ManagedDesktopValidationSeverity.Error,
            mapping.Code,
            $"More than one Windows desktop matches '{mapping.DisplayName}'",
            message,
            remedy,
            mapping.SemanticKey);
    }
}
