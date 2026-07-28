namespace DesktopShift.Core.Hosting;

/// <summary>
/// The states a per-user startup registration can report.
/// </summary>
/// <remarks>
/// <see cref="DisabledByUser"/> and <see cref="DisabledByPolicy"/> are distinct
/// from <see cref="Disabled"/> because neither can be reversed by the
/// application: Windows requires the user to re-enable a startup task they
/// turned off in Task Manager or Settings, and policy is never negotiable. The
/// shell has to explain that instead of silently failing to honour a toggle.
/// </remarks>
public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    DisabledByUser,
    DisabledByPolicy,
    Unavailable,
}

/// <summary>
/// Reads and writes the per-user "start with Windows" registration.
/// </summary>
/// <remarks>
/// Kept behind an interface so the shell can be driven in tests without
/// touching the real login state of the machine running them.
/// </remarks>
public interface IStartupRegistration
{
    ValueTask<StartupRegistrationState> GetStateAsync(
        CancellationToken cancellationToken = default);

    ValueTask<StartupRegistrationState> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);
}

public static class StartupRegistrationStates
{
    public static bool IsEnabled(this StartupRegistrationState state) =>
        state == StartupRegistrationState.Enabled;

    /// <summary>
    /// Whether the application is unable to change the registration itself.
    /// </summary>
    public static bool IsLocked(this StartupRegistrationState state) =>
        state is StartupRegistrationState.DisabledByUser
            or StartupRegistrationState.DisabledByPolicy
            or StartupRegistrationState.Unavailable;

    public static string Describe(this StartupRegistrationState state) =>
        state switch
        {
            StartupRegistrationState.Enabled =>
                "DesktopShift starts with Windows.",
            StartupRegistrationState.Disabled =>
                "DesktopShift does not start with Windows.",
            StartupRegistrationState.DisabledByUser =>
                "Startup was turned off outside DesktopShift. Re-enable it from the Startup apps page in Windows Settings.",
            StartupRegistrationState.DisabledByPolicy =>
                "Startup is blocked by policy on this device.",
            _ =>
                "Startup registration is unavailable. It requires the packaged DesktopShift application.",
        };
}
