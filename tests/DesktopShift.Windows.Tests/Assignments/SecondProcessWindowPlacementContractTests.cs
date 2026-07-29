using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.VirtualDesktops;

namespace DesktopShift.Windows.Tests.Assignments;

[TestClass]
[DoNotParallelize]
public sealed class SecondProcessWindowPlacementContractTests
{
    private static readonly TimeSpan QueryRetryDelay =
        TimeSpan.FromMilliseconds(20);
    private const int QueryAttempts = 100;

    [TestMethod]
    public async Task CurrentMachine_ChildOwnedWindowMovesToAnotherDesktopAndBack()
    {
        RequireOptIn();
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive(
                "The isolated placement contract requires an interactive Windows session.");
        }

        using ValidatedVirtualDesktopTopologyProvider topology = new();
        DesktopTopologyProviderResult compatibility =
            await topology.TestCompatibilityAsync(
                new EnvironmentWindowsBuildInfoProvider().GetCurrent());
        Assert.IsTrue(compatibility.IsSuccess, compatibility.Error?.Message);
        Assert.AreEqual(
            DesktopTopologyProviderMode.Full,
            topology.Identity.Mode,
            topology.LastFallback?.Error.Message);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> inventory =
            await topology.EnumerateDesktopsAsync();
        Assert.IsTrue(inventory.IsSuccess, inventory.Error?.Message);
        if (inventory.Value!.Count < 2)
        {
            Assert.Inconclusive(
                "The isolated placement contract requires two existing virtual desktops. " +
                "It never creates one because the shipping provider has no reversible " +
                "desktop-removal contract.");
        }

        await using SecondProcessTestWindow helper =
            await SecondProcessTestWindow.StartAsync(CancellationToken.None);
        using WindowsWindowDesktopPlacementService placement = new(topology);
        Guid? initialDesktopId = null;
        DesktopTopologyProviderResult? restoreResult = null;
        DesktopTopologyProviderResult<Guid>? afterRestore = null;

        try
        {
            DesktopTopologyProviderResult<Guid> initial =
                await WaitForSuccessfulQueryAsync(placement, helper.Handle);
            Assert.IsTrue(initial.IsSuccess, FormatError(initial.Error));
            initialDesktopId = initial.Value;
            Assert.AreNotEqual(Guid.Empty, initialDesktopId.Value);

            Guid targetDesktopId = inventory.Value
                .First(desktop => desktop.Id != initialDesktopId.Value)
                .Id;
            DesktopTopologyProviderResult move =
                await placement.MoveWindowToDesktopAsync(
                    helper.Handle,
                    targetDesktopId);
            Assert.IsTrue(move.IsSuccess, FormatError(move.Error));

            DesktopTopologyProviderResult<Guid> afterMove =
                await WaitForDesktopAsync(
                    placement,
                    helper.Handle,
                    targetDesktopId);
            Assert.IsTrue(afterMove.IsSuccess, FormatError(afterMove.Error));
            Assert.AreEqual(targetDesktopId, afterMove.Value);
            Assert.AreNotEqual(initialDesktopId.Value, afterMove.Value);
            Assert.IsTrue(
                helper.IsAliveAndOwned,
                "The moved HWND stopped belonging to the isolated child process.");
        }
        finally
        {
            if (initialDesktopId is Guid originalDesktopId &&
                helper.IsAliveAndOwned)
            {
                restoreResult = await placement.MoveWindowToDesktopAsync(
                    helper.Handle,
                    originalDesktopId);
                if (restoreResult.IsSuccess)
                {
                    afterRestore = await WaitForDesktopAsync(
                        placement,
                        helper.Handle,
                        originalDesktopId);
                }
            }
        }

        Assert.IsNotNull(restoreResult);
        Assert.IsTrue(restoreResult.IsSuccess, FormatError(restoreResult.Error));
        Assert.IsNotNull(afterRestore);
        Assert.IsTrue(afterRestore.IsSuccess, FormatError(afterRestore.Error));
        Assert.AreEqual(initialDesktopId, afterRestore.Value);
    }

    private static async Task<DesktopTopologyProviderResult<Guid>> WaitForSuccessfulQueryAsync(
        WindowsWindowDesktopPlacementService placement,
        nint windowHandle)
    {
        DesktopTopologyProviderResult<Guid> result =
            await placement.GetWindowDesktopIdAsync(windowHandle);
        for (int attempt = 1;
             attempt < QueryAttempts && !result.IsSuccess;
             attempt++)
        {
            await Task.Delay(QueryRetryDelay);
            result = await placement.GetWindowDesktopIdAsync(windowHandle);
        }

        return result;
    }

    private static async Task<DesktopTopologyProviderResult<Guid>> WaitForDesktopAsync(
        WindowsWindowDesktopPlacementService placement,
        nint windowHandle,
        Guid expectedDesktopId)
    {
        DesktopTopologyProviderResult<Guid> result =
            await placement.GetWindowDesktopIdAsync(windowHandle);
        for (int attempt = 1;
             attempt < QueryAttempts &&
             (!result.IsSuccess || result.Value != expectedDesktopId);
             attempt++)
        {
            await Task.Delay(QueryRetryDelay);
            result = await placement.GetWindowDesktopIdAsync(windowHandle);
        }

        return result;
    }

    private static void RequireOptIn()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "DESKTOPSHIFT_RUN_NATIVE_CONTRACT_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set DESKTOPSHIFT_RUN_NATIVE_CONTRACT_TESTS=1 on a designated " +
                "interactive Windows test machine.");
        }
    }

    private static string FormatError(DesktopTopologyProviderError? error) =>
        error is null
            ? "Windows returned no placement error details."
            : $"{error.Code} 0x{error.HResult:X8}: {error.Message}";
}
