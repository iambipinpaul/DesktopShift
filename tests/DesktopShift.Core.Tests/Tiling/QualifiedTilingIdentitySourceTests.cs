using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class QualifiedTilingIdentitySourceTests
{
    [TestMethod]
    public async Task DesktopShiftWindowGetsSafeIdentityForTiling()
    {
        FakeClassifier classifier = new(
            WindowQualification.Skipped(WindowSkipReason.DesktopShiftWindow));
        FakeIdentityResolver resolver = new();
        QualifiedTilingIdentitySource source = new(classifier, resolver);

        WindowIdentity? identity = await source.ResolveAsync((nint)0x1234);

        Assert.IsNotNull(identity);
        Assert.AreEqual(ProductInfo.ApplicationName, identity.ProcessName);
        Assert.AreEqual(unchecked((uint)Environment.ProcessId), identity.ProcessId);
        Assert.AreEqual(0, resolver.CallCount);
    }

    [TestMethod]
    public async Task OtherSkippedWindowDoesNotGetIdentity()
    {
        FakeClassifier classifier = new(
            WindowQualification.Skipped(WindowSkipReason.ShellWindow));
        FakeIdentityResolver resolver = new();
        QualifiedTilingIdentitySource source = new(classifier, resolver);

        WindowIdentity? identity = await source.ResolveAsync((nint)0x1234);

        Assert.IsNull(identity);
        Assert.AreEqual(0, resolver.CallCount);
    }

    private sealed class FakeClassifier(WindowQualification qualification)
        : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) => qualification;

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) => WindowSkipReason.None;
    }

    private sealed class FakeIdentityResolver : IWindowIdentityResolver
    {
        public int CallCount { get; private set; }

        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                WindowIdentityResolution.Failed(
                    WindowIdentityResolutionFailure.NativeFailure));
        }
    }
}
