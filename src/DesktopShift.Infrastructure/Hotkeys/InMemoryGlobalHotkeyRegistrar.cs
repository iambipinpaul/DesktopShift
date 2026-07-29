using System.Collections.Immutable;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Infrastructure.Hotkeys;

/// <summary>
/// One attempt to claim a combination, as the in-memory registrar recorded it.
/// </summary>
/// <param name="Action">The action the chord was claimed for.</param>
/// <param name="Chord">The combination that was asked for.</param>
/// <param name="Succeeded">Whether the registrar agreed to hold it.</param>
public sealed record HotkeyRegistrationAttempt(
    HotkeyAction Action,
    HotkeyChord Chord,
    bool Succeeded);

/// <summary>
/// A registrar that remembers what it was asked for and claims nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is a safety mechanism, not a convenience. <c>RegisterHotKey</c> takes a
/// combination away from every other application on the machine for as long as
/// it is held, and it needs a real message loop to deliver anything. Registering
/// this as the default means a test host — or any host that has not deliberately
/// asked for the platform implementation — cannot claim a combination on the
/// machine it runs on, in the same way
/// <see cref="Hosting.InMemoryStartupRegistration"/> means no test can write the
/// machine's real login state.
/// </para>
/// <para>
/// It records every attempt and every release so the ordering rules above it can
/// be asserted, and it can be told to refuse a chord so the failure path is
/// reachable without needing another application to hold the combination first.
/// </para>
/// </remarks>
public sealed class InMemoryGlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<HotkeyChord, HotkeyRegistrationOutcome> _refusals = [];
    private readonly Dictionary<HotkeyAction, HotkeyChord> _held = [];
    private readonly List<HotkeyRegistrationAttempt> _attempts = [];
    private int _unregisterAllCount;
    private int _disposeCount;

    public event EventHandler<HotkeyInvokedEventArgs>? Pressed;

    /// <summary>Every attempt, in the order it was made.</summary>
    public ImmutableArray<HotkeyRegistrationAttempt> Attempts
    {
        get
        {
            lock (_syncRoot)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>The combinations being held right now, by action.</summary>
    public ImmutableArray<HotkeyRegistration> Held
    {
        get
        {
            lock (_syncRoot)
            {
                return
                [
                    .. _held.Select(entry =>
                        new HotkeyRegistration(entry.Key, entry.Value, true)),
                ];
            }
        }
    }

    /// <summary>How many combinations are held right now.</summary>
    public int HeldCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>How many times every combination was released.</summary>
    public int UnregisterAllCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _unregisterAllCount;
            }
        }
    }

    /// <summary>How many times this registrar was disposed.</summary>
    public int DisposeCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _disposeCount;
            }
        }
    }

    /// <summary>Whether this registrar has been disposed.</summary>
    public bool IsDisposed => DisposeCount > 0;

    /// <summary>
    /// Makes one combination fail to register, the way Windows fails a chord
    /// another application already owns.
    /// </summary>
    /// <param name="chord">The combination to refuse.</param>
    /// <param name="failureMessage">Why it was refused.</param>
    /// <param name="nativeErrorCode">
    /// The Win32 error to report, defaulting to <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>.
    /// </param>
    public void Refuse(
        HotkeyChord chord,
        string failureMessage = "Another application is already using it.",
        int? nativeErrorCode = 1409)
    {
        lock (_syncRoot)
        {
            _refusals[chord] = new HotkeyRegistrationOutcome(
                false,
                failureMessage,
                nativeErrorCode);
        }
    }

    /// <summary>
    /// Raises <see cref="Pressed"/> for an action, whether or not a combination
    /// is being held for it.
    /// </summary>
    /// <remarks>
    /// Deliberately unconditional. A real hotkey message can already be sitting
    /// in the thread's queue when the combination is released, so the "press an
    /// action nobody is holding" case has to be reachable to prove the
    /// coordinator drops it.
    /// </remarks>
    public void SimulatePress(HotkeyAction action) =>
        Pressed?.Invoke(this, new HotkeyInvokedEventArgs(action));

    public HotkeyRegistrationOutcome Register(
        HotkeyAction action,
        HotkeyChord chord)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            HotkeyRegistrationOutcome outcome =
                _refusals.TryGetValue(chord, out HotkeyRegistrationOutcome? refusal)
                    ? refusal
                    : HotkeyRegistrationOutcome.Success;
            if (outcome.Succeeded)
            {
                _held[action] = chord;
            }

            _attempts.Add(
                new HotkeyRegistrationAttempt(action, chord, outcome.Succeeded));
            return outcome;
        }
    }

    public void UnregisterAll()
    {
        lock (_syncRoot)
        {
            _unregisterAllCount++;
            _held.Clear();
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _disposeCount++;
            _held.Clear();
        }

        Pressed = null;
    }
}
