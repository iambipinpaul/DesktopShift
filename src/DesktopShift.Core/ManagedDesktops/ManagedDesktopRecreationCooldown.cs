using System.Collections.Immutable;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// The bounds a <see cref="ManagedDesktopRecreationCooldown"/> enforces.
/// </summary>
/// <remarks>
/// Every bound is counted in topology notifications rather than in elapsed time.
/// A wall-clock cooldown would expire while the user is still deleting the
/// desktop, and would also expire on a machine nobody is touching, where there
/// is nothing to cool down from.
/// </remarks>
/// <param name="MaxRecreations">
/// How many desktops may be recreated for one semantic key before the key goes
/// into cooldown. The first recreation is the useful one — a desktop was lost to
/// a crash or a stray click. By the third, the user is telling DesktopShift
/// something, and it is time to stop answering.
/// </param>
/// <param name="RecreationWindowTopologyEvents">
/// How many notifications may pass after a recreation before the key is
/// considered calm again and its recreation count resets. Deleting one desktop
/// in Task View raises several notifications, so this is generous enough that a
/// genuine delete-recreate-delete sequence still counts as repeated.
/// </param>
/// <param name="CooldownTopologyEvents">
/// How many notifications a key stays in cooldown for. This is the hard bound:
/// recreation cannot be suppressed forever, and the user needs no hidden reset.
/// </param>
public sealed record ManagedDesktopRecreationCooldownOptions(
    int MaxRecreations = 3,
    int RecreationWindowTopologyEvents = 16,
    int CooldownTopologyEvents = 32)
{
    public static ManagedDesktopRecreationCooldownOptions Default { get; } = new();

    public int MaxRecreations { get; init; } =
        MaxRecreations > 0
            ? MaxRecreations
            : throw new ArgumentOutOfRangeException(nameof(MaxRecreations));

    public int RecreationWindowTopologyEvents { get; init; } =
        RecreationWindowTopologyEvents > 0
            ? RecreationWindowTopologyEvents
            : throw new ArgumentOutOfRangeException(
                nameof(RecreationWindowTopologyEvents));

    public int CooldownTopologyEvents { get; init; } =
        CooldownTopologyEvents > 0
            ? CooldownTopologyEvents
            : throw new ArgumentOutOfRangeException(nameof(CooldownTopologyEvents));
}

/// <summary>
/// Stops a Managed Desktop the user keeps deleting from being recreated forever.
/// </summary>
/// <remarks>
/// <para>
/// Recreating a deleted managed destination is right the first time and wrong
/// the fifth. Without a bound the two sides never settle: the user deletes the
/// desktop, Windows reports the deletion, DesktopShift recreates it, Windows
/// reports the creation, and the loop feeds itself.
/// </para>
/// <para>
/// The bound is deliberately owned here rather than inside
/// <see cref="IManagedDesktopMaintenanceService.RecreateAsync"/>. Maintenance
/// runs because a user pressed a button and must always do as it is told; this
/// gate runs because Windows raised a notification, which is the only place a
/// runaway loop can start.
/// </para>
/// </remarks>
public sealed class ManagedDesktopRecreationCooldown : IManagedDesktopRecreationGate
{
    /// <summary>
    /// The diagnostic code every suppressed recreation carries, from the runtime
    /// mapping through to the activity journal.
    /// </summary>
    public const string SuppressedCode = "managed_desktops.recreation_suppressed";

    private readonly ManagedDesktopRecreationCooldownOptions options;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, KeyState> states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManagedDesktopRecreationSuppression> pendingSuppressions = [];

    public ManagedDesktopRecreationCooldown()
        : this(ManagedDesktopRecreationCooldownOptions.Default)
    {
    }

    public ManagedDesktopRecreationCooldown(
        ManagedDesktopRecreationCooldownOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ManagedDesktopRecreationDecision Evaluate(string semanticKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        lock (syncRoot)
        {
            if (!states.TryGetValue(semanticKey, out KeyState? state) ||
                state.CooldownRemaining <= 0)
            {
                return ManagedDesktopRecreationDecision.Allowed;
            }

            string reason =
                $"'{semanticKey}' has been recreated {state.Recreations} times and deleted again each time, " +
                $"so DesktopShift stopped recreating it. It will try again after " +
                $"{Describe(state.CooldownRemaining)} of desktop activity, or immediately if you recreate it yourself.";
            pendingSuppressions.Add(
                new ManagedDesktopRecreationSuppression(
                    semanticKey,
                    SuppressedCode,
                    reason,
                    state.CooldownRemaining));
            return new ManagedDesktopRecreationDecision(
                IsAllowed: false,
                SuppressedCode,
                reason);
        }
    }

    public void NoteRecreated(string semanticKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        lock (syncRoot)
        {
            if (!states.TryGetValue(semanticKey, out KeyState? state))
            {
                state = new KeyState();
                states.Add(semanticKey, state);
            }

            state.Recreations++;
            state.EventsSinceRecreation = 0;

            if (state.Recreations >= options.MaxRecreations)
            {
                state.CooldownRemaining = options.CooldownTopologyEvents;
            }
        }
    }

    public void NoteTopologyEvent()
    {
        lock (syncRoot)
        {
            List<string>? settled = null;

            foreach ((string semanticKey, KeyState state) in states)
            {
                if (state.CooldownRemaining > 0)
                {
                    state.CooldownRemaining--;
                    if (state.CooldownRemaining > 0)
                    {
                        continue;
                    }

                    // The cooldown is spent. The key starts over with a clean
                    // count, so the next deletion gets the same good-faith
                    // recreation the first one did.
                    settled ??= [];
                    settled.Add(semanticKey);
                    continue;
                }

                state.EventsSinceRecreation++;
                if (state.EventsSinceRecreation >= options.RecreationWindowTopologyEvents)
                {
                    settled ??= [];
                    settled.Add(semanticKey);
                }
            }

            if (settled is null)
            {
                return;
            }

            foreach (string semanticKey in settled)
            {
                _ = states.Remove(semanticKey);
            }
        }
    }

    public ImmutableArray<ManagedDesktopRecreationSuppression> DrainSuppressions()
    {
        lock (syncRoot)
        {
            if (pendingSuppressions.Count == 0)
            {
                return [];
            }

            ImmutableArray<ManagedDesktopRecreationSuppression> drained =
                [.. pendingSuppressions];
            pendingSuppressions.Clear();
            return drained;
        }
    }

    /// <summary>
    /// How many further topology notifications the named key stays suppressed
    /// for, or zero when it is not suppressed.
    /// </summary>
    public int GetRemainingCooldown(string semanticKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        lock (syncRoot)
        {
            return states.TryGetValue(semanticKey, out KeyState? state)
                ? Math.Max(0, state.CooldownRemaining)
                : 0;
        }
    }

    private static string Describe(int events) =>
        events == 1 ? "1 more desktop change" : $"{events} more desktop changes";

    private sealed class KeyState
    {
        public int Recreations { get; set; }

        public int EventsSinceRecreation { get; set; }

        public int CooldownRemaining { get; set; }
    }
}
