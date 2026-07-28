using DesktopShift.Core.Appearance;
using DesktopShift.Core.Hosting;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

[TestClass]
public sealed class DesktopShiftHostTests
{
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
