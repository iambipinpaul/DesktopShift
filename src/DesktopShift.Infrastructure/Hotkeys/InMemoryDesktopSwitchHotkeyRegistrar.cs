using System.Collections.Immutable;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Infrastructure.Hotkeys;

/// <summary>
/// One attempt to claim a desktop-switching combination, as the in-memory
/// registrar recorded it.
/// </summary>
public sealed record DesktopSwitchHotkeyRegistrationAttempt(
    int DesktopOrdinal,
    HotkeyChord Chord,
    bool Succeeded);

/// <summary>
/// A desktop-switching registrar that remembers what it was asked for and claims
/// nothing.
/// </summary>
/// <remarks>
/// The safe default, for the same reason
/// <see cref="InMemoryGlobalHotkeyRegistrar"/> is: a profile claims ten
/// combinations from every application on the machine and needs a real message
/// loop to deliver any of them, so a test host must not be able to reach the
/// platform implementation by accident.
/// </remarks>
public sealed class InMemoryDesktopSwitchHotkeyRegistrar :
    IDesktopSwitchHotkeyRegistrar
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<HotkeyChord, HotkeyRegistrationOutcome> _refusals = [];
    private readonly Dictionary<int, HotkeyChord> _held = [];
    private readonly List<DesktopSwitchHotkeyRegistrationAttempt> _attempts = [];
    private int _unregisterAllCount;
    private int _disposeCount;

    public event EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? Pressed;

    /// <summary>Every attempt, in the order it was made.</summary>
    public ImmutableArray<DesktopSwitchHotkeyRegistrationAttempt> Attempts
    {
        get
        {
            lock (_syncRoot)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>The desktop positions being held right now, in ascending order.</summary>
    public ImmutableArray<int> HeldDesktopOrdinals
    {
        get
        {
            lock (_syncRoot)
            {
                return [.. _held.Keys.Order()];
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
    /// Makes one combination fail to register, the way Windows fails a chord the
    /// taskbar already owns.
    /// </summary>
    public void Refuse(
        HotkeyChord chord,
        string failureMessage = "Another application is already using this shortcut.",
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
    /// Raises <see cref="Pressed"/> for a desktop position, whether or not a
    /// combination is being held for it.
    /// </summary>
    /// <remarks>
    /// Deliberately unconditional. A real hotkey message can already be in the
    /// thread's queue when a profile changes, so "press a desktop nobody is
    /// holding" has to be reachable to prove the coordinator drops it.
    /// </remarks>
    public void SimulatePress(int desktopOrdinal) =>
        Pressed?.Invoke(
            this,
            new DesktopSwitchHotkeyInvokedEventArgs(desktopOrdinal));

    public HotkeyRegistrationOutcome Register(int desktopOrdinal, HotkeyChord chord)
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
                _held[desktopOrdinal] = chord;
            }

            _attempts.Add(
                new DesktopSwitchHotkeyRegistrationAttempt(
                    desktopOrdinal,
                    chord,
                    outcome.Succeeded));
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
