using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// Keeps the ten desktop-switching combinations Windows is holding equal to the
/// profile the configuration asks for.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here calls Windows. Every claim goes through
/// <see cref="IDesktopSwitchHotkeyRegistrar"/>, which is what lets the ordering
/// rules — release first, validate, register only what survived — be proved
/// without a single combination being taken from the machine a test runs on.
/// </para>
/// <para>
/// Changing profile moves all ten chords at once. Releasing everything first
/// prevents a partial change from leaving Ctrl+Alt+1 claimed by a process that
/// now believes it owns a different profile, with no way back but a restart.
/// </para>
/// <para>
/// A refused desktop does not stop the others. A user whose taskbar already owns
/// Win+Alt+3 should still get the nine that Windows was willing to hand over,
/// with the one failure reported rather than the whole profile abandoned.
/// </para>
/// </remarks>
public sealed class DesktopSwitchHotkeyCoordinator : IDesktopSwitchHotkeyCoordinator
{
    private readonly IDesktopSwitchHotkeyRegistrar _registrar;
    private readonly TimeProvider _timeProvider;
    private readonly object _syncRoot = new();
    private DesktopSwitchHotkeyState _current;
    private bool _isDisposed;

    public DesktopSwitchHotkeyCoordinator(
        IDesktopSwitchHotkeyRegistrar registrar,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _registrar = registrar;
        _timeProvider = timeProvider;
        _current = new DesktopSwitchHotkeyState(
            false,
            DesktopSwitchShortcutProfile.CtrlAlt,
            [],
            [],
            timeProvider.GetUtcNow());
        _registrar.Pressed += OnRegistrarPressed;
    }

    public DesktopSwitchHotkeyState Current
    {
        get
        {
            lock (_syncRoot)
            {
                return _current;
            }
        }
    }

    public event EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? Invoked;

    public DesktopSwitchHotkeyState Apply(DesktopSwitchShortcutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // Unconditional, and before anything is inspected: whatever this
            // call decides, no combination claimed by a previous profile may
            // survive it.
            _current = new DesktopSwitchHotkeyState(
                false,
                settings.Profile,
                [],
                [],
                _timeProvider.GetUtcNow());
            _registrar.UnregisterAll();

            ImmutableArray<ConfigurationValidationIssue> issues =
                DesktopSwitchShortcuts.Validate(settings);
            DesktopSwitchHotkeyState state =
                DesktopSwitchShortcuts.IsRegistrable(settings)
                    ? RegisterProfile(settings, issues)
                    : new DesktopSwitchHotkeyState(
                        false,
                        settings.Profile,
                        [],
                        issues,
                        _timeProvider.GetUtcNow());

            _current = state;
            return state;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _current = _current with
            {
                IsEnabled = false,
                Registrations = [],
            };
        }

