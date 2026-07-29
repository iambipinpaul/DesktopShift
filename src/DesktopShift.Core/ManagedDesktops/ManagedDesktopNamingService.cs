using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// Makes Task View say what DesktopShift says.
/// </summary>
/// <remarks>
/// <para>
/// A Managed Desktop is identified by its semantic key and bound to a runtime
/// desktop by GUID. Neither of those needs a name, which is exactly why naming
/// gets its own service: it is the one part of managing a desktop that is
/// purely for the person looking at Win+Tab, and nothing else may come to
/// depend on it. A pass that fails in every possible way still leaves every
/// binding intact.
/// </para>
/// <para>
/// Two guards, for two different failures that look alike from a distance:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Readback.</b> After writing a name, the inventory is re-read. If Windows
/// stored something other than what was sent — truncated, normalized, rejected
/// — the key stops for the session. Without this, a name that can never match
/// is rewritten on every topology notification for as long as the app runs,
/// with no user involved. That is the only self-feeding loop naming can create,
/// and it is the reason this service reads back at all.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Concede.</b> A user renaming a desktop in Task View is answered once, and
/// then again, and then left alone. This loop is not self-feeding — writing the
/// name makes the next pass a no-op — so the bound is not about runaway. It is
/// about how many times it is reasonable to overrule somebody.
/// </description>
/// </item>
/// </list>
/// <para>
/// Names are not checked for uniqueness before being applied. Two desktops can
/// end up sharing one, and reconciliation reports that as an ambiguity if it
/// ever has to fall back to matching by name.
/// </para>
/// </remarks>
public sealed class ManagedDesktopNamingService : IManagedDesktopNamingService
{
    private readonly IDesktopTopologyProvider topologyProvider;
    private readonly IConfigurationService configurationService;
    private readonly TimeProvider timeProvider;
    private readonly ManagedDesktopNamingOptions options;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, KeyState> states =
        new(StringComparer.OrdinalIgnoreCase);

    public ManagedDesktopNamingService(
        IDesktopTopologyProvider topologyProvider,
        IConfigurationService configurationService,
        TimeProvider timeProvider,
        ManagedDesktopNamingOptions? options = null)
    {
        this.topologyProvider =
            topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
        this.configurationService =
            configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        this.timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? ManagedDesktopNamingOptions.Default;
    }

    public async Task<ManagedDesktopNamingPass> ApplyAsync(
        ManagedDesktopReconciliationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Guid correlationId = Guid.NewGuid();
        ImmutableArray<ManagedDesktopRuntimeMapping> bound = snapshot.Mappings
            .Where(static mapping => mapping.IsBound && mapping.RuntimeDesktopId is not null)
            .ToImmutableArray();
        if (bound.IsEmpty)
        {
            return new ManagedDesktopNamingPass(correlationId, timeProvider.GetUtcNow(), []);
        }

        if (!IsNamingEnabled())
        {
            return Uniform(
                correlationId,
                bound,
                ManagedDesktopNamingOutcome.Unavailable,
                "managed_desktops.naming_disabled",
                "Naming Windows desktops is switched off in Settings, so Task View keeps whatever names it already had.");
        }

        if (!topologyProvider.Capabilities.CanRenameDesktop)
        {
            return Uniform(
                correlationId,
                bound,
                ManagedDesktopNamingOutcome.Unavailable,
                "managed_desktops.naming_unsupported",
                "This Windows build did not prove it lays the shell manager out where desktop naming lives, so Task View keeps its own names.");
        }

        ImmutableArray<ManagedDesktopNamingResult>.Builder results =
            ImmutableArray.CreateBuilder<ManagedDesktopNamingResult>(bound.Length);
        List<PendingWrite> written = [];

        foreach (ManagedDesktopRuntimeMapping mapping in bound)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid runtimeId = mapping.RuntimeDesktopId!.Value;
            string desiredName = mapping.DisplayName.Trim();
            string? observedName = Normalize(mapping.RuntimeDisplayName);

            if (NamesMatch(observedName, desiredName))
            {
                // Steady state. Recording the agreement matters: it is what
                // makes the *next* disagreement identifiable as the user
                // renaming the desktop rather than as a first naming.
                NoteAgreement(mapping.SemanticKey, runtimeId, desiredName);
                results.Add(new ManagedDesktopNamingResult(
                    mapping.SemanticKey,
                    desiredName,
                    observedName,
                    runtimeId,
                    ManagedDesktopNamingOutcome.AlreadyNamed,
                    "managed_desktops.naming_not_needed",
                    $"The Windows desktop bound to '{mapping.SemanticKey}' is already called '{desiredName}'."));
                continue;
            }

            NamingDecision decision = Decide(mapping.SemanticKey, runtimeId, desiredName);
            if (!decision.IsAllowed)
            {
                results.Add(new ManagedDesktopNamingResult(
                    mapping.SemanticKey,
                    desiredName,
                    observedName,
                    runtimeId,
                    decision.Outcome,
                    decision.Code,
                    decision.Explanation));
                continue;
            }

            DesktopTopologyProviderResult renameResult;
            try
            {
                renameResult = await topologyProvider
                    .RenameDesktopAsync(runtimeId, desiredName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                results.Add(new ManagedDesktopNamingResult(
                    mapping.SemanticKey,
                    desiredName,
                    observedName,
                    runtimeId,
                    ManagedDesktopNamingOutcome.Failed,
                    "managed_desktops.naming_threw",
                    $"Naming the Windows desktop bound to '{mapping.SemanticKey}' failed: {exception.Message}"));
                continue;
            }

            if (!renameResult.IsSuccess)
            {
                DesktopTopologyProviderError error =
                    renameResult.Error ??
                    new DesktopTopologyProviderError(
                        "managed_desktops.naming_failed",
                        "The provider did not name the desktop and did not say why.");
                results.Add(new ManagedDesktopNamingResult(
                    mapping.SemanticKey,
                    desiredName,
                    observedName,
                    runtimeId,
                    ManagedDesktopNamingOutcome.Failed,
                    error.Code,
                    error.Message));
                continue;
            }

            written.Add(new PendingWrite(
                mapping.SemanticKey,
                runtimeId,
                desiredName,
                observedName));
        }

        if (written.Count > 0)
        {
            results.AddRange(
                await VerifyAsync(written, cancellationToken).ConfigureAwait(false));
        }

        return new ManagedDesktopNamingPass(
            correlationId,
            timeProvider.GetUtcNow(),
            results.ToImmutable());
    }

