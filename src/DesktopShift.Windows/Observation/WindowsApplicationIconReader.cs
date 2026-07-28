using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Windows.Observation;

/// <summary>
/// The platform calls needed to turn an executable into pixels.
/// </summary>
/// <remarks>
/// Every call is behind this interface so the reader's own behavior — the path
/// tests, the size bounds, the alpha repair, and above all releasing the icon and
/// both bitmaps on every path out — can be tested without a shell, an installed
/// application, or a desktop.
/// </remarks>
public interface IWindowsIconNativeApi
{
    /// <summary>
    /// Extracts an executable's first icon.
    /// </summary>
    /// <param name="executablePath">The full path of the executable.</param>
    /// <returns>An icon handle, or zero when the file carries no icon.</returns>
    nint TryExtractIcon(string executablePath);

    /// <summary>
    /// Reads the colour and mask bitmaps an icon is made of.
    /// </summary>
    /// <param name="icon">The icon handle.</param>
    /// <param name="colorBitmap">The colour bitmap, or zero.</param>
    /// <param name="maskBitmap">The mask bitmap, or zero.</param>
    /// <returns>Whether the bitmaps were read.</returns>
    bool TryGetIconBitmaps(nint icon, out nint colorBitmap, out nint maskBitmap);

    /// <summary>
    /// Reads a bitmap's pixel dimensions.
    /// </summary>
    /// <param name="bitmap">The bitmap handle.</param>
    /// <param name="width">The bitmap width.</param>
    /// <param name="height">The bitmap height.</param>
    /// <returns>Whether the dimensions were read.</returns>
    bool TryGetBitmapSize(nint bitmap, out int width, out int height);

    /// <summary>
    /// Copies a bitmap into a top-down 32-bit BGRA buffer.
    /// </summary>
    /// <param name="bitmap">The bitmap handle.</param>
    /// <param name="width">The bitmap width.</param>
    /// <param name="height">The bitmap height.</param>
    /// <param name="destination">
    /// A buffer of exactly <c>width * height * 4</c> bytes.
    /// </param>
    /// <returns>Whether the pixels were copied.</returns>
    bool TryCopyPixels(nint bitmap, int width, int height, byte[] destination);

    /// <summary>
    /// Releases a bitmap handle.
    /// </summary>
    /// <param name="bitmap">The bitmap handle, which may be zero.</param>
    void DeleteBitmap(nint bitmap);

    /// <summary>
    /// Releases an icon handle.
    /// </summary>
    /// <param name="icon">The icon handle, which may be zero.</param>
    void DestroyIcon(nint icon);
}

/// <summary>
/// Reads the icon an executable carries, for the rule editor and the rule list.
/// </summary>
/// <remarks>
/// An icon is decoration, never a decision: a rule with no readable icon is a
/// perfectly good rule, so every failure here returns nothing rather than
/// throwing. The reader still owns the handles it opens, and releases the icon
/// and both of its bitmaps whichever way it leaves.
/// </remarks>
public sealed class WindowsApplicationIconReader : IApplicationIconReader
{
    /// <summary>
    /// The largest icon accepted. Windows shell icons top out well below this;
    /// a larger reported size means the bitmap header was not what it claimed,
    /// and allocating on that number would be trusting it.
    /// </summary>
    public const int MaximumIconExtent = 512;

    private readonly IWindowsIconNativeApi nativeApi;

    public WindowsApplicationIconReader(IWindowsIconNativeApi? nativeApi = null)
    {
        this.nativeApi = nativeApi ?? new WindowsIconNativeApi();
    }

    public ApplicationIcon? TryRead(string? executablePath)
    {
        // A relative path would be resolved against whatever the process's
        // current directory happens to be, so it is refused rather than guessed.
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            return null;
        }

        nint icon = nativeApi.TryExtractIcon(executablePath);
        if (icon == 0)
        {
            return null;
        }

        nint colorBitmap = 0;
        nint maskBitmap = 0;
        try
        {
            if (!nativeApi.TryGetIconBitmaps(icon, out colorBitmap, out maskBitmap) ||
                colorBitmap == 0)
            {
                return null;
            }

            if (!nativeApi.TryGetBitmapSize(colorBitmap, out int width, out int height) ||
                width <= 0 ||
                height <= 0 ||
                width > MaximumIconExtent ||
                height > MaximumIconExtent)
            {
                return null;
            }

            byte[] pixels = new byte[width * height * 4];
            if (!nativeApi.TryCopyPixels(colorBitmap, width, height, pixels))
            {
                return null;
            }

            RepairOpaqueAlpha(pixels);
            return new ApplicationIcon(width, height, [.. pixels]);
        }
        finally
        {
            nativeApi.DeleteBitmap(colorBitmap);
            nativeApi.DeleteBitmap(maskBitmap);
            nativeApi.DestroyIcon(icon);
        }
    }

    /// <summary>
    /// Makes an icon without an alpha channel visible.
    /// </summary>
    /// <remarks>
    /// A 24-bit icon copied into a 32-bit buffer leaves every alpha byte at
    /// zero, which is indistinguishable from a fully transparent image and would
    /// render as nothing at all. An icon whose every pixel is transparent is not
    /// an icon anyone chose to ship, so it is read as "no alpha channel" and
    /// made opaque.
    /// </remarks>
    /// <param name="pixels">The copied BGRA pixels.</param>
    private static void RepairOpaqueAlpha(byte[] pixels)
    {
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                return;
            }
        }

        for (int index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = byte.MaxValue;
        }
    }
}

