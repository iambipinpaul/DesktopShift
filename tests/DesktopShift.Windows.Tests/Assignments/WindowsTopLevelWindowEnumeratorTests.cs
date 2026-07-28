using System.ComponentModel;
using DesktopShift.Windows.Assignments;

namespace DesktopShift.Windows.Tests.Assignments;

[TestClass]
public sealed class WindowsTopLevelWindowEnumeratorTests
{
    [TestMethod]
    public void Enumerate_PerformsOnePassAndPreservesNativeOrder()
    {
        nint[] expected = [(nint)0x1001, (nint)0x1002, (nint)0x1003];
        FakeEnumWindowsApi nativeApi = new(expected);
        WindowsTopLevelWindowEnumerator enumerator = new(nativeApi);

        IReadOnlyList<nint> result = enumerator.Enumerate();

        CollectionAssert.AreEqual(expected, result.ToArray());
        Assert.AreEqual(1, nativeApi.EnumerationCount);
    }

    [TestMethod]
    public void Enumerate_ThrowsStructuredWin32Failure()
    {
        const int AccessDenied = 5;
        FakeEnumWindowsApi nativeApi = new([])
        {
            Result = new EnumWindowsResult(false, AccessDenied),
        };
        WindowsTopLevelWindowEnumerator enumerator = new(nativeApi);

        Win32Exception exception =
            Assert.ThrowsExactly<Win32Exception>(enumerator.Enumerate);

        Assert.AreEqual(AccessDenied, exception.NativeErrorCode);
        Assert.AreEqual(1, nativeApi.EnumerationCount);
    }

    private sealed class FakeEnumWindowsApi(IReadOnlyList<nint> windows)
        : IEnumWindowsApi
    {
        public EnumWindowsResult Result { get; init; } = new(true);

        public int EnumerationCount { get; private set; }

        public EnumWindowsResult Enumerate(Action<nint> onWindow)
        {
            EnumerationCount++;
            foreach (nint window in windows)
            {
                onWindow(window);
            }

            return Result;
        }
    }
}
