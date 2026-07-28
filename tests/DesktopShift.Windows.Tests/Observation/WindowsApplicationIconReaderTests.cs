using DesktopShift.Core.Configuration;
using DesktopShift.Windows.Observation;

namespace DesktopShift.Windows.Tests.Observation;

/// <summary>
/// Reading an executable's icon for the rule editor. Every platform call is
/// faked, so what is under test is the reader's own behaviour: which paths it
/// refuses, which sizes it trusts, and whether it ever leaves a handle open.
/// </summary>
[TestClass]
public sealed class WindowsApplicationIconReaderTests
{
    private static readonly nint IconHandle = 11;
    private static readonly nint ColorBitmap = 12;
    private static readonly nint MaskBitmap = 13;

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("notepad.exe")]
    [DataRow(@"Windows\notepad.exe")]
    public void APathThatIsNotFullyQualified_IsRefusedWithoutAskingWindows(
        string? executablePath)
    {
        // A relative path would be resolved against whatever the process's
        // current directory happens to be, which is not a decision an icon read
        // gets to make.
        FakeIconNativeApi nativeApi = new();

        Assert.IsNull(new WindowsApplicationIconReader(nativeApi)
            .TryRead(executablePath));
        Assert.AreEqual(0, nativeApi.ExtractCount);
    }

    [TestMethod]
    public void AnExecutableWithoutAnIcon_ReadsAsNoIconRatherThanAnError()
    {
        FakeIconNativeApi nativeApi = new() { Icon = 0 };

        Assert.IsNull(new WindowsApplicationIconReader(nativeApi)
            .TryRead(@"C:\Windows\System32\where.exe"));
        Assert.AreEqual(1, nativeApi.ExtractCount);
        Assert.IsTrue(nativeApi.HasReleasedEverything);
    }

    [TestMethod]
    public void AReadableIcon_BecomesTopDownBgraPixels()
    {
        FakeIconNativeApi nativeApi = new()
        {
            Width = 2,
            Height = 2,
            Pixels =
            [
                1, 2, 3, 255, 4, 5, 6, 255,
                7, 8, 9, 255, 10, 11, 12, 255,
            ],
        };

        ApplicationIcon? icon = new WindowsApplicationIconReader(nativeApi)
            .TryRead(@"C:\Windows\System32\mstsc.exe");

        Assert.IsNotNull(icon);
        Assert.AreEqual(2, icon.Width);
        Assert.AreEqual(2, icon.Height);
        Assert.IsTrue(icon.IsComplete);
        CollectionAssert.AreEqual(nativeApi.Pixels, icon.Pixels.ToArray());
        Assert.IsTrue(nativeApi.HasReleasedEverything);
    }

    [TestMethod]
    public void AnIconWithNoAlphaChannel_IsMadeOpaqueRatherThanInvisible()
    {
        // A 24-bit icon copied into a 32-bit buffer leaves every alpha byte at
        // zero, which is indistinguishable from a fully transparent image and
        // would draw as nothing at all.
        FakeIconNativeApi nativeApi = new()
        {
            Width = 1,
            Height = 2,
            Pixels = [1, 2, 3, 0, 4, 5, 6, 0],
        };

        ApplicationIcon? icon = new WindowsApplicationIconReader(nativeApi)
            .TryRead(@"C:\Windows\System32\mstsc.exe");

        Assert.IsNotNull(icon);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 },
            icon.Pixels.ToArray());
    }

    [TestMethod]
    public void APartiallyTransparentIcon_KeepsItsAlphaChannel()
    {
        FakeIconNativeApi nativeApi = new()
        {
            Width = 1,
            Height = 2,
            Pixels = [1, 2, 3, 0, 4, 5, 6, 128],
        };

        ApplicationIcon? icon = new WindowsApplicationIconReader(nativeApi)
            .TryRead(@"C:\Windows\System32\mstsc.exe");

        Assert.IsNotNull(icon);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 },
            icon.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(0, 16)]
    [DataRow(16, 0)]
    [DataRow(4096, 16)]
    [DataRow(16, 4096)]
    public void AnImplausibleReportedSize_IsNotTrustedWithAnAllocation(
        int width,
        int height)
    {
        FakeIconNativeApi nativeApi = new()
        {
            Width = width,
            Height = height,
        };

        Assert.IsNull(new WindowsApplicationIconReader(nativeApi)
            .TryRead(@"C:\Windows\System32\mstsc.exe"));
        Assert.AreEqual(0, nativeApi.CopyCount);
        Assert.IsTrue(nativeApi.HasReleasedEverything);
    }

    [TestMethod]
    public void EveryFailureAfterTheIconIsOpened_StillReleasesEveryHandle()
    {
        foreach (FakeIconNativeApi nativeApi in new[]
        {
            new FakeIconNativeApi { BitmapsAvailable = false },
            new FakeIconNativeApi { ColorBitmapAvailable = false },
            new FakeIconNativeApi { SizeAvailable = false },
            new FakeIconNativeApi { PixelsAvailable = false },
        })
        {
            Assert.IsNull(new WindowsApplicationIconReader(nativeApi)
                .TryRead(@"C:\Windows\System32\mstsc.exe"));
            Assert.IsTrue(
                nativeApi.HasReleasedEverything,
                "The icon and both of its bitmaps have to be released on every path out.");
        }
    }

    private sealed class FakeIconNativeApi : IWindowsIconNativeApi
    {
        private readonly HashSet<nint> released = [];

        public nint Icon { get; init; } = IconHandle;

        public bool BitmapsAvailable { get; init; } = true;

        public bool ColorBitmapAvailable { get; init; } = true;

        public bool SizeAvailable { get; init; } = true;

        public bool PixelsAvailable { get; init; } = true;

        public int Width { get; init; } = 1;

        public int Height { get; init; } = 1;

        public byte[] Pixels { get; init; } = [0, 0, 0, 255];

        public int ExtractCount { get; private set; }

        public int CopyCount { get; private set; }

        public bool HasReleasedEverything =>
            ExtractCount == 0 ||
            Icon == 0 ||
            (released.Contains(Icon) &&
                (!BitmapsAvailable ||
                    (released.Contains(MaskBitmap) &&
                        (!ColorBitmapAvailable || released.Contains(ColorBitmap)))));

        public nint TryExtractIcon(string executablePath)
        {
            ExtractCount++;
            return Icon;
        }

        public bool TryGetIconBitmaps(
            nint icon,
            out nint colorBitmap,
            out nint maskBitmap)
        {
            colorBitmap = ColorBitmapAvailable ? ColorBitmap : 0;
            maskBitmap = MaskBitmap;
            return BitmapsAvailable;
        }

        public bool TryGetBitmapSize(nint bitmap, out int width, out int height)
        {
            width = Width;
            height = Height;
            return SizeAvailable;
        }

        public bool TryCopyPixels(
            nint bitmap,
            int width,
            int height,
            byte[] destination)
        {
            CopyCount++;
            if (!PixelsAvailable)
            {
                return false;
            }

            Pixels.CopyTo(destination, 0);
            return true;
        }

        public void DeleteBitmap(nint bitmap)
        {
            if (bitmap != 0)
            {
                _ = released.Add(bitmap);
            }
        }

        public void DestroyIcon(nint icon)
        {
            if (icon != 0)
            {
                _ = released.Add(icon);
            }
        }
    }
}