        _registrar.Pressed -= OnRegistrarPressed;
        _registrar.UnregisterAll();
        _registrar.Dispose();
        Invoked = null;
    }

    /// <summary>
    /// Claims all ten combinations, turning each refusal into an issue the
    /// Settings page can show against the profile that produced it.
    /// </summary>
    private DesktopSwitchHotkeyState RegisterProfile(
        DesktopSwitchShortcutSettings settings,
        ImmutableArray<ConfigurationValidationIssue> validationIssues)
    {
        ImmutableArray<DesktopSwitchHotkeyBinding> bindings = settings.Bindings();
        ImmutableArray<DesktopSwitchHotkeyRegistration>.Builder registrations =
            ImmutableArray.CreateBuilder<DesktopSwitchHotkeyRegistration>(
                bindings.Length);
        ImmutableArray<ConfigurationValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();
        issues.AddRange(validationIssues);
        List<int> refused = [];

        foreach (DesktopSwitchHotkeyBinding binding in bindings)
        {
            HotkeyRegistrationOutcome outcome;
            try
            {
                outcome = _registrar.Register(
                    binding.DesktopOrdinal,
                    binding.Chord);
            }
            catch (Exception exception)
            {
                outcome = new HotkeyRegistrationOutcome(
                    false,
                    $"Windows shortcut registration failed unexpectedly: {exception.Message}",
                    exception.HResult);
            }

            if (outcome.Succeeded)
            {
                registrations.Add(
                    new DesktopSwitchHotkeyRegistration(
                        binding.DesktopOrdinal,
                        binding.Chord,
                        true));
                continue;
            }

            string failure = DescribeFailure(binding, outcome);
            registrations.Add(
                new DesktopSwitchHotkeyRegistration(
                    binding.DesktopOrdinal,
                    binding.Chord,
                    false,
                    failure));
            refused.Add(binding.DesktopOrdinal);
        }

        // One issue for the whole profile rather than one per refused digit. Ten
        // near-identical rows saying the taskbar owns Win+Alt would bury the
        // single thing the user has to decide, which is whether to pick another
        // profile.
        if (refused.Count > 0)
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.DesktopSwitchShortcutRegistrationFailed,
                DescribeRefusals(settings, refused, registrations),
                PathFor(settings.Profile),
                ConfigurationEntryKind.Hotkey,
                DesktopSwitchShortcuts.DesktopSwitchShortcutEntryId));
        }

        return new DesktopSwitchHotkeyState(
            true,
            settings.Profile,
            registrations.ToImmutable(),
            issues.ToImmutable(),
            _timeProvider.GetUtcNow());
    }

    private static string PathFor(DesktopSwitchShortcutProfile profile) =>
        profile == DesktopSwitchShortcutProfile.Custom
            ? $"{DesktopSwitchShortcuts.PathPrefix}.customModifiers"
            : $"{DesktopSwitchShortcuts.PathPrefix}.profile";

    private static string DescribeFailure(
        DesktopSwitchHotkeyBinding binding,
        HotkeyRegistrationOutcome outcome)
    {
        string reason = string.IsNullOrWhiteSpace(outcome.FailureMessage)
            ? "Another application may already be using it."
            : outcome.FailureMessage!;
        string message =
            $"Shortcut '{binding.Chord.Describe()}' for Desktop {binding.DesktopOrdinal} could not be registered. {reason}";
        return outcome.NativeErrorCode is int nativeErrorCode
            ? $"{message} (Windows error {nativeErrorCode}.)"
            : message;
    }

    private static string DescribeRefusals(
        DesktopSwitchShortcutSettings settings,
        List<int> refused,
        ImmutableArray<DesktopSwitchHotkeyRegistration>.Builder registrations)
    {
        string desktops = refused.Count == 1
            ? $"Desktop {refused[0]}"
            : $"Desktops {string.Join(", ", refused[..^1])} and {refused[^1]}";
        int registered = registrations.Count - refused.Count;
        string summary =
            $"Windows refused the {DesktopSwitchShortcuts.Describe(settings.Profile)} shortcut for {desktops}. {registered} of {registrations.Count} shortcuts are active.";

        return
            $"{summary} Something else on this machine already holds the refused combinations; choose a different one.";
    }

    /// <summary>
    /// Re-raises a press, but only for a desktop this coordinator believes is
    /// registered right now.
    /// </summary>
    /// <remarks>
    /// A hotkey message can already be sitting in the thread's queue when the
    /// user switches profile, and a message that arrives after its combination
    /// was released must not move the user to a desktop they no longer have a
    /// shortcut for.
    /// </remarks>
    private void OnRegistrarPressed(
        object? sender,
        DesktopSwitchHotkeyInvokedEventArgs args)
    {
        EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? handler;

        lock (_syncRoot)
        {
            if (_isDisposed ||
                !_current.Registrations.Any(registration =>
                    registration.IsRegistered &&
                    registration.DesktopOrdinal == args.DesktopOrdinal))
            {
                return;
            }

            handler = Invoked;
        }

        handler?.Invoke(this, args);
    }
}
