using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// Keeps the combinations Windows is actually holding equal to the ones the
/// configuration asks for.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here calls Windows. Every claim goes through
/// <see cref="IGlobalHotkeyRegistrar"/>, which is what lets the ordering rules
/// below — release first, validate, register only what survived — be proved
/// without a single combination ever being taken from the machine a test runs
/// on.
/// </para>
/// <para>
/// The release-everything-first order is deliberate. An edit that moves a chord
/// from one action to another would otherwise leave the old combination claimed
/// by a process that no longer believes it owns it, and the only way back would
/// be to restart DesktopShift.
/// </para>
/// </remarks>
public sealed class GlobalHotkeyCoordinator : IGlobalHotkeyCoordinator
{
    private readonly IGlobalHotkeyRegistrar _registrar;
    private readonly TimeProvider _timeProvider;
    private readonly object _syncRoot = new();
    private HotkeyState _current;
    private bool _isDisposed;

    public GlobalHotkeyCoordinator(
        IGlobalHotkeyRegistrar registrar,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _registrar = registrar;
        _timeProvider = timeProvider;
        _current = new HotkeyState(false, [], [], timeProvider.GetUtcNow());
        _registrar.Pressed += OnRegistrarPressed;
    }

    public HotkeyState Current
    {
        get
        {
            lock (_syncRoot)
            {
                return _current;
            }
        }
    }

    public event EventHandler<HotkeyInvokedEventArgs>? Invoked;

    public HotkeyState Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // Unconditional, and before anything is inspected: whatever this
            // call decides, no combination claimed by a previous call may
            // survive it.
            _current = new HotkeyState(
                false,
                [],
                [],
                _timeProvider.GetUtcNow());
            _registrar.UnregisterAll();

            ImmutableArray<ConfigurationValidationIssue> issues =
                HotkeyValidation.Validate(settings.Bindings);
            HotkeyState state = settings.IsEnabled
                ? RegisterEnabled(settings, issues)
                : new HotkeyState(
                    false,
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
    /// Claims every binding validation did not already reject, turning each
    /// refusal into an issue the Settings page can show beside the row it
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// Only the registrable set is attempted. Handing a chord that already lost
    /// a conflict to the registrar would claim the same combination twice and
    /// report a collision DesktopShift caused itself.
    /// </remarks>
    private HotkeyState RegisterEnabled(
        HotkeySettings settings,
        ImmutableArray<ConfigurationValidationIssue> validationIssues)
    {
        ImmutableArray<HotkeyBinding> registrable =
            HotkeyValidation.Registrable(settings.Bindings);
        ImmutableArray<HotkeyRegistration>.Builder registrations =
            ImmutableArray.CreateBuilder<HotkeyRegistration>(registrable.Length);
        ImmutableArray<ConfigurationValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();
        issues.AddRange(validationIssues);

        foreach (HotkeyBinding binding in registrable)
        {
            HotkeyRegistrationOutcome outcome;
            try
            {
                outcome = _registrar.Register(binding.Action, binding.Chord);
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
                    new HotkeyRegistration(binding.Action, binding.Chord, true));
                continue;
            }

            string failure = DescribeFailure(binding.Chord, outcome);
            registrations.Add(
                new HotkeyRegistration(
                    binding.Action,
                    binding.Chord,
                    false,
                    failure));
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.HotkeyRegistrationFailed,
                failure,
                $"{HotkeyValidation.PathPrefix}[{IndexOf(settings, binding)}]",
                ConfigurationEntryKind.Hotkey,
                binding.Action.ToString()));
        }

        return new HotkeyState(
            true,
            registrations.ToImmutable(),
            issues.ToImmutable(),
            _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The binding's position in the document, so an issue points at the row a
    /// user would edit rather than at its position in the filtered set.
    /// </summary>
    private static int IndexOf(HotkeySettings settings, HotkeyBinding binding)
    {
        for (int index = 0; index < settings.Bindings.Length; index++)
        {
            if (ReferenceEquals(settings.Bindings[index], binding))
            {
                return index;
            }
        }

        return 0;
    }

    private static string DescribeFailure(
        HotkeyChord chord,
        HotkeyRegistrationOutcome outcome)
    {
        string reason = string.IsNullOrWhiteSpace(outcome.FailureMessage)
            ? "Another application may already be using it."
            : outcome.FailureMessage!;
        string message =
            $"Shortcut '{chord.Describe()}' could not be registered. {reason}";
        return outcome.NativeErrorCode is int nativeErrorCode
            ? $"{message} (Windows error {nativeErrorCode}.)"
            : message;
    }

    /// <summary>
    /// Re-raises a press, but only for an action this coordinator believes is
    /// registered right now.
    /// </summary>
    /// <remarks>
    /// A hotkey message can already be in the thread's queue when the user turns
    /// the shortcut off, and a message that arrives after the combination was
    /// released must not run a command the user just withdrew.
    /// </remarks>
    private void OnRegistrarPressed(object? sender, HotkeyInvokedEventArgs args)
    {
        EventHandler<HotkeyInvokedEventArgs>? handler;

        lock (_syncRoot)
        {
            if (_isDisposed ||
                !_current.Registrations.Any(registration =>
                    registration.IsRegistered &&
                    registration.Action == args.Action))
            {
                return;
            }

            handler = Invoked;
        }

        handler?.Invoke(this, args);
    }
}
