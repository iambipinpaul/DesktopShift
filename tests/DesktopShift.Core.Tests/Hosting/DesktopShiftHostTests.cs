using DesktopShift.Core.Appearance;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

[TestClass]
public sealed class DesktopShiftHostTests
{
    [TestMethod]
    public void Observation_HoldsOpenedWindowsLongEnoughForAnActivation()
    {
        // Most host tests turn the wait off so their own subject stays in view,
        // which would let the shipped default drift to zero unnoticed. A window
        // assigned the instant it opens can never be followed, so this asserts
        // that the composition the application uses does wait.
        ServiceCollection services = new();
        services.AddDesktopShiftObservation();
        using ServiceProvider provider = services.BuildServiceProvider();

        OpenWindowFollowGrace grace =
            provider.GetRequiredService<OpenWindowFollowGrace>();

        Assert.IsTrue(grace.IsEnabled);
    }

    [TestMethod]
    public async Task Host_StartAndStop_UpdatesRuntimeState()
    {
        using IHost host = DesktopShiftHost.Create();
        IApplicationRuntimeState runtimeState =
            host.Services.GetRequiredService<IApplicationRuntimeState>();

        Assert.IsFalse(runtimeState.IsRunning);
        Assert.IsNull(runtimeState.StartedAtUtc);

        await host.StartAsync();

        Assert.IsTrue(runtimeState.IsRunning);
        Assert.IsNotNull(runtimeState.StartedAtUtc);
        Assert.IsInstanceOfType<IThemePreferenceService>(
            host.Services.GetRequiredService<IThemePreferenceService>());

        await host.StopAsync();

        Assert.IsFalse(runtimeState.IsRunning);
        Assert.IsNull(runtimeState.StartedAtUtc);
    }
}