    /// <summary>
    /// Reads the inventory back once and asks Windows what it actually stored.
    /// </summary>
    /// <remarks>
    /// One enumeration for the whole pass rather than one per desktop. The
    /// answer that matters is what the inventory says after every write, and
    /// asking four times would only invite four different answers.
    /// </remarks>
    private async Task<ImmutableArray<ManagedDesktopNamingResult>> VerifyAsync(
        List<PendingWrite> written,
        CancellationToken cancellationToken)
    {
        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> inventory;
        try
        {
            inventory = await topologyProvider
                .EnumerateDesktopsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            return Unverified(written, exception.Message);
        }

        if (!inventory.IsSuccess || inventory.Value is null)
        {
            return Unverified(
                written,
                inventory.Error?.Message ?? "The desktop inventory was unavailable.");
        }

        Dictionary<Guid, string?> namesById = inventory.Value.ToDictionary(
            static desktop => desktop.Id,
            static desktop => Normalize(desktop.DisplayName));

        ImmutableArray<ManagedDesktopNamingResult>.Builder results =
            ImmutableArray.CreateBuilder<ManagedDesktopNamingResult>(written.Count);

        foreach (PendingWrite write in written)
        {
            if (!namesById.TryGetValue(write.RuntimeDesktopId, out string? storedName))
            {
                // The desktop went away between the write and the read. That is
                // an ordinary race, not a layout problem, so the key is left
                // free to try again rather than stopped.
                results.Add(new ManagedDesktopNamingResult(
                    write.SemanticKey,
                    write.DesiredName,
                    write.ObservedName,
                    write.RuntimeDesktopId,
                    ManagedDesktopNamingOutcome.Unverified,
                    "managed_desktops.naming_unverified",
                    $"The Windows desktop bound to '{write.SemanticKey}' was named, but it left the inventory before the name could be confirmed."));
                continue;
            }

            if (NamesMatch(storedName, write.DesiredName))
            {
                NoteApplied(write.SemanticKey, write.RuntimeDesktopId, write.DesiredName);
                results.Add(new ManagedDesktopNamingResult(
                    write.SemanticKey,
                    write.DesiredName,
                    storedName,
                    write.RuntimeDesktopId,
                    ManagedDesktopNamingOutcome.Applied,
                    "managed_desktops.naming_applied",
                    $"Task View now shows '{write.DesiredName}' for '{write.SemanticKey}'."));
                continue;
            }

            // Windows took the name and stored something else. Retrying cannot
            // converge, so this key stops until the app restarts.
            NoteNotStored(write.SemanticKey);
            results.Add(new ManagedDesktopNamingResult(
                write.SemanticKey,
                write.DesiredName,
                storedName,
                write.RuntimeDesktopId,
                ManagedDesktopNamingOutcome.NotStored,
                "managed_desktops.naming_not_stored",
                $"Windows accepted '{write.DesiredName}' for '{write.SemanticKey}' and then reported the desktop as '{storedName ?? "unnamed"}'. DesktopShift stopped naming this destination rather than rewriting it forever; give it a display name Windows will keep, then reconcile all."));
        }

        return results.ToImmutable();
    }

