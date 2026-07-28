using DesktopShift.Core.Hosting;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

[TestClass]
public sealed class InMemoryStartupRegistrationTests
{
    [TestMethod]
    public async Task SetEnabledAsync_RoundTripsTheRequestedState()
    {
        InMemoryStartupRegistration registration = new();

        Assert.AreEqual(
            StartupRegistrationState.Disabled,
            await registration.GetStateAsync());

        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            await registration.SetEnabledAsync(true));
        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            await registration.GetStateAsync());

        Assert.AreEqual(
            StartupRegistrationState.Disabled,
            await registration.SetEnabledAsync(false));
    }

    [TestMethod]
    public async Task SetEnabledAsync_CannotUndoAUserOrPolicyDecision()
    {
        InMemoryStartupRegistration disabledByUser =
            new(StartupRegistrationState.DisabledByUser);
        InMemoryStartupRegistration disabledByPolicy =
            new(StartupRegistrationState.DisabledByPolicy);

        Assert.AreEqual(
            StartupRegistrationState.DisabledByUser,
            await disabledByUser.SetEnabledAsync(true));
        Assert.AreEqual(
            StartupRegistrationState.DisabledByPolicy,
            await disabledByPolicy.SetEnabledAsync(true));
        Assert.IsFalse(StartupRegistrationState.DisabledByUser.IsEnabled());
        Assert.IsTrue(StartupRegistrationState.DisabledByUser.IsLocked());
    }

    [TestMethod]
    public void Host_DefaultsToTheInMemoryRegistration()
    {
        using IHost host = DesktopShiftHost.Create();

        // No test host may reach the machine's real login state.
        Assert.IsInstanceOfType<InMemoryStartupRegistration>(
            host.Services.GetRequiredService<IStartupRegistration>());
    }
}
