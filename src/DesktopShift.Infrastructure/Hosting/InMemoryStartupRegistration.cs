using DesktopShift.Core.Hosting;

namespace DesktopShift.Infrastructure.Hosting;

/// <summary>
/// A startup registration that only remembers what it was told.
/// </summary>
/// <remarks>
/// Registered as the default so no host — a test host in particular — can alter
/// the real login state of the machine it runs on. The packaged application
/// replaces it with the platform implementation.
/// </remarks>
public sealed class InMemoryStartupRegistration : IStartupRegistration
{
    private readonly object _syncRoot = new();
    private StartupRegistrationState _state;

    public InMemoryStartupRegistration()
        : this(StartupRegistrationState.Disabled)
    {
    }

    public InMemoryStartupRegistration(StartupRegistrationState initialState)
    {
        ThrowIfUndefined(initialState);
        _state = initialState;
    }

    public ValueTask<StartupRegistrationState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_state);
        }
    }

    public ValueTask<StartupRegistrationState> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            // A locked state models Windows faithfully: the application cannot
            // undo a user or policy decision, so the write is a no-op.
            if (!_state.IsLocked())
            {
                _state = isEnabled
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.Disabled;
            }

            return ValueTask.FromResult(_state);
        }
    }

    private static void ThrowIfUndefined(StartupRegistrationState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "The startup registration state is not defined.");
        }
    }
}