    private NamingDecision Decide(
        string semanticKey,
        Guid runtimeDesktopId,
        string desiredName)
    {
        lock (syncRoot)
        {
            if (!states.TryGetValue(semanticKey, out KeyState? state))
            {
                return NamingDecision.Allowed;
            }

            if (state.IsStopped)
            {
                return new NamingDecision(
                    false,
                    ManagedDesktopNamingOutcome.NotStored,
                    "managed_desktops.naming_not_stored",
                    $"DesktopShift stopped naming '{semanticKey}' this session because Windows did not store the name it was given.");
            }

            // A correction only counts when this exact desktop was previously
            // observed carrying the name and no longer does. Binding to a
            // different desktop is a fresh start, not a continuation of an
            // argument about the old one.
            bool isCorrection =
                state.AppliedToDesktopId == runtimeDesktopId &&
                state.AppliedName is not null;
            if (!isCorrection)
            {
                return NamingDecision.Allowed;
            }

            if (state.Corrections >= options.MaxCorrections)
            {
                return new NamingDecision(
                    false,
                    ManagedDesktopNamingOutcome.Conceded,
                    "managed_desktops.naming_conceded",
                    $"'{semanticKey}' has been renamed back {state.Corrections} times, so DesktopShift stopped renaming it and left the name you chose. Turn naming off in Settings to make that permanent, or rename the Managed Desktop to '{desiredName}' if you want them to agree.");
            }

            return NamingDecision.Allowed;
        }
    }

    private void NoteAgreement(string semanticKey, Guid runtimeDesktopId, string name)
    {
        lock (syncRoot)
        {
            KeyState state = GetOrAdd(semanticKey);
            state.AppliedToDesktopId = runtimeDesktopId;
            state.AppliedName = name;
        }
    }

    private void NoteApplied(string semanticKey, Guid runtimeDesktopId, string name)
    {
        lock (syncRoot)
        {
            KeyState state = GetOrAdd(semanticKey);
            bool isCorrection =
                state.AppliedToDesktopId == runtimeDesktopId &&
                state.AppliedName is not null;
            if (isCorrection)
            {
                state.Corrections++;
            }

            state.AppliedToDesktopId = runtimeDesktopId;
            state.AppliedName = name;
        }
    }

    private void NoteNotStored(string semanticKey)
    {
        lock (syncRoot)
        {
            GetOrAdd(semanticKey).IsStopped = true;
        }
    }

    private KeyState GetOrAdd(string semanticKey)
    {
        if (states.TryGetValue(semanticKey, out KeyState? state))
        {
            return state;
        }

        state = new KeyState();
        states.Add(semanticKey, state);
        return state;
    }

    private bool IsNamingEnabled() =>
        configurationService.CurrentState.Active?.Behavior.NameWindowsDesktops ?? false;

    private ManagedDesktopNamingPass Uniform(
        Guid correlationId,
        ImmutableArray<ManagedDesktopRuntimeMapping> bound,
        ManagedDesktopNamingOutcome outcome,
        string code,
        string explanation) =>
        new(
            correlationId,
            timeProvider.GetUtcNow(),
            bound
                .Select(mapping => new ManagedDesktopNamingResult(
                    mapping.SemanticKey,
                    mapping.DisplayName.Trim(),
                    Normalize(mapping.RuntimeDisplayName),
                    mapping.RuntimeDesktopId,
                    outcome,
                    code,
                    explanation))
                .ToImmutableArray());

    private static ImmutableArray<ManagedDesktopNamingResult> Unverified(
        List<PendingWrite> written,
        string reason) =>
        written
            .Select(write => new ManagedDesktopNamingResult(
                write.SemanticKey,
                write.DesiredName,
                write.ObservedName,
                write.RuntimeDesktopId,
                ManagedDesktopNamingOutcome.Unverified,
                "managed_desktops.naming_unverified",
                $"The Windows desktop bound to '{write.SemanticKey}' was named, but the name could not be confirmed: {reason}"))
            .ToImmutableArray();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Whether Task View is already showing the configured name.
    /// </summary>
    /// <remarks>
    /// Case-sensitive, unlike the comparison reconciliation uses to match a
    /// desktop to a definition. The two are asking different questions:
    /// reconciliation asks "is this the same destination", where case is noise,
    /// and this asks "is the label right", where a desktop reading "code" when
    /// the user configured "Code" is not yet right.
    /// </remarks>
    private static bool NamesMatch(string? observed, string desired) =>
        observed is not null &&
        string.Equals(observed, desired.Trim(), StringComparison.Ordinal);

    private readonly record struct PendingWrite(
        string SemanticKey,
        Guid RuntimeDesktopId,
        string DesiredName,
        string? ObservedName);

    private readonly record struct NamingDecision(
        bool IsAllowed,
        ManagedDesktopNamingOutcome Outcome,
        string Code,
        string Explanation)
    {
        public static NamingDecision Allowed { get; } = new(
            true,
            ManagedDesktopNamingOutcome.Applied,
            string.Empty,
            string.Empty);
    }

    private sealed class KeyState
    {
        /// <summary>
        /// The desktop this key was last observed agreeing with, so a later
        /// disagreement about the same desktop is recognizable as a user rename
        /// rather than as a first naming.
        /// </summary>
        public Guid? AppliedToDesktopId { get; set; }

        public string? AppliedName { get; set; }

        public int Corrections { get; set; }

        public bool IsStopped { get; set; }
    }
}