/// <summary>
/// The documented Windows calls that turn an executable into pixels.
/// </summary>
/// <remarks>
/// <c>ExtractIconEx</c> reads the icon resource out of the file itself rather
/// than asking the shell what icon it would draw. That keeps the read cheap and
/// predictable — no shell extension runs, nothing is looked up on a network
/// path — at the cost of returning nothing for a file with no embedded icon,
/// which the caller already treats as an ordinary outcome.
/// </remarks>
public sealed class WindowsIconNativeApi : IWindowsIconNativeApi
{
    private const int BitmapInfoHeaderSize = 40;
    private const int BitCount = 32;
    private const uint UncompressedRgb = 0;
    private const uint RgbColors = 0;

    public nint TryExtractIcon(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        int extracted = NativeMethods.ExtractIconEx(
            executablePath,
            iconIndex: 0,
            out nint largeIcon,
            out nint smallIcon,
            iconCount: 1);

        // The large icon is preferred and the small one released, so the unused
        // half of the pair never leaks. A file with no icon resource yields two
        // zero handles and no icon, which is an ordinary outcome.
        return extracted > 0 && largeIcon != 0
            ? Release(discarded: smallIcon, kept: largeIcon)
            : Release(discarded: largeIcon, kept: smallIcon);
    }

    public bool TryGetIconBitmaps(
        nint icon,
        out nint colorBitmap,
        out nint maskBitmap)
    {
        colorBitmap = 0;
        maskBitmap = 0;
        if (icon == 0 || !NativeMethods.GetIconInfo(icon, out IconInfo info))
        {
            return false;
        }

        colorBitmap = info.ColorBitmap;
        maskBitmap = info.MaskBitmap;
        return true;
    }

    public bool TryGetBitmapSize(nint bitmap, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bitmap == 0)
        {
            return false;
        }

        DeviceIndependentBitmap description = default;
        if (NativeMethods.GetObject(
            bitmap,
            Marshal.SizeOf<DeviceIndependentBitmap>(),
            ref description) == 0)
        {
            return false;
        }

        width = description.Width;
        height = Math.Abs(description.Height);
        return true;
    }

    public bool TryCopyPixels(
        nint bitmap,
        int width,
        int height,
        byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (bitmap == 0 ||
            width <= 0 ||
            height <= 0 ||
            destination.Length < width * height * 4)
        {
            return false;
        }

        nint deviceContext = NativeMethods.CreateCompatibleDC(0);
        if (deviceContext == 0)
        {
            return false;
        }

        try
        {
            BitmapInfo info = new()
            {
                HeaderSize = BitmapInfoHeaderSize,
                Width = width,

                // A negative height asks for a top-down buffer, which is the
                // order every consumer of these pixels expects. Without it the
                // icon arrives upside down.
                Height = -height,
                Planes = 1,
                BitCount = BitCount,
                Compression = UncompressedRgb,
            };

            return NativeMethods.GetDIBits(
                deviceContext,
                bitmap,
                startScanLine: 0,
                (uint)height,
                destination,
                ref info,
                RgbColors) != 0;
        }
        finally
        {
            _ = NativeMethods.DeleteDC(deviceContext);
        }
    }

    public void DeleteBitmap(nint bitmap)
    {
        if (bitmap != 0)
        {
            _ = NativeMethods.DeleteObject(bitmap);
        }
    }

    public void DestroyIcon(nint icon)
    {
        if (icon != 0)
        {
            _ = NativeMethods.DestroyIcon(icon);
        }
    }

    /// <summary>
    /// Releases the icon that is not being used and returns the one that is, so
    /// the unused half of an <c>ExtractIconEx</c> pair never leaks.
    /// </summary>
    /// <param name="discarded">The icon handle being dropped.</param>
    /// <param name="kept">The icon handle being returned.</param>
    /// <returns><paramref name="kept"/>.</returns>
    private static nint Release(nint discarded, nint kept)
    {
        if (discarded != 0)
        {
            _ = NativeMethods.DestroyIcon(discarded);
        }

        return kept;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceIndependentBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int HorizontalResolution;
        public int VerticalResolution;
        public uint UsedColors;
        public uint ImportantColors;
        public uint FirstPaletteEntry;
    }

    private static class NativeMethods
    {
        [DllImport(
            "shell32.dll",
            EntryPoint = "ExtractIconExW",
            CharSet = CharSet.Unicode)]
        internal static extern int ExtractIconEx(
            string executablePath,
            int iconIndex,
            out nint largeIcon,
            out nint smallIcon,
            int iconCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIconInfo(nint icon, out IconInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint icon);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        internal static extern int GetObject(
            nint handle,
            int bufferSize,
            ref DeviceIndependentBitmap buffer);

        [DllImport("gdi32.dll")]
        internal static extern nint CreateCompatibleDC(nint deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint handle);

        [DllImport("gdi32.dll")]
        internal static extern int GetDIBits(
            nint deviceContext,
            nint bitmap,
            uint startScanLine,
            uint scanLines,
            byte[] pixels,
            ref BitmapInfo info,
            uint usage);
    }
}
