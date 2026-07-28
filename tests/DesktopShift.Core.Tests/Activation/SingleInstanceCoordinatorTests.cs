using DesktopShift.Core.Activation;

namespace DesktopShift.Core.Tests.Activation;

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public async Task RouteStartupActivationAsync_ForPrimaryInstance_RaisesLocalActivation()
    {
        FakeSingleInstanceService service = new(isCurrent: true);
        await using SingleInstanceCoordinator coordinator = new(service);
        int activationCount = 0;
        coordinator.ActivationRequested += (_, _) => activationCount++;

        ActivationRoute route = await coordinator.RouteStartupActivationAsync();

        Assert.AreEqual(ActivationRoute.PrimaryInstance, route);
        Assert.AreEqual(1, activationCount);
        Assert.AreEqual(0, service.RedirectCount);
    }

    [TestMethod]
    public async Task RouteStartupActivationAsync_ForSecondaryInstance_RedirectsToPrimary()
    {
        FakeSingleInstanceService service = new(isCurrent: false);
        await using SingleInstanceCoordinator coordinator = new(service);
        int activationCount = 0;
        coordinator.ActivationRequested += (_, _) => activationCount++;

        ActivationRoute route = await coordinator.RouteStartupActivationAsync();

        Assert.AreEqual(ActivationRoute.RedirectedToPrimaryInstance, route);
        Assert.AreEqual(0, activationCount);
        Assert.AreEqual(1, service.RedirectCount);
    }

    [TestMethod]
    public async Task PlatformActivation_ForPrimaryInstance_IsRelayed()
    {
        FakeSingleInstanceService service = new(isCurrent: true);
        await using SingleInstanceCoordinator coordinator = new(service);
        int activationCount = 0;
        coordinator.ActivationRequested += (_, _) => activationCount++;

        service.RaiseActivationRequested();

        Assert.AreEqual(1, activationCount);
    }

    private sealed class FakeSingleInstanceService(bool isCurrent) : ISingleInstanceService
    {
        public bool IsCurrent { get; } = isCurrent;

        public int RedirectCount { get; private set; }

        public event EventHandler? ActivationRequested;

        public Task RedirectActivationToCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedirectCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void RaiseActivationRequested()
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
